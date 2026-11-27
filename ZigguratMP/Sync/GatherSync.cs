using System.IO;
using System.Linq;
using Logic;
using Logic.Fights;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// A fight (or the boss intro) starting in a room pulls every standing player who is outside
    /// into that room, next to the player already inside, so nobody gets locked out by the doors.
    /// </summary>
    internal static class GatherSync
    {
        /// <summary>Room the local player is really standing in (-1 = corridor / none).</summary>
        public static int LocalRoomId { get; private set; } = -1;

        public static void Reset() => LocalRoomId = -1;

        public static void OnLocalRoomEntered(RoomScript room) => LocalRoomId = room?.Room != null ? room.Room.Id : -1;
        public static void OnLocalRoomExit() => LocalRoomId = -1;

        private static bool InCoop => Session.InLobby && Session.Members.Count > 1 && !StageParameters.IsTutorial;
        /// <summary>Each player opts in for being teleported (F8 > Settings); the host always announces.</summary>
        private static bool Enabled => InCoop && Plugin.TeleportToFights.Value;

        // ------------------------------------------------------------------ host

        /// <summary>Where the fight was triggered (the client that walked in), set by RoomSync.</summary>
        public static Vector3? PendingAnchor;

        public static void HostOnFightStarted(RoomScript room, Fight fight)
        {
            if (!Session.IsHost || !InCoop || room?.Room == null || fight == null || fight.FightType == FightTypes.Boss) return;
            Broadcast(room, false);
        }

        public static void HostOnBossCutscene()
        {
            if (!Session.IsHost || !InCoop) return;
            RoomScript room = LocalPlayerAccess.Logic?.CurrentRoom;
            if (room?.Room != null) Broadcast(room, true);
        }

        private static void Broadcast(RoomScript room, bool boss)
        {
            Player p = LocalPlayerAccess.Get();
            Vector3 anchor = LocalRoomId == room.Room.Id && p != null ? p.transform.position : PendingAnchor ?? (p != null ? p.transform.position : room.transform.position);
            PendingAnchor = null;
            BinaryWriter w = Session.Begin(MsgType.Gather);
            w.Write(StageParameters.DungeonLevel);
            w.Write(room.Room.Id);
            w.Write(boss);
            w.Write(anchor.x); w.Write(anchor.y); w.Write(anchor.z);
            Session.SendBuiltToAll(true);
        }

        // ------------------------------------------------------------------ everyone

        public static void OnGather(BinaryReader r)
        {
            int level = r.ReadInt32();
            int roomId = r.ReadInt32();
            bool boss = r.ReadBoolean();
            var anchor = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!Enabled || level != StageParameters.DungeonLevel || LocalRoomId == roomId) return;
            DungeonLogic logic = LocalPlayerAccess.Logic;
            RoomScript room = logic?.Rooms?.FirstOrDefault(x => x?.Room != null && x.Room.Id == roomId);
            if (room == null || logic.IsFighting) return; // already busy in another fight: don't yank
            TeleportInto(room, anchor, boss ? "boss" : "fight");
        }

        /// <summary>Teleport the local player next to <paramref name="anchor"/> inside the room and enter it.</summary>
        public static bool TeleportInto(RoomScript room, Vector3 anchor, string why)
        {
            Player p = LocalPlayerAccess.Get();
            StageScript stage = StageScript.Instance;
            if (p == null || !p.IsAlive || DownedSync.LocalDowned || stage == null || stage.StageState != StageScript.StageStates.Playing) return false;

            Vector3 target = anchor;
            for (int i = 0; i < 4; i++)
            {
                Vector2 off = Random.insideUnitCircle.normalized * 1.6f;
                Vector3? valid = room.GetValidSpawnPoint(anchor + new Vector3(off.x, 0f, off.y), 2.5f);
                if (valid.HasValue) { target = valid.Value; break; }
            }
            p.Controller.Teleport(target + Vector3.up * 0.15f);
            Plugin.Log.LogInfo($"[gather] Teleported into room {room.Room.Id} ({why})");
            LocalPlayerAccess.Logic.EnteredRoom(room); // doors, fight start / boss intro, like walking in
            return true;
        }
    }
}
