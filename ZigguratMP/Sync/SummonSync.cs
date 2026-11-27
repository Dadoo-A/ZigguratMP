using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Logic;
using Logic.Campaign;
using Milkstone.Utils;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>Marks a player-team summon copied from another player (visual + target, no AI).</summary>
    internal sealed class SummonReplica : MonoBehaviour
    {
        public ulong Owner;
        public int Id;
    }

    /// <summary>Marks a local player-team summon already announced to the others.</summary>
    internal sealed class SummonOwned : MonoBehaviour
    {
        public int Id;
    }

    /// <summary>
    /// Player-team entities living only on their owner's machine:
    ///  - summons (Necromancer minions, any Team.Player enemy): copied to every other player as
    ///    puppets driven by the owner's snapshots; enemy AI can target them;
    ///  - amulet turrets: a visual copy (it doesn't shoot; the owner's turret deals the damage);
    ///  - skills: a cast gesture on the body, and their explosions replayed visually (no damage).
    /// </summary>
    internal static class SummonSync
    {
        // ------------------------------------------------------------------ owner side

        private static int nextId = 1;
        private static readonly Dictionary<int, Enemy> owned = new Dictionary<int, Enemy>();
        private static float nextScan, nextSnap;

        private static void OwnerTick()
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.5f;
                foreach (Enemy e in Object.FindObjectsOfType<Enemy>())
                {
                    if (e == null || e.Team != Team.Player || e.Definition == null || !e.IsAlive) continue;
                    if (e.GetComponent<SummonReplica>() != null || e.GetComponent<SummonOwned>() != null || e.GetComponent<NetEnemy>() != null) continue;
                    int id = nextId++;
                    e.gameObject.AddComponent<SummonOwned>().Id = id;
                    owned[id] = e;
                    int capturedId = id;
                    e.OnDie += (en, reason) => OwnerSummonDied(capturedId);
                    BinaryWriter w = Session.Begin(MsgType.SummonSpawn);
                    w.Write(StageParameters.DungeonLevel);
                    w.Write(id);
                    w.Write(e.Definition.FullId);
                    Vector3 p = e.transform.position;
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(e.transform.eulerAngles.y);
                    Session.SendBuiltToAll(true);
                }
            }

            if (owned.Count == 0 || Time.unscaledTime < nextSnap) return;
            nextSnap = Time.unscaledTime + 0.1f;
            BinaryWriter s = Session.Begin(MsgType.SummonSnaps);
            s.Write(StageParameters.DungeonLevel);
            var live = owned.Where(kv => kv.Value != null && kv.Value.IsAlive).Take(30).ToList();
            s.Write((byte)live.Count);
            foreach (KeyValuePair<int, Enemy> kv in live)
            {
                Transform t = kv.Value.transform;
                s.Write(kv.Key);
                s.Write(t.position.x); s.Write(t.position.y); s.Write(t.position.z);
                s.Write(t.eulerAngles.y);
            }
            Session.SendBuiltToAll(false);
            foreach (int dead in owned.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) OwnerSummonDied(dead);
        }

        private static void OwnerSummonDied(int id)
        {
            if (!owned.Remove(id) || !Session.InLobby) return;
            BinaryWriter w = Session.Begin(MsgType.SummonDie);
            w.Write(StageParameters.DungeonLevel);
            w.Write(id);
            Session.SendBuiltToAll(true);
        }

        // ------------------------------------------------------------------ replicas

        private static readonly Dictionary<(ulong, int), Enemy> replicas = new Dictionary<(ulong, int), Enemy>();
        private static int replicaLevel = -1;

        public static bool IsReplica(Enemy e) => e != null && e.GetComponent<SummonReplica>() != null;

        public static void OnSummonSpawn(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            string defId = r.ReadString();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float yaw = r.ReadSingle();
            if (level != StageParameters.DungeonLevel || LocalPlayerAccess.Get() == null) return;
            EnemyDefinition def = ItemManager.GetItem<EnemyDefinition>(defId);
            if (def == null || def.EnemyPrefab == null || !def.EnemyPrefab.RuntimeKeyIsValid()) return;
            EnsureLevel();

            var key = (from.m_SteamID, id);
            def.EnemyPrefab.InstantiateAddressable(pos, Quaternion.Euler(0f, yaw, 0f), go =>
            {
                if (go == null) return;
                if (StageParameters.DungeonLevel != level) { Object.Destroy(go); return; }
                Enemy e = go.GetComponent<Enemy>();
                if (e == null) { Object.Destroy(go); return; }
                go.AddComponent<SummonReplica>().Owner = from.m_SteamID;
                go.GetComponent<SummonReplica>().Id = id;
                NetEnemy ne = go.AddComponent<NetEnemy>();
                ne.IsPuppet = true;
                ne.IsSummonReplica = true;
                ne.Push(pos, yaw);
                e.Init(def, isBoss: false);
                e.Team = Team.Player;
                replicas[key] = e;
                LocalPlayerAccess.Logic?.AddEnemyTarget(e); // enemies may go after it
                Plugin.Log.LogInfo($"[summon] {SteamFriends.GetFriendPersonaName(from)} summoned {defId}");
            });
        }

        public static void OnSummonSnaps(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float yaw = r.ReadSingle();
                if (level != StageParameters.DungeonLevel) continue;
                if (replicas.TryGetValue((from.m_SteamID, id), out Enemy e) && e != null)
                    e.GetComponent<NetEnemy>()?.Push(pos, yaw);
            }
        }

        public static void OnSummonDie(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            var key = (from.m_SteamID, id);
            if (!replicas.TryGetValue(key, out Enemy e)) return;
            replicas.Remove(key);
            RemoveReplica(e);
        }

        private static void RemoveReplica(Enemy e)
        {
            if (e == null) return;
            LocalPlayerAccess.Logic?.RemoveEnemyTarget(e);
            NetEnemy ne = e.GetComponent<NetEnemy>();
            if (ne != null) ne.AllowDeath = true;
            try { if (e.IsAlive) e.Kill(countAsSuicide: true); else Object.Destroy(e.gameObject, 2f); }
            catch { Object.Destroy(e.gameObject); }
        }

        private static void EnsureLevel()
        {
            if (replicaLevel == StageParameters.DungeonLevel) return;
            replicaLevel = StageParameters.DungeonLevel;
            replicas.Clear();
            owned.Clear();
        }

        // ------------------------------------------------------------------ turrets (amulet)

        /// <summary>TurretAmulet's bullet exploded where the turret appears: tell the others.</summary>
        public static void OnLocalTurret(object amuletInstance, Vector3 pos, Vector3 forward)
        {
            if (!Session.InLobby || amuletInstance == null) return;
            var def = Traverse.Create(amuletInstance).Property("Definition").GetValue() as ScriptableObject;
            if (def == null) return;
            float duration = Traverse.Create(def).Field("Power_TurretDurationBase").GetValue<float>();
            BinaryWriter w = Session.Begin(MsgType.TurretSpawn);
            w.Write(StageParameters.DungeonLevel);
            w.Write(def.name);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            forward.y = 0f;
            float yaw = forward.sqrMagnitude > 0.001f ? Quaternion.LookRotation(forward).eulerAngles.y : 0f;
            w.Write(yaw);
            w.Write(duration);
            Session.SendBuiltToAll(true);
        }

        public static void OnTurretSpawn(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            string name = r.ReadString();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float yaw = r.ReadSingle();
            float duration = r.ReadSingle();
            if (level != StageParameters.DungeonLevel || LocalPlayerAccess.Get() == null) return;
            System.Type t = AccessTools.TypeByName("TurretAmulet");
            ScriptableObject def = t == null ? null : Resources.FindObjectsOfTypeAll(t).OfType<ScriptableObject>().FirstOrDefault(d => d.name == name);
            var prefab = def == null ? null : Traverse.Create(def).Field("TurretPrefab").GetValue<UnityEngine.AddressableAssets.AssetReferenceGameObject>();
            if (prefab == null || !prefab.RuntimeKeyIsValid()) return;
            prefab.InstantiateAddressable(pos, Quaternion.Euler(0f, yaw, 0f), go =>
            {
                if (go == null) return;
                // Visual copy only: the owner's turret does the shooting and the damage.
                foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && mb.GetType().Name == "TurretScript") mb.enabled = false;
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                Artifact a = go.GetComponent<Artifact>();
                if (a != null) a.Init(1f, duration, -1f); else Object.Destroy(go, Mathf.Max(1f, duration));
            });
        }

        // ------------------------------------------------------------------ skills

        /// <summary>PlayerData.ActivateSkill postfix (local).</summary>
        public static void OnLocalSkill(bool activated)
        {
            if (!activated || !Session.InLobby) return;
            BinaryWriter w = Session.Begin(MsgType.WeaponFx);
            w.Write((byte)0);
            w.Write(RemotePlayers.FxSkill);
            Session.SendBuiltToAll(false);
        }

        /// <summary>ExplosionDefinition.Explode postfix: replay skill / amulet explosions visually.</summary>
        public static void OnExplode(ExplosionDefinition def, Vector3 pos, Vector3 forward, ElementalDamage damage, IDamageSource src, Team team, Vector3? normal, bool hard)
        {
            if (def == null || !Session.InLobby || applyingExplosion) return;
            if (!(src?.Character is Player) || src is WeaponAttack.AttackDamageSource) return; // weapon shots are replayed as bullets
            string key = ExplosionRegistry.KeyOf(def);
            if (key == null) return;
            BinaryWriter w = Session.Begin(MsgType.ExplosionFx);
            w.Write(StageParameters.DungeonLevel);
            w.Write(key);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            w.Write(forward.x); w.Write(forward.y); w.Write(forward.z);
            w.Write(normal.HasValue);
            Vector3 n = normal ?? Vector3.up;
            w.Write(n.x); w.Write(n.y); w.Write(n.z);
            w.Write(hard);
            w.Write((byte)damage.Nature);
            Session.SendBuiltToAll(false);
        }

        private static bool applyingExplosion;

        public static void OnExplosionFx(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            string name = r.ReadString();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var fwd = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            bool hasNormal = r.ReadBoolean();
            var n = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            bool hard = r.ReadBoolean();
            var nature = (ElementalNature)r.ReadByte();
            if (level != StageParameters.DungeonLevel || LocalPlayerAccess.Get() == null) return;
            ExplosionDefinition def = ExplosionRegistry.Get(name);
            if (def == null) return;
            applyingExplosion = true;
            try
            {
                var src = new ReplayDamageSource { Character = RemotePlayers.GetCharacter(from), Position = pos };
                def.Explode(pos, fwd, new ElementalDamage(nature, 0f), 0f, src, Team.Player, hasNormal ? n : (Vector3?)null, hard);
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"explosion fx: {e.Message}"); }
            finally { applyingExplosion = false; }
        }

        // ------------------------------------------------------------------ lifecycle

        public static void Tick(float dt)
        {
            if (!Session.InLobby || Session.Members.Count <= 1 || LocalPlayerAccess.Get() == null) return;
            EnsureLevel();
            OwnerTick();
        }

        public static void Reset()
        {
            foreach (Enemy e in replicas.Values) if (e != null) Object.Destroy(e.gameObject);
            replicas.Clear();
            owned.Clear();
            replicaLevel = -1;
        }
    }

    /// <summary>
    /// ExplosionDefinition is plain data embedded in assets (projectiles, skills, amulets), not a Unity
    /// object: identify it as "AssetType/assetName/field" by scanning ScriptableObjects once.
    /// </summary>
    internal static class ExplosionRegistry
    {
        private static readonly Dictionary<ExplosionDefinition, string> keys = new Dictionary<ExplosionDefinition, string>();
        private static readonly Dictionary<string, ExplosionDefinition> defs = new Dictionary<string, ExplosionDefinition>();
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo[]> fieldCache = new Dictionary<System.Type, System.Reflection.FieldInfo[]>();
        private static float nextScan = -1f;

        private static void Scan()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 10f;
            foreach (ScriptableObject so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                if (so == null) continue;
                System.Type t = so.GetType();
                if (!fieldCache.TryGetValue(t, out System.Reflection.FieldInfo[] fields))
                {
                    fields = t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                        .Where(f => f.FieldType == typeof(ExplosionDefinition)).ToArray();
                    fieldCache[t] = fields;
                }
                foreach (System.Reflection.FieldInfo f in fields)
                {
                    if (!(f.GetValue(so) is ExplosionDefinition d)) continue;
                    string key = t.Name + "/" + so.name + "/" + f.Name;
                    if (!keys.ContainsKey(d)) keys[d] = key;
                    defs[key] = d;
                }
            }
        }

        public static string KeyOf(ExplosionDefinition d)
        {
            if (keys.TryGetValue(d, out string k)) return k;
            Scan();
            return keys.TryGetValue(d, out k) ? k : null;
        }

        public static ExplosionDefinition Get(string key)
        {
            if (defs.TryGetValue(key, out ExplosionDefinition d)) return d;
            Scan();
            return defs.TryGetValue(key, out d) ? d : null;
        }
    }
}
