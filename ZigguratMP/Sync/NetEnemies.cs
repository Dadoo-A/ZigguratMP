using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Logic;
using Logic.Campaign;
using Milkstone.Logic;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>Per-enemy network state. Host: id only. Client: snapshot buffer for the puppet.</summary>
    internal sealed class NetEnemy : MonoBehaviour
    {
        private const float InterpDelay = 0.15f;

        public int Id;
        public bool IsPuppet;
        /// <summary>Copy of another player's summon (puppet on host and clients alike).</summary>
        public bool IsSummonReplica;
        /// <summary>Client: our last damage source on this puppet (kill credit / on-kill perks).</summary>
        public IDamageSource LastLocalSource;
        public bool AllowDeath;
        public bool IsSpawnFlag;

        private struct Snap { public float T; public Vector3 P; public float Yaw; }
        private readonly List<Snap> snaps = new List<Snap>(6);

        public void Push(Vector3 p, float yaw)
        {
            snaps.Add(new Snap { T = Time.time, P = p, Yaw = yaw });
            if (snaps.Count > 6) snaps.RemoveAt(0);
        }

        public bool HasSnapshot => snaps.Count > 0;

        public void Sample(out Vector3 pos, out float yaw, out Vector3 vel)
        {
            float t = Time.time - InterpDelay;
            for (int i = snaps.Count - 1; i > 0; i--)
            {
                Snap a = snaps[i - 1], b = snaps[i];
                if (t >= a.T)
                {
                    float span = Mathf.Max(0.0001f, b.T - a.T);
                    float k = Mathf.Clamp01((t - a.T) / span);
                    pos = Vector3.Lerp(a.P, b.P, k);
                    yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, k);
                    vel = (b.P - a.P) / span;
                    return;
                }
            }
            pos = snaps[0].P; yaw = snaps[0].Yaw; vel = Vector3.zero;
        }
    }

    /// <summary>
    /// Host-authoritative enemies.
    /// Host: every enemy gets a net id; spawn / 10 Hz snapshots / attacks / deaths are broadcast.
    /// Client: never spawns enemies itself. Network spawns become puppets: no AI, position from
    /// host, attacks replayed locally (so bullets you see are the bullets that hurt you), damage
    /// you deal is forwarded to the host, death comes from the host.
    /// </summary>
    internal static class NetEnemies
    {
        private const float SnapshotRate = 10f;
        private const int SnapshotsPerPacket = 40;

        private static readonly Dictionary<int, Enemy> enemies = new Dictionary<int, Enemy>();
        private static readonly HashSet<int> pendingDeaths = new HashSet<int>();
        private static int nextId = 1;
        private static float snapshotTimer;
        private static int floorLevel = -1;

        /// <summary>Client: true while we spawn a puppet ourselves (lets SpawnEnemy through).</summary>
        public static bool AllowLocalSpawn;

        public static int Count => enemies.Count;

        private static readonly AccessTools.FieldRef<Enemy, Enemy.EnemyMovementState> MovementStateRef =
            AccessTools.FieldRefAccess<Enemy, Enemy.EnemyMovementState>("movementState");

        public static void Reset()
        {
            enemies.Clear();
            pendingDeaths.Clear();
            nextId = 1;
            floorLevel = -1;
        }

        /// <summary>New floor generated: old enemies are gone.</summary>
        public static void OnFloorGenerated()
        {
            enemies.Clear();
            pendingDeaths.Clear();
            bossPuppet = null;
            pendingBossCallbacks.Clear();
            pendingBossRoom = null;
            floorLevel = StageParameters.DungeonLevel;
        }

        public static bool IsPuppet(Enemy e, out NetEnemy ne)
        {
            ne = null;
            if (e == null || !Session.InLobby) return false;
            ne = e.GetComponent<NetEnemy>();
            return ne != null && ne.IsPuppet && (Session.IsClient || ne.IsSummonReplica);
        }

        // =====================================================================
        // Host
        // =====================================================================

        public static void HostOnEnemySpawned(Enemy e, EnemyDefinition def, bool isBoss)
        {
            if (!Session.IsHost || e == null || def == null) return;

            ScaleForPlayerCount(e, isBoss);

            var ne = e.gameObject.AddComponent<NetEnemy>();
            ne.Id = nextId++;
            enemies[ne.Id] = e;
            int id = ne.Id;
            e.OnDie += (en, reason) => HostOnEnemyDie(id, reason);

            Transform t = e.transform;
            BinaryWriter w = Session.Begin(MsgType.EnemySpawn);
            w.Write(StageParameters.DungeonLevel);
            w.Write(id);
            w.Write(def.FullId);
            WriteVec(w, t.position);
            w.Write(t.eulerAngles.y);
            w.Write(isBoss);
            w.Write(e.IsSpawn);
            w.Write((int)e.Data.ChampionType);
            Session.SendBuiltToAll(true);
        }

        /// <summary>Host: more players, tougher enemies (health only; everything else stays vanilla).</summary>
        private static void ScaleForPlayerCount(Enemy e, bool isBoss)
        {
            int extra = Mathf.Clamp(Session.Members.Count, 1, 4) - 1;
            if (extra <= 0 || e.Data == null) return;
            float mult = 1f + extra * (isBoss ? Plugin.BossHealthPerPlayer.Value : Plugin.EnemyHealthPerPlayer.Value);
            if (mult > 1.001f) e.Data.Health.Multiply(mult);
        }

        public static string ScalingInfo
        {
            get
            {
                int extra = Mathf.Clamp(Session.Members.Count, 1, 4) - 1;
                return $"Enemy HP x{1f + extra * Plugin.EnemyHealthPerPlayer.Value:0.##}, boss HP x{1f + extra * Plugin.BossHealthPerPlayer.Value:0.##}";
            }
        }

        private static void HostOnEnemyDie(int id, DeathReason reason)
        {
            enemies.Remove(id);
            if (!Session.IsHost) return;
            lastHitter.TryGetValue(id, out ulong killer);
            lastHitter.Remove(id);
            BinaryWriter w = Session.Begin(MsgType.EnemyDie);
            w.Write(StageParameters.DungeonLevel);
            w.Write(id);
            w.Write((int)reason);
            w.Write(killer);
            Session.SendBuiltToAll(true);
        }

        public static void HostOnEnemyAttack(Enemy e, EnemyAttackDefinition.EnemyAttack attack, Character target)
        {
            if (!Session.IsHost || e == null) return;
            NetEnemy ne = e.GetComponent<NetEnemy>();
            if (ne == null) return;
            int idx = e.Attacks != null ? e.Attacks.IndexOf(attack) : -1;
            if (idx < 0) return;

            ulong targetId = 0;
            if (target is Player) targetId = Session.LocalId.m_SteamID;
            else if (target is RemotePlayerCharacter rp) targetId = rp.SteamId.m_SteamID;

            BinaryWriter w = Session.Begin(MsgType.EnemyAttack);
            w.Write(StageParameters.DungeonLevel);
            w.Write(ne.Id);
            w.Write(idx);
            w.Write(targetId);
            Session.SendBuiltToAll(true);
        }

        private static readonly List<int> deadIds = new List<int>();

        private static void HostSendSnapshots()
        {
            deadIds.Clear();
            int count = 0;
            BinaryWriter w = null;
            int countPos = 0;
            foreach (KeyValuePair<int, Enemy> kv in enemies)
            {
                Enemy e = kv.Value;
                if (e == null) { deadIds.Add(kv.Key); continue; }
                if (e.State == Enemy.EnemyState.Dead) continue;

                if (w == null)
                {
                    w = Session.Begin(MsgType.EnemySnapshots);
                    w.Write(StageParameters.DungeonLevel);
                    countPos = Session.BuiltLength;
                    w.Write((byte)0); // patched below
                    count = 0;
                }
                Transform t = e.transform;
                w.Write(kv.Key);
                WriteVec(w, t.position);
                w.Write(t.eulerAngles.y);
                w.Write(e.Data != null ? e.Data.Health.Progress : 1f);
                w.Write((byte)e.State);
                w.Write((byte)(Mathf.Min(e.CurrentStage, 127) | (e.IsInStageTransition ? 0x80 : 0)));
                w.Write(StatusMask(e));
                count++;

                if (count == SnapshotsPerPacket)
                {
                    PatchCountAndSend(w, countPos, count);
                    w = null;
                }
            }
            if (w != null && count > 0) PatchCountAndSend(w, countPos, count);
            foreach (int id in deadIds) enemies.Remove(id);
        }

        private static void PatchCountAndSend(BinaryWriter w, int countPos, int count)
        {
            w.Flush();
            Stream s = w.BaseStream;
            long end = s.Position;
            s.Position = countPos;
            s.WriteByte((byte)count);
            s.Position = end;
            Session.SendBuiltToAll(false);
        }

        public static void OnHurtRequest(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            var nature = (ElementalNature)r.ReadInt32();
            float dmg = r.ReadSingle();
            float proc = r.ReadSingle();
            float duration = r.ReadSingle();
            Vector3 src = ReadVec(r);
            if (level != StageParameters.DungeonLevel) return;
            if (!enemies.TryGetValue(id, out Enemy e) || e == null || !e.IsAlive) return;

            var data = new DamageData(Team.Player, new ElementalDamage(nature, dmg, proc), duration, NetPlayerSource.For(from, src), src);
            lastHitter[id] = from.m_SteamID;
            e.Hurt(data);
        }

        /// <summary>Host: who hit each enemy last (kill credit), by Steam id.</summary>
        private static readonly Dictionary<int, ulong> lastHitter = new Dictionary<int, ulong>();

        public static void HostNoteLocalHit(Enemy e)
        {
            if (!Session.IsHost || e == null) return;
            NetEnemy ne = e.GetComponent<NetEnemy>();
            if (ne != null) lastHitter[ne.Id] = Session.LocalId.m_SteamID;
        }

        // ---------------------------------------------------------------- client skill effects -> host

        public static void ClientForwardImpulse(NetEnemy ne, Vector3 impulse, bool knockback)
        {
            BinaryWriter w = Session.Begin(MsgType.EnemyImpulse);
            w.Write(StageParameters.DungeonLevel);
            w.Write(ne.Id);
            WriteVec(w, impulse);
            w.Write(knockback);
            Session.SendBuiltToHost(false);
        }

        public static void OnImpulseRequest(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            Vector3 impulse = ReadVec(r);
            bool knockback = r.ReadBoolean();
            if (level != StageParameters.DungeonLevel || !enemies.TryGetValue(id, out Enemy e) || e == null || !e.IsAlive) return;
            if (float.IsNaN(impulse.x) || impulse.sqrMagnitude > 1e6f) return;
            e.ApplyImpulse(impulse, knockback);
        }

        public static bool ApplyingRemoteStatus;

        public static void ClientForwardStatus(NetEnemy ne, EnemyStatusEffects fx)
        {
            BinaryWriter w = Session.Begin(MsgType.EnemyStatus);
            w.Write(StageParameters.DungeonLevel);
            w.Write(ne.Id);
            w.Write((byte)fx);
            Session.SendBuiltToHost(true);
        }

        public static void OnStatusRequest(CSteamID from, BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            var fx = (EnemyStatusEffects)r.ReadByte();
            if (level != StageParameters.DungeonLevel || !enemies.TryGetValue(id, out Enemy e) || e == null || !e.IsAlive || e.Data == null) return;
            if (fx >= EnemyStatusEffects.Count) return;
            e.Data.StartStatusEffect(fx, NetPlayerSource.For(from, e.transform.position));
        }

        // =====================================================================
        // Client
        // =====================================================================

        private static bool ClientReady(int level)
        {
            return StageScript.Instance != null && LocalPlayerAccess.Logic != null
                   && level == StageParameters.DungeonLevel && level == floorLevel;
        }

        public static void OnSpawn(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            string defId = r.ReadString();
            Vector3 pos = ReadVec(r);
            float yaw = r.ReadSingle();
            bool isBoss = r.ReadBoolean();
            bool isSpawn = r.ReadBoolean();
            var champion = (EnemyChampionTypes)r.ReadInt32();

            if (!ClientReady(level) || enemies.ContainsKey(id)) return;
            EnemyDefinition def = ItemManager.GetEnemy(defId);
            if (def == null) { Plugin.Log.LogWarning($"[client] Unknown enemy '{defId}'"); return; }

            enemies[id] = null; // reserved while the prefab loads asynchronously
            AllowLocalSpawn = true;
            try
            {
                StageScript.Instance.SpawnEnemy(def, pos, yaw, e => OnPuppetReady(e, id, level, isSpawn, champion), isBoss);
            }
            finally { AllowLocalSpawn = false; }
        }

        private static void OnPuppetReady(Enemy e, int id, int level, bool isSpawn, EnemyChampionTypes champion)
        {
            if (e == null) return;
            if (level != floorLevel) { UnityEngine.Object.Destroy(e.gameObject); return; }

            var ne = e.gameObject.AddComponent<NetEnemy>();
            ne.Id = id;
            ne.IsPuppet = true;
            ne.IsSpawnFlag = isSpawn;
            enemies[id] = e;

            if (champion != EnemyChampionTypes.None)
            {
                try { e.Data.SetChampion(champion); }
                catch (Exception ex) { Plugin.Log.LogWarning($"SetChampion({champion}) failed: {ex.Message}"); }
            }

            if (pendingDeaths.Remove(id)) { KillPuppet(e, ne, DeathReason.Damage); return; }

            if (e.IsBoss) OnBossPuppetReady(e);
        }

        // ---------------- Boss (client) ----------------
        // Client boss room: the intro cutscene calls SpawnBossEnemy (blocked) and then
        // RoomScript.StartBossFight(boss). We hand it the host's boss puppet instead.

        private static Enemy bossPuppet;
        private static readonly List<Action<Enemy>> pendingBossCallbacks = new List<Action<Enemy>>();
        private static RoomScript pendingBossRoom;

        public static void ClientRequestBoss(Action<Enemy> callback)
        {
            if (bossPuppet != null && bossPuppet.IsAlive) callback?.Invoke(bossPuppet);
            else if (callback != null) pendingBossCallbacks.Add(callback);
        }

        /// <summary>Boss cutscene ended before the host's boss existed: start the fight when it arrives.</summary>
        public static void ClientDeferBossFight(RoomScript room)
        {
            pendingBossRoom = room;
            Plugin.Log.LogInfo("[client] Boss fight waiting for the host's boss");
        }

        private static void OnBossPuppetReady(Enemy e)
        {
            bossPuppet = e;
            Plugin.Log.LogInfo($"[client] Boss puppet ready ({e.Definition?.Id})");
            foreach (Action<Enemy> cb in pendingBossCallbacks)
            {
                try { cb(e); } catch (Exception ex) { Plugin.Log.LogWarning($"Boss callback failed: {ex.Message}"); }
            }
            pendingBossCallbacks.Clear();

            if (pendingBossRoom != null)
            {
                RoomScript room = pendingBossRoom;
                pendingBossRoom = null;
                room.StartBossFight(e);
            }
        }

        public static void OnSnapshots(BinaryReader r)
        {
            int level = r.ReadInt32();
            int count = r.ReadByte();
            bool ready = ClientReady(level);
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                Vector3 pos = ReadVec(r);
                float yaw = r.ReadSingle();
                float health = r.ReadSingle();
                var hostState = (Enemy.EnemyState)r.ReadByte();
                byte stageInfo = r.ReadByte();
                byte statusMask = r.ReadByte();
                if (!ready || !enemies.TryGetValue(id, out Enemy e) || e == null) continue;

                NetEnemy ne = e.GetComponent<NetEnemy>();
                if (ne == null) continue;
                ne.Push(pos, yaw);

                // Bosses (and some spawns) leave the intro state from outside (cutscene end);
                // follow the host so the puppet can play attacks.
                if (e.State == Enemy.EnemyState.Spawning && hostState == Enemy.EnemyState.Alive)
                    e.EndSpawn();

                if (e.State != Enemy.EnemyState.Dead)
                {
                    SyncStage(e, stageInfo & 0x7F, (stageInfo & 0x80) != 0);
                    SyncStatus(e, statusMask);
                }

                // Mirror host health (lowering it plays hurt feedback / health bar). Never to 0 here:
                // death only comes from EnemyDie.
                EnemyData data = e.Data;
                // Local hit estimates may drift; follow the host down always, up only on clear gaps
                // (avoids flicker while our own hits are in flight).
                if (data != null && data.IsAlive && health > 0f)
                {
                    float local = data.Health.Progress;
                    if (health < local - 0.001f || health > local + 0.05f)
                        data.Health.Progress = health;
                }
            }
        }

        // ---------------------------------------------------------------- status effects / boss stages

        private static byte StatusMask(Enemy e)
        {
            int mask = 0;
            if (e.Data?.StatusEffects != null)
                foreach (EnemyData.EnemyStatusEffect s in e.Data.StatusEffects)
                    if ((int)s.StatusEffect < 8) mask |= 1 << (int)s.StatusEffect;
            return (byte)mask;
        }

        private static readonly System.Reflection.MethodInfo EndStatusMethod = AccessTools.Method(typeof(EnemyData), "EndStatusEffect");
        private static readonly System.Reflection.FieldInfo OnEndStatusField = AccessTools.Field(typeof(EnemyData), "OnEndStatusEffect");
        private static readonly System.Reflection.MethodInfo StartNextStageMethod = AccessTools.Method(typeof(Enemy), "StartNextStage");

        /// <summary>Client: mirror the host's status effects (visuals, slow) without local damage ticks.</summary>
        private static void SyncStatus(Enemy e, byte mask)
        {
            EnemyData d = e.Data;
            if (d?.StatusEffects == null || !d.IsAlive) return;
            byte local = StatusMask(e);
            if (local == mask) return;
            try
            {
                for (int i = 0; i < (int)EnemyStatusEffects.Count && i < 8; i++)
                {
                    bool want = (mask & (1 << i)) != 0, have = (local & (1 << i)) != 0;
                    var fx = (EnemyStatusEffects)i;
                    if (want && !have) { ApplyingRemoteStatus = true; try { d.StartStatusEffect(fx, null); } finally { ApplyingRemoteStatus = false; } }
                    else if (!want && have)
                    {
                        EndStatusMethod?.Invoke(d, new object[] { fx });
                        d.StatusEffects.RemoveAll(s => s.StatusEffect == fx);
                        (OnEndStatusField?.GetValue(d) as System.Action<EnemyStatusEffects>)?.Invoke(fx);
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogDebug($"status sync: {ex.Message}"); }
        }

        /// <summary>Client: follow the host's boss stage / shielded transition.</summary>
        private static void SyncStage(Enemy e, int hostStage, bool hostTransition)
        {
            if (e.Definition == null || e.Definition.StageCount <= 1) return;
            try
            {
                for (int guard = 0; guard < 8; guard++)
                {
                    if (e.CurrentStage < hostStage)
                    {
                        if (!e.IsInStageTransition) e.CurrentStageComplete();
                        StartNextStageMethod?.Invoke(e, null);
                        continue;
                    }
                    if (e.CurrentStage == hostStage && hostTransition && !e.IsInStageTransition)
                    {
                        Plugin.Log.LogInfo($"[client] Boss {e.Definition.Id} enters stage transition {hostStage}");
                        e.CurrentStageComplete();
                    }
                    else if (e.CurrentStage == hostStage && !hostTransition && e.IsInStageTransition)
                    {
                        Plugin.Log.LogInfo($"[client] Boss {e.Definition.Id} starts stage {hostStage + 1}");
                        StartNextStageMethod?.Invoke(e, null);
                    }
                    break;
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogDebug($"stage sync: {ex.Message}"); }
        }

        public static void OnDie(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            var reason = (DeathReason)r.ReadInt32();
            ulong killer = r.ReadUInt64();
            if (level != floorLevel) return;

            if (enemies.TryGetValue(id, out Enemy e))
            {
                if (e == null) { pendingDeaths.Add(id); return; } // still loading
                NetEnemy kne = e.GetComponent<NetEnemy>();
                // We landed the killing blow on the host: run our on-kill logic (weapon mastery, on-kill perks).
                if (killer == Session.LocalId.m_SteamID && kne?.LastLocalSource != null)
                {
                    try { kne.LastLocalSource.DidKill(e); }
                    catch (System.Exception ex) { Plugin.Log.LogDebug($"kill credit: {ex.Message}"); }
                }
                KillPuppet(e, kne, reason);
            }
            else pendingDeaths.Add(id);
        }

        private static void KillPuppet(Enemy e, NetEnemy ne, DeathReason reason)
        {
            enemies.Remove(ne != null ? ne.Id : -1);
            if (e.State == Enemy.EnemyState.Dead) return;
            if (ne != null) ne.AllowDeath = true;

            Vector3 pos = e.transform.position;
            int threat = e.Data != null ? e.Data.Threat : 1;
            bool champion = e.Data != null && e.Data.IsChampion;
            EnemyStatusEffects? status = e.Data?.HasStatusEffect();
            bool isSpawn = ne != null && ne.IsSpawnFlag;

            bool isBoss = e.IsBoss;
            e.Kill(reason == DeathReason.Suicide);

            // Each player gets their own mana/health drops. No XP orbs: XP is shared through
            // XpShare from the host's orbs (expired orbs are auto-collected), avoiding double XP.
            // Bosses: the client's BossFight spawns its own boss drops.
            if (reason != DeathReason.Suicide && !isBoss && StageScript.Instance != null)
                StageScript.Instance.SpawnEnemyDrops(pos + Vector3.up, threat, champion, false, isSpawn, status, 0);
        }

        public static void OnAttack(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            int idx = r.ReadInt32();
            ulong targetId = r.ReadUInt64();
            if (!ClientReady(level) || !enemies.TryGetValue(id, out Enemy e) || e == null) return;
            if (e.State == Enemy.EnemyState.Dead || e.Attacks == null || idx < 0 || idx >= e.Attacks.Count) return;

            Character target = null;
            if (targetId == Session.LocalId.m_SteamID) target = LocalPlayerAccess.Get();
            else if (targetId != 0) target = RemotePlayers.GetCharacter(new CSteamID(targetId));

            try { e.StartAttack(e.Attacks[idx], target); }
            catch (Exception ex) { Plugin.Log.LogWarning($"Replay attack failed on {e.Definition?.Id}: {ex.Message}"); }
        }

        /// <summary>Client: our weapon hit a puppet → ask the host to apply it.</summary>
        public static void ClientForwardHurt(NetEnemy ne, DamageData d)
        {
            BinaryWriter w = Session.Begin(MsgType.EnemyHurt);
            w.Write(StageParameters.DungeonLevel);
            w.Write(ne.Id);
            w.Write((int)d.Damage.Nature);
            w.Write(d.Damage.Damage);
            w.Write(d.Damage.ProcChance);
            w.Write(d.Duration);
            WriteVec(w, d.SourcePosition);
            Session.SendBuiltToHost(true);
        }

        private static readonly System.Reflection.FieldInfo OnHitField = AccessTools.Field(typeof(Enemy), "OnHit");

        /// <summary>
        /// Client: what the game would show for a local hit (health bar, damage number, hurt anim,
        /// on-hit perks), with a local health estimate. Host snapshots correct it; never reaches 0
        /// locally (death comes from the host).
        /// </summary>
        public static void ClientLocalHitFeedback(Enemy e, DamageData d)
        {
            if (d.IsDamageOverTime || e.Data == null || e.Definition == null) return;
            try
            {
                float dealt = e.Definition.Resistances.ApplyResistance(d.Damage, out EnemyElementalResistance resistance);
                LimitedFloat health = e.Data.Health;
                float floor = Mathf.Min(health.Value, health.MaxValue * 0.01f);
                health.Value = Mathf.Max(health.Value - dealt, floor);

                var onHit = OnHitField?.GetValue(e) as Action<Enemy, EnemyElementalResistance, IDamageSource, float, ElementalNature>;
                onHit?.Invoke(e, resistance, d.DamageSource, dealt, d.DamageNature);

                if (d.DamageSource?.Character is Player p) p.PlayerData.EnemyDamaged(dealt);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Hit feedback failed: {ex.Message}");
            }
        }

        /// <summary>Client: drive a puppet that is idle/moving (not attacking, spawning or dead).</summary>
        public static void DrivePuppet(Enemy e, NetEnemy ne)
        {
            if (!ne.HasSnapshot) return;
            ne.Sample(out Vector3 target, out float yaw, out Vector3 vel);
            MoveTowards(e, target);
            e.transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up);

            // Animator picks Walk/Idle from MovementState + CharacterController.velocity.
            MovementStateRef(e) = vel.sqrMagnitude > 0.09f ? Enemy.EnemyMovementState.Moving : Enemy.EnemyMovementState.Thinking;
        }

        /// <summary>Client: after the game's own Update (attack/spawn), pull the puppet to the host position.</summary>
        public static void CorrectPuppet(Enemy e, NetEnemy ne)
        {
            if (!ne.HasSnapshot || e.State == Enemy.EnemyState.Dead) return;
            ne.Sample(out Vector3 target, out _, out _);
            MoveTowards(e, target);
        }

        private static void MoveTowards(Enemy e, Vector3 target)
        {
            Transform t = e.transform;
            Vector3 delta = target - t.position;
            CharacterController cc = e.CharacterController;
            if (delta.sqrMagnitude > 16f || cc == null || !cc.enabled)
            {
                bool was = cc != null && cc.enabled;
                if (cc != null) cc.enabled = false;
                t.position = target;
                if (cc != null) cc.enabled = was;
            }
            else
            {
                cc.Move(delta);
            }
        }

        // =====================================================================

        public static void Tick(float dt)
        {
            if (!Session.IsHost || StageScript.Instance == null) return;
            snapshotTimer -= dt;
            if (snapshotTimer > 0f) return;
            snapshotTimer = 1f / SnapshotRate;
            HostSendSnapshots();
        }

        private static void WriteVec(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVec(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
