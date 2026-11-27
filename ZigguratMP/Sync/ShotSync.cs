using System.Collections.Generic;
using System.IO;
using Logic;
using Milkstone.Utils;
using Steamworks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Damage source of replayed (remote) projectiles. Every Hurt() seeing it is cancelled: the
    /// shooter's own game already dealt (or forwarded) the real damage.
    /// </summary>
    internal sealed class ReplayDamageSource : IDamageSource
    {
        public Character Character { get; set; }
        public bool IsCloseQuarters => false;
        public bool IsTrap => false;
        public float ProcDmgMultiplier => 0f;
        public Vector3 Position { get; set; }
        public void DidHurt(Character damageReceiver, float damage, bool didHit) { }
        public void DidKill(Character damageReceiver) { }
    }

    /// <summary>
    /// Visual replication of player shots: every bullet / beam the local player fires is sent,
    /// other players spawn the same prefab (trails, impacts, explosions) without damage.
    /// </summary>
    internal static class ShotSync
    {
        public static bool IsReplay(DamageData d) => d.DamageSource is ReplayDamageSource;

        private static bool Active => Session.InLobby && LocalPlayerAccess.Get() != null;

        // ------------------------------------------------------------------ definitions & prefabs

        private static readonly Dictionary<string, BulletDefinition> bulletDefs = new Dictionary<string, BulletDefinition>();
        private static readonly Dictionary<string, BeamDefinition> beamDefs = new Dictionary<string, BeamDefinition>();
        private static float nextDefScan;
        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        private static readonly HashSet<string> loading = new HashSet<string>();

        private static T FindDef<T>(Dictionary<string, T> cache, string name) where T : ScriptableObject
        {
            if (cache.TryGetValue(name, out T d) && d != null) return d;
            if (Time.unscaledTime < nextDefScan) return null;
            nextDefScan = Time.unscaledTime + 2f;
            foreach (T def in Resources.FindObjectsOfTypeAll<T>())
                if (def != null) cache[def.name] = def;
            return cache.TryGetValue(name, out d) ? d : null;
        }

        /// <summary>Loaded prefab, or null while the async load runs (that shot is skipped).</summary>
        private static GameObject GetPrefab(string key, AssetReference asset)
        {
            if (prefabs.TryGetValue(key, out GameObject p) && p != null) return p;
            if (asset == null || !asset.RuntimeKeyIsValid() || loading.Contains(key)) return null;
            loading.Add(key);
            asset.LoadAddressable<GameObject>(g => { loading.Remove(key); if (g != null) prefabs[key] = g; });
            return null;
        }

        private static ReplayDamageSource SourceFor(CSteamID from, Vector3 pos) =>
            new ReplayDamageSource { Character = RemotePlayers.GetCharacter(from), Position = pos };

        /// <summary>Remote shots come from the first-person camera area; start them at the remote weapon instead.</summary>
        private static void FromMuzzle(CSteamID from, ref Vector3 pos, ref Quaternion rot)
        {
            Vector3? m = RemotePlayers.GetMuzzle(from);
            if (!m.HasValue || (m.Value - pos).sqrMagnitude > 3f * 3f) return;
            Vector3 aimPoint = pos + rot * Vector3.forward * 30f;
            Vector3 dir = aimPoint - m.Value;
            if (dir.sqrMagnitude < 0.01f) return;
            rot = Quaternion.LookRotation(dir, rot * Vector3.up);
            pos = m.Value;
        }

        private static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        private static Quaternion ReadQ(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static void WriteV(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ------------------------------------------------------------------ bullets

        /// <summary>Bullet.Init postfix.</summary>
        public static void OnBulletInit(Bullet b, BulletDefinition def, ElementalDamage dmg, IDamageSource src, Quaternion attackRot, int bulletIndex)
        {
            if (b == null || def == null || !(src?.Character is Player) || !Active) return;
            BinaryWriter w = Session.Begin(MsgType.ShotFired);
            w.Write(def.name);
            WriteV(w, b.transform.position);
            WriteQ(w, b.transform.rotation);
            WriteQ(w, attackRot);
            w.Write(b.Charge);
            w.Write((byte)Mathf.Clamp(bulletIndex, 0, 255));
            w.Write((byte)dmg.Nature);
            Session.SendBuiltToAll(false);
        }

        public static void OnShot(CSteamID from, BinaryReader r)
        {
            string name = r.ReadString();
            Vector3 pos = ReadV(r);
            Quaternion rot = ReadQ(r), attackRot = ReadQ(r);
            float charge = r.ReadSingle();
            int index = r.ReadByte();
            var nature = (ElementalNature)r.ReadByte();
            if (LocalPlayerAccess.Get() == null) return;

            BulletDefinition def = FindDef(bulletDefs, name);
            if (def == null) return;
            GameObject prefab = GetPrefab("b:" + name, def.BulletPrefab);
            if (prefab == null) return;

            FromMuzzle(from, ref pos, ref rot);
            try
            {
                GameObject go = Object.Instantiate(prefab, pos, rot);
                Bullet bullet = go.GetComponent<Bullet>();
                if (bullet == null) { Object.Destroy(go); return; }
                bullet.Charge = charge;
                bullet.Init(def, new ElementalDamage(nature, 0f), 0f, SourceFor(from, pos), attackRot, Team.Player, index);
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"replay bullet {name}: {e.Message}"); }
        }

        // ------------------------------------------------------------------ weapon fx

        /// <summary>Player.WeaponShoot postfix (once per trigger pull / burst shot).</summary>
        public static void OnLocalWeaponShoot(int attackIndex) => SendWeaponFx(attackIndex, RemotePlayers.FxShoot);

        /// <summary>Weapon charge / cooldown events of the local player's weapon.</summary>
        public static void OnLocalWeaponEvent(Weapon w, int attackIndex, byte kind)
        {
            Player p = LocalPlayerAccess.Get();
            if (p == null || w == null || w.Player != p) return;
            SendWeaponFx(attackIndex, kind);
        }

        private static void SendWeaponFx(int attackIndex, byte kind)
        {
            if (!Active) return;
            BinaryWriter w = Session.Begin(MsgType.WeaponFx);
            w.Write((byte)Mathf.Clamp(attackIndex, 0, 255));
            w.Write(kind);
            Session.SendBuiltToAll(false);
        }

        public static void OnWeaponFx(CSteamID from, BinaryReader r)
        {
            byte idx = r.ReadByte();
            byte kind = r.ReadByte();
            RemotePlayers.OnRemoteFire(from, idx, kind);
        }

        // ------------------------------------------------------------------ beams

        /// <summary>Beam.Init postfix: mark local beams so they report themselves.</summary>
        public static void OnBeamInit(Beam beam, BeamDefinition def, ElementalDamage dps, IDamageSource src)
        {
            if (beam == null || def == null || !(src?.Character is Player) || !Active) return;
            if (beam.GetComponent<LocalBeamReporter>() != null) return;
            LocalBeamReporter rep = beam.gameObject.AddComponent<LocalBeamReporter>();
            rep.DefName = def.name;
            rep.Nature = dps.Nature;
        }

        internal sealed class LocalBeamReporter : MonoBehaviour
        {
            private static int nextId = 1;
            public string DefName;
            public ElementalNature Nature;
            private int id;
            private float nextSend;

            private void Start()
            {
                id = nextId++;
                if (!Session.InLobby) return;
                BinaryWriter w = Session.Begin(MsgType.BeamStart);
                w.Write(id);
                w.Write(DefName);
                w.Write((byte)Nature);
                WriteV(w, transform.position);
                WriteQ(w, transform.rotation);
                Session.SendBuiltToAll(true);
            }

            private void LateUpdate()
            {
                if (!Session.InLobby || Time.unscaledTime < nextSend) return;
                nextSend = Time.unscaledTime + 0.05f;
                BinaryWriter w = Session.Begin(MsgType.BeamUpdate);
                w.Write(id);
                WriteV(w, transform.position);
                WriteQ(w, transform.rotation);
                Session.SendBuiltToAll(false);
            }

            private void OnDestroy()
            {
                if (!Session.InLobby || id == 0) return;
                Session.Begin(MsgType.BeamEnd).Write(id);
                Session.SendBuiltToAll(true);
            }
        }

        private sealed class RemoteBeam
        {
            public GameObject Go;
            public Vector3 Pos;
            public Quaternion Rot;
            public float LastRecv;
        }

        private static readonly Dictionary<ulong, RemoteBeam> beams = new Dictionary<ulong, RemoteBeam>();
        private static ulong BeamKey(CSteamID from, int id) => (from.m_SteamID * 1000003UL) ^ (uint)id;

        public static void OnBeamStart(CSteamID from, BinaryReader r)
        {
            int id = r.ReadInt32();
            string name = r.ReadString();
            var nature = (ElementalNature)r.ReadByte();
            Vector3 pos = ReadV(r);
            Quaternion rot = ReadQ(r);
            if (LocalPlayerAccess.Get() == null) return;

            BeamDefinition def = FindDef(beamDefs, name);
            if (def == null) return;
            GameObject prefab = GetPrefab("m:" + name, def.BeamPrefab);
            if (prefab == null) return;

            ulong key = BeamKey(from, id);
            EndBeam(key);
            FromMuzzle(from, ref pos, ref rot);
            try
            {
                GameObject go = Object.Instantiate(prefab, pos, rot);
                Beam beam = go.GetComponent<Beam>();
                if (beam == null) { Object.Destroy(go); return; }
                beam.Init(def, new ElementalDamage(nature, 0f), SourceFor(from, pos), Team.Player);
                beam.SetEnableDamage(enable: false);
                beams[key] = new RemoteBeam { Go = go, Pos = pos, Rot = rot, LastRecv = Time.unscaledTime };
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"replay beam {name}: {e.Message}"); }
        }

        public static void OnBeamUpdate(CSteamID from, BinaryReader r)
        {
            int id = r.ReadInt32();
            Vector3 pos = ReadV(r);
            Quaternion rot = ReadQ(r);
            if (!beams.TryGetValue(BeamKey(from, id), out RemoteBeam b)) return;
            FromMuzzle(from, ref pos, ref rot);
            b.Pos = pos; b.Rot = rot; b.LastRecv = Time.unscaledTime;
        }

        public static void OnBeamEnd(CSteamID from, BinaryReader r) => EndBeam(BeamKey(from, r.ReadInt32()));

        private static void EndBeam(ulong key)
        {
            if (!beams.TryGetValue(key, out RemoteBeam b)) return;
            if (b.Go != null) Object.Destroy(b.Go);
            beams.Remove(key);
        }

        private static readonly List<ulong> tmp = new List<ulong>();

        public static void Tick(float dt)
        {
            if (beams.Count == 0) return;
            tmp.Clear();
            foreach (KeyValuePair<ulong, RemoteBeam> kv in beams)
            {
                RemoteBeam b = kv.Value;
                if (b.Go == null || Time.unscaledTime - b.LastRecv > 1f) { tmp.Add(kv.Key); continue; }
                float k = 1f - Mathf.Exp(-dt * 20f);
                Transform t = b.Go.transform;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, b.Pos, k), Quaternion.Slerp(t.rotation, b.Rot, k));
            }
            foreach (ulong key in tmp) EndBeam(key);
        }

        public static void Reset()
        {
            foreach (RemoteBeam b in beams.Values) if (b.Go != null) Object.Destroy(b.Go);
            beams.Clear();
        }
    }
}
