using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Exit portal for the group: any player can step in; the floor ends (host decides) once every
    /// standing player is in, or when the countdown started by the first one runs out.
    /// </summary>
    internal static class PortalSync
    {
        private static readonly HashSet<CSteamID> ready = new HashSet<CSteamID>();
        private static int level = -1;
        private static float firstReadyAt = -1f;
        private static bool sentReady;
        private static bool allow;

        private static bool Active => Session.InLobby && Session.Members.Count > 1 && !StageParameters.IsTutorial;

        public static void Reset()
        {
            ready.Clear();
            firstReadyAt = -1f;
            sentReady = false;
            allow = false;
            level = -1;
        }

        private static void EnsureLevel()
        {
            if (level == StageParameters.DungeonLevel) return;
            Reset();
            level = StageParameters.DungeonLevel;
        }

        /// <summary>PortalScript.Interact prefix: false = keep the player waiting in the portal.</summary>
        public static bool BeforePortalInteract()
        {
            if (!Active || allow) return true;
            EnsureLevel();
            if (!sentReady)
            {
                sentReady = true;
                MarkReady(Session.LocalId);
                Session.Begin(MsgType.PortalReady).Write(StageParameters.DungeonLevel);
                Session.SendBuiltToAll(true);
                Plugin.Log.LogInfo("[portal] Waiting in the portal for the other players");
            }
            return false;
        }

        public static void OnPortalReady(CSteamID from, BinaryReader r)
        {
            int l = r.ReadInt32();
            EnsureLevel();
            if (l != level) return;
            MarkReady(from);
        }

        private static void MarkReady(CSteamID id)
        {
            ready.Add(id);
            if (firstReadyAt < 0f) firstReadyAt = Time.unscaledTime;
        }

        /// <summary>Players that must be in the portal: everyone present and standing.</summary>
        private static List<CSteamID> Required()
        {
            var list = new List<CSteamID>();
            foreach (CSteamID m in Session.Members)
            {
                if (m == Session.LocalId) { if (!DownedSync.LocalDowned && LocalPlayerAccess.Get() != null) list.Add(m); continue; }
                if (RemotePlayers.GetCharacter(m) != null && !DownedSync.IsRemoteDowned(m)) list.Add(m);
            }
            return list;
        }

        private static float Countdown => Mathf.Max(5f, Plugin.PortalCountdown.Value);

        public static void Tick(float dt)
        {
            if (!Active || LocalPlayerAccess.Get() == null) return;
            EnsureLevel();
            if (!Session.IsHost || allow || ready.Count == 0) return;

            bool everyone = Required().All(ready.Contains);
            bool timeout = Time.unscaledTime - firstReadyAt > Countdown;
            if (!everyone && !timeout) return;

            StageScript stage = StageScript.Instance;
            if (stage == null || stage.StageState != StageScript.StageStates.Playing) return;
            allow = true;
            Plugin.Log.LogInfo($"[portal] Leaving the floor ({(everyone ? "everyone is in" : "countdown over")})");
            stage.LevelCleared(); // clients follow through LevelCleared
        }

        private static GUIStyle style;

        public static void DrawHud()
        {
            if (!Active || allow || ready.Count == 0 || level != StageParameters.DungeonLevel || LocalPlayerAccess.Get() == null) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.6f, 0.9f, 1f) } };
            List<string> missing = Required().Where(m => !ready.Contains(m)).Select(m => SteamFriends.GetFriendPersonaName(m)).ToList();
            if (missing.Count == 0) return;
            int left = Mathf.CeilToInt(Countdown - (Time.unscaledTime - firstReadyAt));
            GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 30), $"Portal: waiting for {string.Join(", ", missing)} ({Mathf.Max(0, left)}s)", style);
        }
    }
}
