using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Give an item to the others: look at a weapon / amulet on the ground and press the share key.
    /// It disappears for you and appears at the same spot for the other players.
    /// </summary>
    internal static class ItemShare
    {
        public static string LastInfo { get; private set; }
        private static float infoUntil;

        public static void TryShare()
        {
            Player p = LocalPlayerAccess.Get();
            if (p == null || !Session.InLobby || Session.Members.Count <= 1 || DownedSync.LocalDowned) return;
            PlayerInteractable target = p.CurrentInteractable;
            JObject json = null;
            string label = null;
            if (target is WeaponPickupScript w) { json = w.ToJson(); label = "arme"; }
            else if (target is AmuletPickupScript a) { json = a.ToJson(); label = "amulette"; }
            if (json == null) { Show("Regarde une arme ou une amulette au sol pour la partager"); return; }

            Session.Begin(MsgType.ItemShare).Write(json.ToString(Formatting.None));
            Session.SendBuiltToAll(true);
            Object.Destroy(target.gameObject);
            Plugin.Log.LogInfo($"[share] Gave {json["id"]} to the other players");
            Show($"{label} partagée");
        }

        public static void OnItemShare(CSteamID from, BinaryReader r)
        {
            string text = r.ReadString();
            if (LocalPlayerAccess.Get() == null) return;
            try
            {
                JObject json = JObject.Parse(text);
                int type = (int?)json["type"] ?? -1;
                if (type == 0) WeaponPickupScript.LoadJson(json);
                else if (type == 1)
                {
                    if (json["usesLeft"] == null && json["uses"] != null) json["usesLeft"] = json["uses"]; // game's own key mismatch
                    AmuletPickupScript.LoadJson(json);
                }
                else return;
                string who = SteamFriends.GetFriendPersonaName(from);
                Plugin.Log.LogInfo($"[share] {who} shared {json["id"]}");
                Show($"{who} t'a partagé un objet");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[share] Could not spawn shared item: {e.Message}"); }
        }

        private static void Show(string s) { LastInfo = s; infoUntil = Time.unscaledTime + 3f; }

        private static GUIStyle style;

        public static void DrawHud()
        {
            if (LastInfo == null || Time.unscaledTime > infoUntil) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = Color.cyan } };
            GUI.Label(new Rect(0, Screen.height * 0.72f, Screen.width, 30), LastInfo, style);
        }
    }
}
