using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Logic;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Co-op death: a player whose health hits 0 goes down instead of dying while an ally still
    /// stands. Standing near a downed ally for <see cref="ReviveTime"/> seconds revives them.
    /// When the last player goes down, everyone dies for real (normal game over).
    /// </summary>
    internal static class DownedSync
    {
        public static bool LocalDowned { get; private set; }

        private const float ReviveTime = 3f;
        private const float ReviveRange = 2.6f;
        private const float ReviveHealth = 0.3f;

        private static Player downedPlayer;
        private static float downedAt;
        private static bool allowRealDeath;
        private static readonly HashSet<CSteamID> remoteDowned = new HashSet<CSteamID>();
        private static readonly Dictionary<CSteamID, float> reviveProgress = new Dictionary<CSteamID, float>();
        private static string revivingName;
        private static float revivingProgress;

        private static readonly System.Reflection.MethodInfo PlayerDataDie =
            AccessTools.Method(typeof(PlayerData), "Die", new[] { typeof(IDamageSource) });

        public static bool IsRemoteDowned(CSteamID id) => remoteDowned.Contains(id);

        public static void Reset()
        {
            if (LocalDowned) StandUp(LocalPlayerAccess.Get(), 0f);
            LocalDowned = false;
            downedPlayer = null;
            remoteDowned.Clear();
            reviveProgress.Clear();
            allowRealDeath = false;
        }

        private static bool AnyAllyStanding()
        {
            foreach (CSteamID m in Session.Members)
            {
                if (m == Session.LocalId) continue;
                if (RemotePlayers.GetCharacter(m) != null && !remoteDowned.Contains(m)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ local death

        /// <summary>PlayerData.Die prefix. false = we keep the player alive (downed).</summary>
        public static bool BeforeDie(PlayerData data)
        {
            if (allowRealDeath || !Session.InLobby || StageParameters.IsTutorial) return true;
            Player p = LocalPlayerAccess.Get();
            if (p == null || p.PlayerData != data) return true;
            if (LocalDowned) return false;
            if (!AnyAllyStanding())
            {
                Plugin.Log.LogInfo("[downed] Last player standing died: team wipe");
                Session.Begin(MsgType.Wipe);
                Session.SendBuiltToAll(true);
                return true;
            }
            GoDown(p);
            return false;
        }

        private static void GoDown(Player p)
        {
            LocalDowned = true;
            downedPlayer = p;
            downedAt = Time.time;
            p.PlayerData.Health.Value = 1f;
            if (p.Controller != null) p.Controller.EnableInput = false;
            Plugin.Log.LogInfo("[downed] Local player is down, waiting for a revive");
            SendDowned(true);
        }

        private static void StandUp(Player p, float healthProgress)
        {
            LocalDowned = false;
            downedPlayer = null;
            if (p == null) return;
            if (p.Controller != null) p.Controller.EnableInput = true;
            if (healthProgress > 0f && p.PlayerData.Health.Progress < healthProgress)
                p.PlayerData.Health.Progress = healthProgress;
            SendDowned(false);
        }

        private static void DieForReal(string why)
        {
            Player p = LocalPlayerAccess.Get();
            Plugin.Log.LogInfo($"[downed] Dying for real ({why})");
            if (p != null && p.Controller != null) p.Controller.EnableInput = true;
            LocalDowned = false;
            downedPlayer = null;
            if (p == null || PlayerDataDie == null) return;
            allowRealDeath = true;
            try
            {
                p.PlayerData.Health.Value = 0f;
                PlayerDataDie.Invoke(p.PlayerData, new object[] { null });
            }
            finally { allowRealDeath = false; }
        }

        private static void SendDowned(bool down)
        {
            if (!Session.InLobby) return;
            Session.Begin(MsgType.PlayerDowned).Write(down);
            Session.SendBuiltToAll(true);
        }

        /// <summary>Player.Hurt prefix: downed players take no damage.</summary>
        public static bool BlockHurt(Player p) => LocalDowned && p == downedPlayer;

        /// <summary>Weapon trigger prefix: downed players can't shoot.</summary>
        public static bool BlockShooting => LocalDowned;

        // ------------------------------------------------------------------ network

        public static void OnDowned(CSteamID from, BinaryReader r)
        {
            bool down = r.ReadBoolean();
            if (down) remoteDowned.Add(from); else remoteDowned.Remove(from);
            reviveProgress.Remove(from);
            RemotePlayers.SetDowned(from, down);
            Plugin.Log.LogInfo($"[downed] {SteamFriends.GetFriendPersonaName(from)} is {(down ? "down" : "back up")}");
        }

        public static void OnRevive(CSteamID from, BinaryReader r)
        {
            ulong target = r.ReadUInt64();
            if (target != Session.LocalId.m_SteamID || !LocalDowned) return;
            Plugin.Log.LogInfo($"[downed] Revived by {SteamFriends.GetFriendPersonaName(from)}");
            StandUp(LocalPlayerAccess.Get(), ReviveHealth);
        }

        public static void OnWipe(CSteamID from)
        {
            if (LocalDowned) DieForReal("team wipe");
        }

        // ------------------------------------------------------------------ tick

        public static void Tick(float dt)
        {
            Player p = LocalPlayerAccess.Get();

            if (LocalDowned)
            {
                if (p == null || p != downedPlayer)
                {
                    // New floor / scene: get back up.
                    LocalDowned = false;
                    downedPlayer = null;
                    if (p != null) StandUp(p, ReviveHealth); else SendDowned(false);
                }
                else
                {
                    if (p.PlayerData.Health.Value < 1f) p.PlayerData.Health.Value = 1f;
                    if (p.Controller != null) p.Controller.EnableInput = false;
                    // Ally left / got downed meanwhile: nobody can revive us any more.
                    if (Time.time - downedAt > 1f && !AnyAllyStanding()) DieForReal("no ally standing");
                }
            }

            // Reviving downed allies by standing next to them.
            revivingName = null;
            if (p == null || LocalDowned || !p.IsAlive || remoteDowned.Count == 0) { reviveProgress.Clear(); return; }
            foreach (CSteamID id in remoteDowned)
            {
                Character c = RemotePlayers.GetCharacter(id);
                bool near = c != null && (c.transform.position - p.transform.position).sqrMagnitude < ReviveRange * ReviveRange;
                reviveProgress.TryGetValue(id, out float prog);
                prog = near ? prog + dt : Mathf.Max(0f, prog - dt * 2f);
                if (near)
                {
                    revivingName = SteamFriends.GetFriendPersonaName(id);
                    revivingProgress = prog / ReviveTime;
                }
                if (prog >= ReviveTime)
                {
                    prog = 0f;
                    Session.Begin(MsgType.Revive).Write(id.m_SteamID);
                    Session.SendBuiltToAll(true);
                }
                reviveProgress[id] = prog;
            }
        }

        // ------------------------------------------------------------------ HUD

        private static GUIStyle big;

        public static void DrawHud()
        {
            if (big == null)
                big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 26, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.35f, 0.3f) } };

            if (LocalDowned)
            {
                GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 40), "DOWN — an ally must stay next to you to revive you", big);
            }
            else if (revivingName != null)
            {
                float w = 300f, x = (Screen.width - w) * 0.5f, y = Screen.height * 0.6f;
                GUI.Label(new Rect(0, y - 40, Screen.width, 36), $"Reviving {revivingName}...", big);
                GUI.Box(new Rect(x, y, w, 18), GUIContent.none);
                GUI.Box(new Rect(x, y, w * Mathf.Clamp01(revivingProgress), 18), GUIContent.none);
            }
        }
    }
}
