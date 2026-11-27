using Steamworks;
using UnityEngine;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.UI
{
    /// <summary>Temporary IMGUI menu (F8). Replace with in-game UI later.</summary>
    internal static class MpMenu
    {
        public static bool Visible;
        private static Rect window = new Rect(20f, 20f, 380f, 260f);

        public static void Draw()
        {
            if (!Visible) return;
            window = GUILayout.Window(0x5A4D50, window, DrawWindow, $"{Plugin.Name} {Plugin.Version}");
        }

        private static string lastError;

        private static void DrawWindow(int id)
        {
            try { DrawContent(); }
            catch (System.Exception e)
            {
                if (e.Message != lastError) { lastError = e.Message; Plugin.Log.LogError($"F8 menu: {e}"); }
                GUILayout.Label($"Menu error: {e.Message}");
            }
            GUI.DragWindow();
        }

        private static void DrawContent()
        {
            GUILayout.Label(Session.Status);

            if (!Session.SteamReady)
            {
                GUILayout.Label("Steam not initialised yet.");
                return;
            }
            if (!Session.InLobby)
            {
                if (GUILayout.Button("Host co-op lobby")) Session.Host();
                GUILayout.Label("Friends in a lobby:");
                if (Time.unscaledTime > nextLobbyScan) { nextLobbyScan = Time.unscaledTime + 3f; friendLobbies = AllyHud.FindFriendLobbies(); }
                if (friendLobbies == null || friendLobbies.Count == 0) GUILayout.Label("  (none — or accept a Steam invite)");
                else foreach (AllyHud.FriendLobby fl in friendLobbies)
                    if (GUILayout.Button($"Join {fl.Name}")) Session.Join(fl.Lobby);
                DrawSettings();
                return;
            }

            GUILayout.Label(Session.IsHost ? "Role: HOST" : "Role: CLIENT");
            GUILayout.Label($"Run: {RunSync.LastInfo}");
            GUILayout.Label($"Members ({Session.Members.Count}/4):");
            foreach (CSteamID m in Session.Members)
            {
                string tag = m == Session.HostId ? " [host]" : "";
                int ping = m == Session.LocalId ? -1 : AllyHud.PingMs(m);
                string extra = ping >= 0 ? $"  {ping} ms" : "";
                if (DownedSync.IsRemoteDowned(m) || (m == Session.LocalId && DownedSync.LocalDowned)) extra += "  [DOWN]";
                GUILayout.Label($"  • {SteamFriends.GetFriendPersonaName(m)}{tag}{extra}");
            }
            GUILayout.Label($"Ghosts: {RemotePlayers.Count}   Net enemies: {NetEnemies.Count}");
            GUILayout.Label($"Fight: {FightSync.LastInfo}");
            if (Session.IsHost) GUILayout.Label(NetEnemies.ScalingInfo);

            if (Session.IsClient)
            {
                if (RunSync.HasPendingRun && StageScript.Instance == null)
                {
                    GUILayout.Label("Pick your character + weapons in any mission briefing and press Start, or:");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Join now (my proposed loadout)")) RunSync.JoinNow(true);
                    if (GUILayout.Button("Join as host's")) RunSync.JoinNow(false);
                    GUILayout.EndHorizontal();
                }

                DungeonLogic logic = LocalPlayerAccess.Logic;
                if (logic != null && logic.IsFighting && GUILayout.Button("Unstick: force-end current fight"))
                    FightSync.ForceEndCurrentFight();
            }

            if (Session.IsHost && ReadyRoom.HostWaiting && GUILayout.Button("Start now without waiting")) ReadyRoom.Launch();

            if (Session.IsHost && LocalPlayerAccess.Logic != null && LocalPlayerAccess.Logic.IsFighting)
            {
                GUILayout.Label(BossWatch.Info);
                if (GUILayout.Button("Unstick boss: kill phase minions, next phase")) BossWatch.Unstick("manual");
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Invite friends")) Session.InviteFriends();
            if (GUILayout.Button("Leave")) Session.Leave();
            GUILayout.EndHorizontal();
            DrawSettings();
        }

        private static float nextLobbyScan;
        private static System.Collections.Generic.List<AllyHud.FriendLobby> friendLobbies;
        private static bool showSettings;

        private static void DrawSettings()
        {
            showSettings = GUILayout.Toggle(showSettings, "Settings");
            if (!showSettings) return;
            GUILayout.Label($"Send rate: {Plugin.SendRateHz.Value} Hz");
            Plugin.SendRateHz.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(Plugin.SendRateHz.Value, 10, 60));
            GUILayout.Label($"Smoothing delay: {Plugin.InterpDelay.Value * 1000f:0} ms");
            Plugin.InterpDelay.Value = Mathf.Round(GUILayout.HorizontalSlider(Plugin.InterpDelay.Value, 0.05f, 0.4f) * 100f) / 100f;
            GUILayout.Label($"Enemy HP per extra player: +{Plugin.EnemyHealthPerPlayer.Value * 100f:0}% (host)");
            Plugin.EnemyHealthPerPlayer.Value = Mathf.Round(GUILayout.HorizontalSlider(Plugin.EnemyHealthPerPlayer.Value, 0f, 1.5f) * 20f) / 20f;
            GUILayout.Label($"Boss HP per extra player: +{Plugin.BossHealthPerPlayer.Value * 100f:0}% (host)");
            Plugin.BossHealthPerPlayer.Value = Mathf.Round(GUILayout.HorizontalSlider(Plugin.BossHealthPerPlayer.Value, 0f, 2f) * 20f) / 20f;
        }
    }
}
