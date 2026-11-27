using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Logic;
using Logic.Fights;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Special fight objectives belong to the host (every player has their own copy of the room):
    ///  - Obelisk: client hits are forwarded, destructions mirrored;
    ///  - Sequence: client activations are forwarded, item states mirrored;
    ///  - Scavenge: client pickups count on the host;
    ///  - Overload: items charge when ANY player stands near them.
    /// Objects are matched by position (same seed, same layout on every machine).
    /// </summary>
    internal static class ObjectiveSync
    {
        private static bool applyingRemote;
        private const float MatchRadius = 2f;

        private static T Nearest<T>(Vector3 pos, System.Func<T, bool> ok = null) where T : Component
        {
            T best = null;
            float bestD = MatchRadius * MatchRadius;
            foreach (T c in Object.FindObjectsOfType<T>())
            {
                if (c == null || (ok != null && !ok(c))) continue;
                float d = (c.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private static void WriteV(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ------------------------------------------------------------------ obelisks

        /// <summary>Obelisk.Hurt prefix (client): forward the hit; the local copy still reacts.</summary>
        public static void OnLocalObeliskHurt(Obelisk o, DamageData d)
        {
            if (applyingRemote || !Session.IsClient || o == null || ShotSync.IsReplay(d)) return;
            BinaryWriter w = Session.Begin(MsgType.ObeliskHit);
            w.Write(StageParameters.DungeonLevel);
            WriteV(w, o.transform.position);
            w.Write((byte)d.Damage.Nature);
            w.Write(d.Damage.Damage);
            w.Write(d.Damage.ProcChance);
            w.Write(d.Duration);
            Session.SendBuiltToHost(true);
        }

        public static void OnObeliskHit(BinaryReader r)
        {
            int level = r.ReadInt32();
            Vector3 pos = ReadV(r);
            var nature = (ElementalNature)r.ReadByte();
            float dmg = r.ReadSingle(), proc = r.ReadSingle(), dur = r.ReadSingle();
            if (level != StageParameters.DungeonLevel) return;
            Obelisk o = Nearest<Obelisk>(pos, x => x.IsAlive);
            if (o == null) return;
            applyingRemote = true;
            try { o.Hurt(new DamageData(Team.Player, new ElementalDamage(nature, dmg, proc), dur, null, pos)); }
            finally { applyingRemote = false; }
        }

        /// <summary>Obelisk.Die postfix (host): tell everyone.</summary>
        public static void OnObeliskDied(Obelisk o)
        {
            if (applyingRemote || !Session.IsHost || o == null) return;
            BinaryWriter w = Session.Begin(MsgType.ObeliskDead);
            w.Write(StageParameters.DungeonLevel);
            WriteV(w, o.transform.position);
            Session.SendBuiltToAll(true);
        }

        public static void OnObeliskDead(BinaryReader r)
        {
            int level = r.ReadInt32();
            Vector3 pos = ReadV(r);
            if (level != StageParameters.DungeonLevel) return;
            Obelisk o = Nearest<Obelisk>(pos, x => x.IsAlive);
            if (o == null) return;
            applyingRemote = true;
            try { o.Hurt(new DamageData(Team.Player, new ElementalDamage(ElementalNature.NonElemental, 1e7f), null, pos)); }
            finally { applyingRemote = false; }
        }

        // ------------------------------------------------------------------ sequence items

        /// <summary>SequenceItem.Interact prefix (client).</summary>
        public static void OnLocalSequenceInteract(SequenceItem item)
        {
            if (applyingRemote || !Session.IsClient || item == null) return;
            BinaryWriter w = Session.Begin(MsgType.SequenceActivate);
            w.Write(StageParameters.DungeonLevel);
            WriteV(w, item.transform.position);
            Session.SendBuiltToHost(true);
        }

        public static void OnSequenceActivate(BinaryReader r)
        {
            int level = r.ReadInt32();
            Vector3 pos = ReadV(r);
            Player p = LocalPlayerAccess.Get();
            if (level != StageParameters.DungeonLevel || p == null) return;
            SequenceItem item = Nearest<SequenceItem>(pos, x => x.CanInteract);
            if (item == null) return;
            applyingRemote = true;
            try { item.Interact(p); }
            finally { applyingRemote = false; }
        }

        private static string lastSequenceSig;
        private static float nextSequenceScan;

        /// <summary>Host: push sequence item states when they change.</summary>
        private static void HostTickSequence()
        {
            if (Time.unscaledTime < nextSequenceScan) return;
            nextSequenceScan = Time.unscaledTime + 0.25f;
            DungeonLogic logic = LocalPlayerAccess.Logic;
            if (logic?.CurrentFight == null || logic.CurrentFight.FightType != FightTypes.Sequence) { lastSequenceSig = null; return; }

            SequenceItem[] items = Object.FindObjectsOfType<SequenceItem>();
            var sb = new StringBuilder();
            foreach (SequenceItem i in items) sb.Append((int)i.State);
            string sig = sb.ToString();
            if (sig == lastSequenceSig) return;
            lastSequenceSig = sig;

            BinaryWriter w = Session.Begin(MsgType.SequenceStates);
            w.Write(StageParameters.DungeonLevel);
            w.Write((byte)Mathf.Min(items.Length, 255));
            for (int k = 0; k < items.Length && k < 255; k++)
            {
                WriteV(w, items[k].transform.position);
                w.Write((byte)items[k].State);
            }
            Session.SendBuiltToAll(true);
        }

        public static void OnSequenceStates(BinaryReader r)
        {
            int level = r.ReadInt32();
            int n = r.ReadByte();
            for (int k = 0; k < n; k++)
            {
                Vector3 pos = ReadV(r);
                var state = (SequenceItem.SequenceItemState)r.ReadByte();
                if (level != StageParameters.DungeonLevel) continue;
                SequenceItem item = Nearest<SequenceItem>(pos);
                if (item != null && item.State != state) item.State = state;
            }
        }

        // ------------------------------------------------------------------ scavenge

        /// <summary>PickupScript.BeginPickup postfix (client): a scavenge item counts for the host's fight.</summary>
        public static void OnLocalPickup(PickupScript p)
        {
            if (!Session.IsClient || p == null || p.PickupType != PickupTypes.ScavengeItem) return;
            BinaryWriter w = Session.Begin(MsgType.ScavengePicked);
            w.Write(StageParameters.DungeonLevel);
            WriteV(w, p.transform.position);
            Session.SendBuiltToHost(true);
        }

        private static readonly AccessTools.FieldRef<ScavengeFight, List<GameObject>> PickUpsRef =
            AccessTools.FieldRefAccess<ScavengeFight, List<GameObject>>("pickUps");
        private static readonly System.Reflection.MethodInfo ItemPickedUp = AccessTools.Method(typeof(ScavengeFight), "ItemPickedUp");

        public static void OnScavengePicked(BinaryReader r)
        {
            int level = r.ReadInt32();
            Vector3 pos = ReadV(r);
            if (level != StageParameters.DungeonLevel) return;
            if (!(LocalPlayerAccess.Logic?.CurrentFight is ScavengeFight fight)) return;
            List<GameObject> list = PickUpsRef(fight);
            if (list == null || list.Count == 0) return;

            GameObject best = null;
            float bestD = float.MaxValue;
            foreach (GameObject g in list)
            {
                if (g == null) continue;
                float d = (g.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = g; }
            }
            PickupScript ps = best != null ? best.GetComponent<PickupScript>() : null;
            if (ps == null) return;
            try
            {
                ItemPickedUp?.Invoke(fight, new object[] { ps });
                if (best != null) Object.Destroy(best);
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"scavenge: {e.Message}"); }
        }

        // ------------------------------------------------------------------ overload

        /// <summary>Replaces Vector3.Distance(item, localPlayer) in OverloadItem.Update: nearest player counts.</summary>
        public static float DistanceToNearestPlayer(Vector3 a, Vector3 localPlayer)
        {
            float best = Vector3.Distance(a, localPlayer);
            if (!Session.InLobby) return best;
            foreach (RemotePlayers.GhostView g in RemotePlayers.VisibleGhosts())
            {
                if (g.Downed) continue;
                float d = Vector3.Distance(a, g.Position);
                if (d < best) best = d;
            }
            return best;
        }

        public static void Tick(float dt)
        {
            if (Session.IsHost) HostTickSequence();
        }
    }
}
