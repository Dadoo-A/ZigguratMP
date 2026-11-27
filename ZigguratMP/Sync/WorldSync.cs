using System.IO;
using Logic;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Breakable props (pots, crates, barrels): broken by one player = broken for everyone.
    /// Each player still gets their own loot drop from it (loot stays per player).
    /// </summary>
    internal static class WorldSync
    {
        private static bool applyingRemote;

        /// <summary>BreakableCharacter.Hurt postfix.</summary>
        public static void OnLocalBreak(BreakableCharacter b, DamageData damage)
        {
            if (applyingRemote || b == null || !Session.InLobby || ShotSync.IsReplay(damage)) return;
            if (LocalPlayerAccess.Get() == null || StageParameters.IsTutorial) return;
            BinaryWriter w = Session.Begin(MsgType.BreakableBroken);
            w.Write(StageParameters.DungeonLevel);
            Vector3 p = b.transform.position;
            w.Write(p.x); w.Write(p.y); w.Write(p.z);
            Session.SendBuiltToAll(true);
        }

        public static void OnRemoteBreak(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (level != StageParameters.DungeonLevel || LocalPlayerAccess.Get() == null) return;

            BreakableCharacter best = null;
            float bestD = 0.5f * 0.5f;
            foreach (BreakableCharacter b in Object.FindObjectsOfType<BreakableCharacter>())
            {
                if (b == null || !b.gameObject.activeSelf) continue;
                float d = (b.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best == null) return;
            applyingRemote = true;
            try { best.Hurt(new DamageData(Team.Player, new ElementalDamage(ElementalNature.NonElemental, 1f), null, pos)); }
            catch (System.Exception e) { Plugin.Log.LogDebug($"remote break: {e.Message}"); }
            finally { applyingRemote = false; }
        }
    }
}
