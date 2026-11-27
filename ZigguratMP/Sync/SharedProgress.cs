using System.IO;
using Logic;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Shared between players: XP and quest items (boss key, secret mini keys).
    /// Not shared (per player): chest contents, weapons, health/mana drops.
    /// </summary>
    internal static class SharedProgress
    {
        /// <summary>True while applying a remote gain, so it is not re-broadcast.</summary>
        private static bool applyingRemote;

        private static bool IsLocal(PlayerData data) => data != null && data == LocalPlayerAccess.Data;

        // ---------------- XP ----------------

        public static void AfterEarnXP(PlayerData data, uint amount)
        {
            if (applyingRemote || amount == 0 || !Session.InLobby || !IsLocal(data)) return;
            BinaryWriter w = Session.Begin(MsgType.XpShare);
            w.Write(StageParameters.DungeonLevel);
            w.Write(amount);
            Session.SendBuiltToAll(true);
        }

        public static void OnXpShare(BinaryReader r)
        {
            r.ReadInt32(); // level (informational: XP is kept across floors anyway)
            uint amount = r.ReadUInt32();
            PlayerData data = LocalPlayerAccess.Data;
            if (data == null) return;
            applyingRemote = true;
            try { data.EarnXP(amount); }
            finally { applyingRemote = false; }
        }

        // ---------------- Keys ----------------

        public static void AfterPickupKey(PlayerData data, KeyPickupScript key, bool isSecret)
        {
            if (applyingRemote || !Session.InLobby || !IsLocal(data)) return;
            Vector3 pos = key != null ? key.transform.position : Vector3.zero;
            BinaryWriter w = Session.Begin(MsgType.KeyShare);
            w.Write(StageParameters.DungeonLevel);
            w.Write(isSecret);
            w.Write(key != null);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            Session.SendBuiltToAll(true);
            Plugin.Log.LogInfo($"Shared key pickup (secret={isSecret})");
        }

        public static void OnKeyShare(BinaryReader r)
        {
            int level = r.ReadInt32();
            bool isSecret = r.ReadBoolean();
            bool hasPos = r.ReadBoolean();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            PlayerData data = LocalPlayerAccess.Data;
            if (data == null || level != StageParameters.DungeonLevel) return;

            KeyPickupScript local = hasPos ? FindKey(pos, isSecret) : null;
            applyingRemote = true;
            try { data.PickupKey(local, isSecret); }
            finally { applyingRemote = false; }

            if (local != null) Object.Destroy(local.gameObject); // our copy of that key is gone
            Plugin.Log.LogInfo($"Received shared key (secret={isSecret}, local copy {(local != null ? "removed" : "not found")})");
        }

        private static KeyPickupScript FindKey(Vector3 pos, bool isSecret)
        {
            KeyPickupScript best = null;
            float bestDist = 6f * 6f;
            foreach (KeyPickupScript k in Object.FindObjectsOfType<KeyPickupScript>())
            {
                if (k.IsSecret != isSecret) continue;
                float d = (k.transform.position - pos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = k; }
            }
            return best;
        }
    }
}
