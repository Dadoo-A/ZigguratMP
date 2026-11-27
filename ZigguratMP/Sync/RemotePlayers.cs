using System.Collections.Generic;
using System.Linq;
using Logic;
using Logic.Campaign;
using Milkstone.Utils;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Other players: capsule + name tag, interpolated from snapshots, with a
    /// <see cref="RemotePlayerCharacter"/> + hitbox so enemy AI (host) and replicated enemy
    /// attacks (everyone) can target them.
    /// </summary>
    internal static class RemotePlayers
    {
        private static float InterpDelay => Mathf.Clamp(Plugin.InterpDelay.Value, 0.05f, 0.5f); // seconds behind newest snapshot (absorbs P2P jitter)
        private const float MaxExtrapolate = 0.25f;
        private const float StaleAfter = 2f;    // hide ghost if no packet for this long
        private const float PlayerHeight = 1.8f; // GameGlobals.PlayerHeight

        private sealed class Ghost
        {
            public CSteamID Id;
            public string Name;
            public GameObject Go;
            public Transform Head;
            public RemotePlayerCharacter Character;
            public DungeonLogic RegisteredLogic;
            public readonly List<PlayerStateMsg> Buffer = new List<PlayerStateMsg>(8);
            public float Clock;
            public bool ClockSynced;
            public float LastRecv;
            public string CharacterId;
            public GameObject Placeholder; // capsule shown until the 3D body is loaded
            public GameObject Body;
            public RemoteBodyAnimator BodyAnim;
            public string WeaponId;
            public GameObject Weapon;
            public WeaponAnimator WeaponFx;   // disabled; read for muzzle/particles/sounds
            public Transform Muzzle;
            public AudioSource Audio;
            public Vector3 DisplayPos;
            public bool HasDisplayPos;
            public bool Downed;
            public float Health = 1f;
        }

        private static readonly Dictionary<CSteamID, Ghost> ghosts = new Dictionary<CSteamID, Ghost>();

        private static Ghost GetOrCreate(CSteamID id)
        {
            if (!ghosts.TryGetValue(id, out Ghost g))
            {
                g = new Ghost { Id = id, Name = SteamFriends.GetFriendPersonaName(id) };
                ghosts[id] = g;
            }
            return g;
        }

        /// <summary>Remote player's character changed: (re)load its 3D body.</summary>
        public static void ApplyInfo(CSteamID from, string characterId, string weaponId)
        {
            Ghost g = GetOrCreate(from);
            if (g.CharacterId != characterId)
            {
                g.CharacterId = characterId;
                if (g.Go != null) LoadBody(g);
            }
            if (g.WeaponId != weaponId)
            {
                g.WeaponId = weaponId;
                if (g.Go != null) LoadWeapon(g);
            }
        }

        /// <summary>The remote player's current weapon model (first-person prefab, effects kept).</summary>
        private static void LoadWeapon(Ghost g)
        {
            if (g.Weapon != null) { Object.Destroy(g.Weapon); g.Weapon = null; g.WeaponFx = null; g.Muzzle = null; }
            g.BodyAnim?.AttachWeapon(null, null);
            WeaponDefinition def = string.IsNullOrEmpty(g.WeaponId) ? null : ItemManager.GetItem<WeaponDefinition>(g.WeaponId);
            if (def == null || def.WeaponPrefab == null || !def.WeaponPrefab.RuntimeKeyIsValid()) return;

            GameObject owner = g.Go;
            string wanted = g.WeaponId;
            def.WeaponPrefab.InstantiateAddressable(owner.transform, w =>
            {
                if (w == null) return;
                if (owner == null || g.Go != owner || g.WeaponId != wanted) { Object.Destroy(w); return; }
                if (g.Weapon != null) Object.Destroy(g.Weapon);
                g.Weapon = w;
                PrepareWeapon(g, w);
            });
        }

        private static void PrepareWeapon(Ghost g, GameObject w)
        {
            WeaponAnimator wa = w.GetComponent<WeaponAnimator>();
            if (wa != null)
            {
                wa.enabled = false; // Start() needs the first-person hands rig; never let it run
                g.WeaponFx = wa;
                g.Muzzle = wa.BulletOrigin != null ? wa.BulletOrigin.transform : null;
                if (wa.HandsRoot != null) Object.Destroy(wa.HandsRoot);
                if (wa.LeftHand != null)
                {
                    Transform lh = g.BodyAnim != null ? g.BodyAnim.LeftHand : null;
                    if (lh != null)
                    {
                        wa.LeftHand.transform.SetParent(lh, false);
                        wa.LeftHand.transform.localPosition = Vector3.zero;
                        wa.LeftHand.transform.localRotation = Quaternion.identity;
                    }
                    else Object.Destroy(wa.LeftHand);
                }
            }
            foreach (Animator a in w.GetComponentsInChildren<Animator>(true)) a.enabled = false; // FP animations
            foreach (Collider c in w.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (Transform t in w.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            foreach (Renderer r in w.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            foreach (ParticleSystem ps in w.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                if (main.simulationSpace == ParticleSystemSimulationSpace.Custom) main.simulationSpace = ParticleSystemSimulationSpace.World;
            }

            w.transform.localPosition = Vector3.zero;
            w.transform.localRotation = Quaternion.identity;
            w.transform.localScale = Vector3.one;
            float size = 0f;
            Renderer[] rs = w.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToArray();
            if (rs.Length > 0)
            {
                Bounds b = rs[0].bounds;
                foreach (Renderer r in rs) b.Encapsulate(r.bounds);
                size = b.size.magnitude;
                if (size > 2.2f || (size > 0.01f && size < 0.25f)) w.transform.localScale = Vector3.one * (0.9f / size);
            }

            g.Audio = w.AddComponent<AudioSource>();
            g.Audio.spatialBlend = 1f;
            g.Audio.minDistance = 3f;
            g.Audio.maxDistance = 35f;
            g.Audio.rolloffMode = AudioRolloffMode.Linear;
            g.Audio.playOnAwake = false;

            g.BodyAnim?.AttachWeapon(w.transform, g.Muzzle);
            if (g.Body == null) w.SetActive(false);
            Plugin.Log.LogInfo($"[weapon {g.Name}] {g.WeaponId} size={size:0.00} muzzle={(g.Muzzle != null ? g.Muzzle.name : "none")}");
        }

        /// <summary>Remote player fired attack <paramref name="attackIndex"/>: muzzle fx, sound, recoil.</summary>
        public static void OnRemoteFire(CSteamID from, int attackIndex)
        {
            if (!ghosts.TryGetValue(from, out Ghost g) || g.Go == null || !g.Go.activeSelf) return;
            g.BodyAnim?.Fire();
            WeaponAnimator wa = g.WeaponFx;
            if (wa == null || g.Weapon == null || !g.Weapon.activeInHierarchy) return;
            WeaponAnimator.AttackEffects fx = attackIndex <= 0 ? wa.MainAttack : wa.SecondaryAttack;
            try
            {
                if (fx.ShootFx != null) fx.ShootFx.Play();
                if (fx.ShootSound != null && !fx.ShootSound.IsLoop && g.Audio != null) fx.ShootSound.Play(g.Audio);
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"remote fx: {e.Message}"); }
        }

        public static void SetDowned(CSteamID from, bool down)
        {
            Ghost g = GetOrCreate(from);
            g.Downed = down;
            if (g.Character != null) g.Character.Downed = down;
            if (g.BodyAnim != null) g.BodyAnim.Downed = down;
        }

        /// <summary>World position remote bullets should start from (weapon muzzle), if known.</summary>
        public static Vector3? GetMuzzle(CSteamID from)
        {
            if (!ghosts.TryGetValue(from, out Ghost g) || g.Go == null || !g.Go.activeSelf) return null;
            if (g.Weapon == null || !g.Weapon.activeInHierarchy) return null;
            return g.Muzzle != null ? g.Muzzle.position : g.Weapon.transform.position;
        }

        private static void LoadBody(Ghost g)
        {
            CharacterDefinition def = string.IsNullOrEmpty(g.CharacterId) ? null : ItemManager.GetItem<CharacterDefinition>(g.CharacterId);
            if (def == null || def.BodyPrefab == null || !def.BodyPrefab.RuntimeKeyIsValid()) return;

            GameObject owner = g.Go;
            string wanted = g.CharacterId;
            def.BodyPrefab.InstantiateAddressable(owner.transform, body =>
            {
                if (body == null) return;
                if (owner == null || g.Go != owner || g.CharacterId != wanted) { Object.Destroy(body); return; }
                if (g.Body != null) Object.Destroy(g.Body);
                g.Body = body;
                PrepareBody(body);
                g.BodyAnim = body.AddComponent<RemoteBodyAnimator>();
                g.BodyAnim.OwnerName = g.Name;
                g.BodyAnim.Downed = g.Downed;
                if (g.Placeholder != null) g.Placeholder.SetActive(false);
                if (g.Weapon != null)
                {
                    g.Weapon.SetActive(true);
                    g.BodyAnim.AttachWeapon(g.Weapon.transform, g.Muzzle);
                }
                Plugin.Log.LogInfo($"Loaded body {wanted} for {g.Name}");
            });
        }

        /// <summary>Menu/cutscene body → world prop: no colliders, default layer, idle pose.</summary>
        private static void PrepareBody(GameObject body)
        {
            Transform t = body.transform;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            foreach (Collider c in body.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (Transform child in body.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 0;
            // BodyAnimator drives menu states (and would fight our locomotion graph). Remove it
            // before its Start() runs, but keep what it did for cloth/hair: enable DynamicBones.
            BodyAnimator anim = body.GetComponent<BodyAnimator>();
            if (anim != null) Object.DestroyImmediate(anim);
            foreach (DynamicBone db in body.GetComponentsInChildren<DynamicBone>(true))
            {
                db.m_UpdateMode = DynamicBone.UpdateMode.Normal;
                db.enabled = true;
            }
        }

        public static void ApplyState(CSteamID from, PlayerStateMsg s)
        {
            Ghost g = GetOrCreate(from);

            g.LastRecv = Time.unscaledTime;
            if (g.Buffer.Count > 0 && s.Time <= g.Buffer[g.Buffer.Count - 1].Time) return; // out of order
            if (g.Buffer.Count > 0 && s.Time - g.Buffer[g.Buffer.Count - 1].Time > 1f) g.ClockSynced = false; // gap (loading)
            g.Buffer.Add(s);
            if (g.Buffer.Count > 8) g.Buffer.RemoveAt(0);

            if (!g.ClockSynced) { g.Clock = s.Time - InterpDelay; g.ClockSynced = true; }
        }

        public static void Tick(float dt)
        {
            bool inStage = LocalPlayerAccess.Get() != null;
            DungeonLogic logic = inStage ? LocalPlayerAccess.Logic : null;

            foreach (Ghost g in ghosts.Values)
            {
                bool visible = inStage && g.Buffer.Count > 0 && Time.unscaledTime - g.LastRecv < StaleAfter;
                if (!visible)
                {
                    Hide(g);
                    continue;
                }

                EnsureGo(g); // scene reloads destroy our objects; recreate on demand
                if (!g.Go.activeSelf) g.Go.SetActive(true);

                if (Session.IsHost && logic != null && g.RegisteredLogic != logic)
                {
                    logic.AddEnemyTarget(g.Character);
                    g.RegisteredLogic = logic;
                }

                g.Clock += dt;
                PlayerStateMsg newest = g.Buffer[g.Buffer.Count - 1];
                float target = newest.Time - InterpDelay;
                if (Mathf.Abs(g.Clock - target) > 0.5f) g.Clock = target;

                // Drift the playout clock gently towards its target instead of snapping.
                g.Clock += (target - g.Clock) * Mathf.Clamp01(dt * 2f);

                Sample(g, g.Clock, out Vector3 pos, out float yaw, out float pitch, out Vector3 vel, out byte flags);
                g.Health = g.Buffer[g.Buffer.Count - 1].Health / 255f;
                if (!g.HasDisplayPos || (g.DisplayPos - pos).sqrMagnitude > 25f) { g.DisplayPos = pos; g.HasDisplayPos = true; }
                else g.DisplayPos = Vector3.Lerp(g.DisplayPos, pos, 1f - Mathf.Exp(-dt * 25f));
                g.Go.transform.SetPositionAndRotation(g.DisplayPos, Quaternion.Euler(0f, yaw, 0f));
                g.Head.localRotation = Quaternion.Euler(pitch, 0f, 0f);
                g.Character.NetVelocity = vel;
                if (g.BodyAnim != null)
                {
                    g.BodyAnim.Velocity = vel;
                    g.BodyAnim.Pitch = pitch;
                    g.BodyAnim.Grounded = (flags & PlayerStateMsg.FlagGrounded) != 0;
                    g.BodyAnim.Dashing = (flags & PlayerStateMsg.FlagDashing) != 0;
                    g.BodyAnim.Shooting = (flags & PlayerStateMsg.FlagShooting) != 0;
                }
            }
        }

        private static void Hide(Ghost g)
        {
            if (g.Go == null) { g.RegisteredLogic = null; return; }
            if (g.RegisteredLogic != null)
            {
                g.RegisteredLogic.RemoveEnemyTarget(g.Character);
                g.RegisteredLogic = null;
            }
            if (g.Go.activeSelf) g.Go.SetActive(false);
        }

        private static void Sample(Ghost g, float t, out Vector3 pos, out float yaw, out float pitch, out Vector3 vel, out byte flags)
        {
            List<PlayerStateMsg> b = g.Buffer;
            PlayerStateMsg last = b[b.Count - 1];
            if (t >= last.Time)
            {
                // Ran past the newest snapshot (late packet): extrapolate briefly with its velocity.
                float ahead = Mathf.Min(t - last.Time, MaxExtrapolate);
                pos = last.Position + last.Velocity * ahead;
                yaw = last.Yaw; pitch = last.Pitch; vel = last.Velocity; flags = last.Flags;
                return;
            }
            for (int i = b.Count - 1; i > 0; i--)
            {
                PlayerStateMsg a = b[i - 1], c = b[i];
                if (t >= a.Time)
                {
                    float span = Mathf.Max(0.0001f, c.Time - a.Time);
                    float k = Mathf.Clamp01((t - a.Time) / span);
                    // Hermite with the sent velocities: smooth through snapshots.
                    pos = Hermite(a.Position, a.Velocity * span, c.Position, c.Velocity * span, k);
                    yaw = Mathf.LerpAngle(a.Yaw, c.Yaw, k);
                    pitch = Mathf.Lerp(a.Pitch, c.Pitch, k);
                    vel = Vector3.Lerp(a.Velocity, c.Velocity, k);
                    flags = k < 0.5f ? a.Flags : c.Flags;
                    return;
                }
            }
            PlayerStateMsg first = b[0];
            pos = first.Position; yaw = first.Yaw; pitch = first.Pitch; vel = first.Velocity; flags = first.Flags;
        }

        private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            Vector3 h = (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
            // Teleports / dash bursts can make tangents wild: never stray far from the straight line.
            Vector3 lin = Vector3.Lerp(p0, p1, t);
            return (h - lin).sqrMagnitude > 0.25f ? lin : h;
        }

        private static void EnsureGo(Ghost g)
        {
            if (g.Go != null) return;
            g.RegisteredLogic = null;

            g.Go = new GameObject($"ZMP_Ghost_{g.Name}");
            g.Character = g.Go.AddComponent<RemotePlayerCharacter>();
            g.Character.SteamId = g.Id;
            g.Character.Downed = g.Downed;
            g.Character.Team = Team.Player;

            g.Placeholder = new GameObject("Placeholder");
            g.Placeholder.transform.SetParent(g.Go.transform, false);
            g.Body = null;

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<Collider>()); // must not block movement
            body.transform.SetParent(g.Placeholder.transform, false);
            body.transform.localPosition = new Vector3(0f, PlayerHeight * 0.5f, 0f);
            body.transform.localScale = new Vector3(0.6f, PlayerHeight * 0.5f, 0.6f);

            var head = new GameObject("Head");
            head.transform.SetParent(g.Placeholder.transform, false);
            head.transform.localPosition = new Vector3(0f, PlayerHeight * 0.9f, 0f);
            GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube); // shows aim direction
            Object.Destroy(nose.GetComponent<Collider>());
            nose.transform.SetParent(head.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0f, 0.4f);
            nose.transform.localScale = new Vector3(0.1f, 0.1f, 0.5f);
            g.Head = head.transform;

            Material mat = GhostMaterial;
            if (mat != null)
                foreach (Renderer r in g.Placeholder.GetComponentsInChildren<Renderer>())
                    r.sharedMaterial = mat;

            CreateHitbox(g.Go.transform);
            g.HasDisplayPos = false;
            g.Weapon = null; g.WeaponFx = null; g.Muzzle = null; g.BodyAnim = null;
            LoadBody(g);
            LoadWeapon(g);
        }

        /// <summary>Hitbox on the "Hitbox" layer, modelled on the local player's if found.</summary>
        private static void CreateHitbox(Transform parent)
        {
            var hb = new GameObject("Hitbox") { layer = GameGlobals.LayerHitbox };
            hb.transform.SetParent(parent, false);
            BoxCollider box = hb.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(0f, PlayerHeight * 0.5f, 0f);
            box.size = new Vector3(0.8f, PlayerHeight, 0.8f);

            Player local = LocalPlayerAccess.Get();
            CharacterHitbox reference = local != null ? local.GetComponentInChildren<CharacterHitbox>(true) : null;
            BoxCollider refBox = reference != null ? reference.GetComponent<BoxCollider>() : null;
            if (refBox != null)
            {
                box.isTrigger = refBox.isTrigger;
                box.size = Vector3.Scale(refBox.size, refBox.transform.lossyScale);
                box.center = new Vector3(0f, box.size.y * 0.5f, 0f);
            }

            hb.AddComponent<CharacterHitbox>(); // OnEnable picks up RemotePlayerCharacter from parent
        }

        public static Character GetCharacter(CSteamID id)
        {
            return ghosts.TryGetValue(id, out Ghost g) && g.Go != null && g.Go.activeSelf ? g.Character : null;
        }

        public static void DrawLabels()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            foreach (Ghost g in ghosts.Values)
            {
                if (g.Go == null || !g.Go.activeSelf) continue;
                Vector3 world = g.Go.transform.position + Vector3.up * (PlayerHeight + 0.3f);
                UI.AllyHud.DrawOffscreen(cam, world, g.Name, g.Downed);
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f) continue;
                UI.AllyHud.DrawHealthBar(new Vector2(sp.x, Screen.height - sp.y + 12f), g.Health, g.Downed);
                var rect = new Rect(sp.x - 75f, Screen.height - sp.y - 12f, 150f, 24f);
                GUI.Label(rect, g.Downed ? $"{g.Name}  [DOWN]" : g.Name, g.Downed ? Styles.DownTag : Styles.NameTag);
            }
        }

        public static void Remove(CSteamID id)
        {
            if (ghosts.TryGetValue(id, out Ghost g))
            {
                Hide(g);
                if (g.Go != null) Object.Destroy(g.Go);
                ghosts.Remove(id);
            }
        }

        public static void Clear()
        {
            foreach (Ghost g in ghosts.Values)
            {
                Hide(g);
                if (g.Go != null) Object.Destroy(g.Go);
            }
            ghosts.Clear();
        }

        public static int Count => ghosts.Count;

        public struct GhostView { public string Name; public Vector3 Position; public float Yaw; public bool Downed; }

        public static IEnumerable<GhostView> VisibleGhosts()
        {
            foreach (Ghost g in ghosts.Values)
                if (g.Go != null && g.Go.activeSelf)
                    yield return new GhostView { Name = g.Name, Position = g.Go.transform.position, Yaw = g.Go.transform.eulerAngles.y, Downed = g.Downed };
        }

        private static Material ghostMaterial;
        private static bool ghostMaterialTried;

        /// <summary>
        /// Primitives default to the Standard shader, which the game build strips (renders pink).
        /// Use a shader Unity always ships ("Always Included Shaders").
        /// </summary>
        private static Material GhostMaterial
        {
            get
            {
                if (ghostMaterialTried) return ghostMaterial;
                ghostMaterialTried = true;
                foreach (string name in new[] { "Sprites/Default", "UI/Default", "Unlit/Color", "Legacy Shaders/Diffuse" })
                {
                    Shader s = Shader.Find(name);
                    if (s == null) continue;
                    ghostMaterial = new Material(s) { color = new Color(0.2f, 0.9f, 1f, 1f) };
                    Plugin.Log.LogInfo($"Ghost material uses shader '{name}'");
                    break;
                }
                if (ghostMaterial == null) Plugin.Log.LogWarning("No fallback shader found for ghosts (will render pink)");
                return ghostMaterial;
            }
        }

        private static class Styles
        {
            private static GUIStyle nameTag, downTag;
            public static GUIStyle DownTag => downTag ?? (downTag = new GUIStyle(NameTag) { normal = { textColor = new Color(1f, 0.35f, 0.3f) } });
            public static GUIStyle NameTag => nameTag ?? (nameTag = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan },
            });
        }
    }
}
