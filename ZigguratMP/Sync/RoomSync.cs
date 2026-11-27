using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Logic;
using Logic.Fights;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Rooms behave as if every player were the host:
    ///  - any player entering a room reveals it on everyone's map;
    ///  - a client entering a combat room makes the host start that fight (host owns enemies).
    /// </summary>
    internal static class RoomSync
    {
        private static readonly HashSet<int> pendingFights = new HashSet<int>();
        private static readonly Dictionary<int, CSteamID> pendingBy = new Dictionary<int, CSteamID>();
        private static int pendingLevel = -1;

        public static void Reset()
        {
            pendingFights.Clear();
            pendingLevel = -1;
        }

        private static RoomScript FindRoom(DungeonLogic logic, int id) =>
            logic?.Rooms?.FirstOrDefault(r => r != null && r.Room != null && r.Room.Id == id);

        /// <summary>RoomScript.PlayerEntered postfix (local player only).</summary>
        public static void OnLocalRoomEntered(RoomScript room)
        {
            if (!Session.InLobby || room?.Room == null || StageParameters.IsTutorial) return;
            BinaryWriter w = Session.Begin(MsgType.RoomEnter);
            w.Write(StageParameters.DungeonLevel);
            w.Write(room.Room.Id);
            Session.SendBuiltToAll(true);
        }

        public static void OnRemoteRoomEnter(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            if (level != StageParameters.DungeonLevel) return;
            DungeonLogic logic = LocalPlayerAccess.Logic;
            RoomScript room = FindRoom(logic, id);
            if (room == null) return;

            if (!room.IsVisited)
            {
                room.SetVisited();
                logic.RefreshRoomVisibility();
            }

            if (Session.IsHost)
            {
                if (pendingLevel != level) { pendingFights.Clear(); pendingLevel = level; }
                pendingFights.Add(id);
                pendingBy[id] = from;
            }
        }

        /// <summary>Host: start fights in rooms a client walked into (when not busy with another fight).</summary>
        public static void Tick()
        {
            if (!Session.IsHost || pendingFights.Count == 0) return;
            DungeonLogic logic = LocalPlayerAccess.Logic;
            if (logic == null || pendingLevel != StageParameters.DungeonLevel) { pendingFights.Clear(); return; }
            if (logic.IsFighting) return; // one fight at a time (game limitation); retried afterwards

            foreach (int id in pendingFights.ToList())
            {
                pendingFights.Remove(id);
                RoomScript room = FindRoom(logic, id);
                if (room == null || room.RoomState != RoomScript.RoomStates.Idle) continue;
                RoomType type = room.Room.Type;
                pendingBy.TryGetValue(id, out CSteamID by);
                Vector3? anchor = RemotePlayers.GetPosition(by);

                // Co-op gathering: the host joins the client inside the room, which starts the fight
                // (or the boss intro) exactly as if the host had walked in.
                bool combat = type == RoomType.Boss || type == RoomType.Gauntlet || type.IsCombatRoom();
                if (combat && Plugin.TeleportToFights.Value && anchor.HasValue && GatherSync.LocalRoomId != id)
                {
                    GatherSync.PendingAnchor = anchor;
                    if (GatherSync.TeleportInto(room, anchor.Value, $"client entered room {id}")) return;
                    GatherSync.PendingAnchor = null;
                }

                if (type == RoomType.Boss) continue; // without gathering the boss intro stays the host's trigger
                if (type == RoomType.Gauntlet)
                {
                    Plugin.Log.LogInfo($"[host] Client entered gauntlet room {id}: starting it");
                    room.StartGauntletFight();
                    return;
                }
                if (type.IsCombatRoom())
                {
                    Plugin.Log.LogInfo($"[host] Client entered room {id}: starting fight");
                    GatherSync.PendingAnchor = anchor;
                    room.StartFight();
                    return;
                }
            }
        }

        private static readonly System.Reflection.MethodInfo ClearRoomMethod = AccessTools.Method(typeof(RoomScript), "ClearRoom");

        /// <summary>Client: host cleared a room this client never fought in. Mark it clear (map icon, no fight on entry).</summary>
        public static void ClientMarkCleared(int roomId)
        {
            DungeonLogic logic = LocalPlayerAccess.Logic;
            RoomScript room = FindRoom(logic, roomId);
            if (room == null || room.RoomState != RoomScript.RoomStates.Idle) return;
            try { ClearRoomMethod?.Invoke(room, null); }
            catch (System.Exception e) { Plugin.Log.LogDebug($"ClearRoom {roomId}: {e.Message}"); }
        }
    }

    /// <summary>
    /// Host safety net for boss stage transitions (shielded phase waiting for its minions to die).
    /// If a minion failed to spawn or its death was missed, the boss would stay shielded forever.
    /// </summary>
    internal static class BossWatch
    {
        private static Enemy boss;
        private static int stage = -1;
        private static float since = -1f, noMinionsSince = -1f;

        private const float NoMinionGrace = 20f;
        private const float HardCap = 100f;

        private static readonly System.Reflection.MethodInfo StartNextStage = AccessTools.Method(typeof(Enemy), "StartNextStage");

        public static string Info { get; private set; } = "-";

        private static Enemy CurrentBoss(out Fight fight)
        {
            fight = LocalPlayerAccess.Logic?.CurrentFight;
            if (fight == null || fight.FightType != FightTypes.Boss) return null;
            return fight.EnemiesAlive?.FirstOrDefault(e => e != null && e.IsBoss);
        }

        private static List<Enemy> Minions(Fight fight, Enemy b) =>
            fight.EnemiesAlive.Where(e => e != null && e != b && e.IsAlive && e.IsSpawn && e.ParentEnemy == b).ToList();

        public static void Tick()
        {
            if (!Session.IsHost) return;
            Enemy b = CurrentBoss(out Fight fight);
            if (b == null || !b.IsInStageTransition)
            {
                if (b != boss || (b != null && !b.IsInStageTransition)) { since = -1f; noMinionsSince = -1f; }
                boss = b;
                Info = b != null ? $"Boss {b.Definition?.Id} stage {b.CurrentStage}" : "-";
                return;
            }

            float now = Time.time;
            if (boss != b || stage != b.CurrentStage || since < 0f)
            {
                boss = b; stage = b.CurrentStage; since = now; noMinionsSince = -1f;
                Plugin.Log.LogInfo($"[host] Boss {b.Definition?.Id} entered stage transition {stage} (shielded={b.IsInShieldedStageTransition}, movement={b.MovementState})");
            }

            int alive = Minions(fight, b).Count;
            if (alive > 0) noMinionsSince = -1f;
            else if (noMinionsSince < 0f) noMinionsSince = now;

            Info = $"Boss transition {stage}: {now - since:0}s, minions alive {alive}, movement {b.MovementState}";
            bool stuck = (noMinionsSince >= 0f && now - noMinionsSince > NoMinionGrace) || now - since > HardCap;
            if (stuck) Unstick($"auto: {Info}");
        }

        /// <summary>Kill the transition minions and move the boss to its next stage.</summary>
        public static void Unstick(string why)
        {
            Enemy b = CurrentBoss(out Fight fight);
            if (b == null) return;
            Plugin.Log.LogWarning($"[host] Unsticking boss {b.Definition?.Id} ({why})");
            foreach (Enemy m in Minions(fight, b)) m.Kill(countAsSuicide: true);
            if (b.IsInStageTransition && StartNextStage != null) StartNextStage.Invoke(b, null);
            since = -1f; noMinionsSince = -1f;
        }
    }
}
