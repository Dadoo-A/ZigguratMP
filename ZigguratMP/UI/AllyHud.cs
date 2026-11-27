using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.UI
{
    /// <summary>Allies on screen: health bar under the name, arrow at the screen edge when off-screen, ping.</summary>
    internal static class AllyHud
    {
        // ------------------------------------------------------------------ ping

        private static float pingTimer;
        private static readonly Dictionary<CSteamID, float> rtt = new Dictionary<CSteamID, float>();

        public static int PingMs(CSteamID id) => rtt.TryGetValue(id, out float s) ? Mathf.RoundToInt(s * 1000f) : -1;

        public static void Tick(float dt)
        {
            pingTimer -= dt;
            if (pingTimer > 0f) return;
            pingTimer = 2f;
            Session.Begin(MsgType.Ping).Write(Time.unscaledTime);
            Session.SendBuiltToAll(false);
        }

        public static void OnPing(CSteamID from, BinaryReader r)
        {
            float t = r.ReadSingle();
            Session.Begin(MsgType.Pong).Write(t);
            Session.SendBuiltTo(from, false);
        }

        public static void OnPong(CSteamID from, BinaryReader r)
        {
            float sample = Time.unscaledTime - r.ReadSingle();
            if (sample < 0f || sample > 5f) return;
            rtt[from] = rtt.TryGetValue(from, out float old) ? Mathf.Lerp(old, sample, 0.3f) : sample;
        }

        public static void Reset() => rtt.Clear();

        // ------------------------------------------------------------------ drawing

        private static Texture2D white;

        private static void Bar(Rect r, float fill, Color c)
        {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(r, white);
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * Mathf.Clamp01(fill), r.height - 2), white);
            GUI.color = old;
        }

        public static void DrawHealthBar(Vector2 center, float health, bool downed)
        {
            Bar(new Rect(center.x - 40f, center.y, 80f, 7f), downed ? 1f : health,
                downed ? new Color(0.9f, 0.2f, 0.2f, 0.9f) : Color.Lerp(new Color(0.9f, 0.2f, 0.2f), new Color(0.3f, 0.9f, 0.3f), health));
        }

        private static GUIStyle arrow;

        /// <summary>Ally behind / off-screen: name + distance pinned to the screen edge.</summary>
        public static void DrawOffscreen(Camera cam, Vector3 worldPos, string name, bool downed)
        {
            Vector3 sp = cam.WorldToScreenPoint(worldPos);
            bool behind = sp.z < 0f;
            if (!behind && sp.x > 0 && sp.x < Screen.width && sp.y > 0 && sp.y < Screen.height) return;

            if (behind) { sp.x = Screen.width - sp.x; sp.y = Screen.height - sp.y; }
            Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector2 d = new Vector2(sp.x, sp.y) - c;
            if (behind && d.sqrMagnitude < 1f) d = Vector2.down;
            float k = Mathf.Min((c.x - 60f) / Mathf.Max(1f, Mathf.Abs(d.x)), (c.y - 40f) / Mathf.Max(1f, Mathf.Abs(d.y)));
            Vector2 p = c + d * k;

            if (arrow == null) arrow = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            arrow.normal.textColor = downed ? new Color(1f, 0.35f, 0.3f) : Color.cyan;
            float dist = Vector3.Distance(cam.transform.position, worldPos);
            GUI.Box(new Rect(p.x - 60f, Screen.height - p.y - 14f, 120f, 28f), $"{(downed ? "! " : "")}{name} {dist:0}m", arrow);
        }

        // ------------------------------------------------------------------ friends' lobbies

        public struct FriendLobby { public string Name; public CSteamID Lobby; }

        /// <summary>Friends currently in a Ziggurat 2 lobby (friends-only lobbies don't show in Steam's lobby search).</summary>
        public static List<FriendLobby> FindFriendLobbies()
        {
            var list = new List<FriendLobby>();
            if (!Session.SteamReady) return list;
            AppId_t app = SteamUtils.GetAppID();
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                CSteamID f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(f, out FriendGameInfo_t info)) continue;
                if (info.m_gameID.AppID() != app || !info.m_steamIDLobby.IsValid() || info.m_steamIDLobby == Session.LobbyId) continue;
                list.Add(new FriendLobby { Name = SteamFriends.GetFriendPersonaName(f), Lobby = info.m_steamIDLobby });
            }
            return list;
        }
    }
}
