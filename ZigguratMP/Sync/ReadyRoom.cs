using System;
using System.Collections.Generic;
using System.IO;
using Logic;
using Logic.Campaign;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Character select together: everyone picks character + gear in the game's own briefing
    /// screen and presses Start. Each pick is shown to everybody; the host's run launches once
    /// the host and every client have picked (host can force it from F8).
    /// </summary>
    internal static class ReadyRoom
    {
        /// <summary>Picked character id per player (shown in the overlay).</summary>
        private static readonly Dictionary<CSteamID, string> picks = new Dictionary<CSteamID, string>();

        private static Action hostLaunch;   // host: replays the blocked mission start
        private static bool launching;

        public static bool HostWaiting => hostLaunch != null;
        public static bool HasPicks => picks.Count > 0;

        public static void Reset()
        {
            picks.Clear();
            hostLaunch = null;
        }

        private static string NameOf(string characterId)
        {
            if (string.IsNullOrEmpty(characterId)) return "?";
            try
            {
                string t = ItemManager.GetItem<CharacterDefinition>(characterId)?.Title;
                if (!string.IsNullOrEmpty(t)) return t;
            }
            catch { }
            return characterId.Replace("Character", "");
        }

        // ------------------------------------------------------------------ host

        /// <summary>
        /// Host pressed Start in a briefing. With other players in the lobby, hold the start until
        /// they have all picked. Returns true to let the game start now.
        /// </summary>
        public static bool HostBeforeStart(Action launch, MissionData mission)
        {
            if (launching || !Session.IsHost || Session.Members.Count <= 1 || mission == null || mission.Type == MissionTypes.Tutorial) return true;

            string ch = mission.Character?.FullId ?? "";
            picks[Session.LocalId] = ch;
            hostLaunch = launch;
            Session.Begin(MsgType.CharacterPick).Write(ch);
            Session.SendBuiltToAll(true);
            Plugin.Log.LogInfo($"[host] Picked {ch}, waiting for the other players to pick");
            if (AllClientsPicked()) Launch();
            return false;
        }

        private static bool AllClientsPicked()
        {
            foreach (CSteamID m in Session.Members)
                if (m != Session.LocalId && !picks.ContainsKey(m)) return false;
            return true;
        }

        /// <summary>Host: start now (all picked, or forced from F8).</summary>
        public static void Launch()
        {
            if (hostLaunch == null) return;
            Action a = hostLaunch;
            hostLaunch = null;
            Plugin.Log.LogInfo("[host] Everyone picked, starting the run");
            launching = true;
            try { a(); }
            catch (Exception e) { Plugin.Log.LogError($"[host] Run start failed: {e}"); }
            finally { launching = false; }
        }

        public static void Tick()
        {
            if (Session.IsHost && hostLaunch != null && AllClientsPicked()) Launch();
        }

        // ------------------------------------------------------------------ client

        /// <summary>Client pressed Start before the host launched: announce the pick.</summary>
        public static void ClientPicked(string characterId)
        {
            picks[Session.LocalId] = characterId ?? "";
            Session.Begin(MsgType.CharacterPick).Write(characterId ?? "");
            Session.SendBuiltToAll(true);
            Plugin.Log.LogInfo($"[client] Picked {characterId}, waiting for the host");
        }

        // ------------------------------------------------------------------ network

        public static void OnPick(CSteamID from, BinaryReader r)
        {
            string ch = r.ReadString();
            picks[from] = ch;
            Plugin.Log.LogInfo($"[lobby] {SteamFriends.GetFriendPersonaName(from)} picked {ch}");
        }

        /// <summary>The run started: picks are consumed.</summary>
        public static void OnRunStarted() => picks.Clear();

        // ------------------------------------------------------------------ overlay

        private static GUIStyle box, title, text;

        public static void DrawOverlay()
        {
            if (!Session.InLobby || picks.Count == 0 || StageScript.Instance != null) return;
            if (box == null)
            {
                box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, padding = new RectOffset(12, 12, 10, 10), richText = true };
                title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.cyan } };
                text = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = true, wordWrap = true };
            }

            var lines = new System.Text.StringBuilder();
            foreach (CSteamID m in Session.Members)
            {
                string name = SteamFriends.GetFriendPersonaName(m) + (m == Session.HostId ? " (host)" : "");
                lines.AppendLine(picks.TryGetValue(m, out string ch)
                    ? $"<color=#7CFC7C>✔</color>  {name} — <b>{NameOf(ch)}</b>"
                    : $"<color=#FFB347>…</color>  {name} — en train de choisir");
            }
            bool iPicked = picks.ContainsKey(Session.LocalId);
            string hint = iPicked
                ? (Session.IsHost ? "La partie se lance quand tout le monde a choisi (F8 : lancer sans attendre)." : "Prêt. La partie se lance quand l'host a choisi.")
                : "Ouvre une mission, choisis ton perso et tes armes, puis Start.";

            float w = 480f, h = 90f + 24f * Session.Members.Count;
            var r = new Rect((Screen.width - w) * 0.5f, 20f, w, h);
            GUI.Box(r, GUIContent.none, box);
            GUI.Label(new Rect(r.x + 12, r.y + 6, w - 24, 26), "Co-op : choix des personnages", title);
            GUI.Label(new Rect(r.x + 12, r.y + 34, w - 24, h - 40), lines + "<i>" + hint + "</i>", text);
        }
    }
}
