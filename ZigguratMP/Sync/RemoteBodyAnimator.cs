using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// Animates a remote player's 3D body (the game's menu/cutscene body prefab).
    ///  - Locomotion: a PlayableGraph blends the body's own idle/walk clips (or any humanoid
    ///    walk/run clip loaded in the game) by real speed. The body's AnimatorController is removed
    ///    so it can't override us. Procedural gait if no clip is usable.
    ///  - Upper body: weapon held in front, right arm reaching it (2-bone CCD), head/aim pitch,
    ///    recoil on each shot, lean when dashing, tucked legs when airborne.
    /// </summary>
    internal sealed class RemoteBodyAnimator : MonoBehaviour
    {
        public Vector3 Velocity;
        public float Pitch;
        public string OwnerName;
        public bool Grounded = true;
        public bool Dashing;
        public bool Shooting;
        public bool Downed;
        private float downBlend;

        private Animator animator;
        private bool humanoid;

        // Humanoid playables
        private PlayableGraph graph;
        private AnimationMixerPlayable mixer;
        private readonly AnimationClipPlayable[] clipPlayables = new AnimationClipPlayable[3];
        private readonly AnimationClip[] clips = new AnimationClip[3]; // idle, walk, run
        private bool graphReady;
        private float nextDonorSearch;

        // Bones
        private Transform hips, head, spine, chest;
        private Transform lThigh, rThigh, lCalf, rCalf, lArm, rArm, rForearm, rHand, lHand;
        private Vector3 hips0;
        private float phase;
        private float smoothSpeed;

        // Weapon
        private Transform weapon;
        private Quaternion weaponAlign = Quaternion.identity;
        private float recoil;
        private float shootPose; // 0 = relaxed, 1 = aiming

        private const float WalkSpeed = 3f;
        private const float RunSpeed = 7f;

        public Transform LeftHand => lHand;
        public Transform RightHand => rHand;

        private void Start()
        {
            animator = GetComponentInChildren<Animator>();
            humanoid = animator != null && animator.avatar != null && animator.avatar.isHuman;
            FindBones();

            AnimationClip[] own = animator != null && animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.animationClips : new AnimationClip[0];
            Plugin.Log.LogInfo($"[body {OwnerName}] humanoid={humanoid} clips=[{string.Join(", ", own.Select(c => c.name).Distinct().ToArray())}] bones: hips={N(hips)} rArm={N(rArm)}/{N(rForearm)}/{N(rHand)} head={N(head)}");

            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            if (humanoid) TryBuildGraph(own, log: true);
        }

        private static string N(Transform t) => t != null ? t.name : "none";

        // ---------------------------------------------------------------- humanoid

        private AnimationClip[] ownClips;

        private void TryBuildGraph(AnimationClip[] own, bool log)
        {
            if (own != null && own.Length > 0) ownClips = own;
            AnimationClip[] mine = ownClips ?? new AnimationClip[0];

            string[] avoid = { "back", "left", "right", "strafe", "attack", "hurt", "die", "death", "hit", "turn", "start", "stop", "face" };
            AnimationClip idle = mine.FirstOrDefault(c => c != null && Has(c, "idle") && Has(c, "loop") && !Has(c, "face"))
                                 ?? mine.FirstOrDefault(c => c != null && Has(c, "idle") && !Has(c, "face"));
            AnimationClip walk = mine.FirstOrDefault(c => c != null && Has(c, "walk"))
                                 ?? FindHumanClip(new[] { "walk" }, avoid);
            AnimationClip run = mine.FirstOrDefault(c => c != null && (Has(c, "run") || Has(c, "jog")))
                                ?? FindHumanClip(new[] { "run", "sprint", "jog" }, avoid);
            if (walk == null) walk = run;
            if (run == null) run = walk;

            if (log || walk != null)
                Plugin.Log.LogInfo($"[body {OwnerName}] locomotion clips: idle={idle?.name ?? "none"} walk={walk?.name ?? "none"} run={run?.name ?? "none"}");
            if (walk == null || idle == null) return; // procedural until a donor shows up

            // The body's own controller (menu states) would fight our graph: drop it.
            animator.runtimeAnimatorController = null;

            clips[0] = idle; clips[1] = walk; clips[2] = run;
            graph = PlayableGraph.Create($"ZMP_Body_{OwnerName}");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(graph, "Locomotion", animator);
            mixer = AnimationMixerPlayable.Create(graph, 3);
            for (int i = 0; i < 3; i++)
            {
                clipPlayables[i] = AnimationClipPlayable.Create(graph, clips[i]);
                clipPlayables[i].SetApplyFootIK(true);
                graph.Connect(clipPlayables[i], 0, mixer, i);
            }
            mixer.SetInputWeight(0, 1f);
            output.SetSourcePlayable(mixer);
            graph.Play();
            graphReady = true;
        }

        private static bool Has(AnimationClip c, string s) => c.name.ToLowerInvariant().Contains(s);

        /// <summary>Best looping humanoid clip among everything currently loaded.</summary>
        private static AnimationClip FindHumanClip(string[] want, string[] avoid)
        {
            AnimationClip best = null;
            int bestScore = int.MinValue;
            foreach (AnimationClip c in Resources.FindObjectsOfTypeAll<AnimationClip>())
            {
                if (c == null || !c.isHumanMotion) continue;
                string n = c.name.ToLowerInvariant();
                if (!want.Any(n.Contains) || avoid.Any(n.Contains)) continue;
                int score = (c.isLooping ? 100 : 0) - n.Length;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        // ---------------------------------------------------------------- bones

        private void FindBones()
        {
            if (humanoid)
            {
                hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                spine = animator.GetBoneTransform(HumanBodyBones.Spine);
                chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                head = animator.GetBoneTransform(HumanBodyBones.Head);
                lThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                rThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                lCalf = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                rCalf = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                lArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                rArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                rForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            }

            Transform[] all = GetComponentsInChildren<Transform>(true);
            hips = hips ?? Find(all, null, "hips", "pelvis");
            spine = spine ?? Find(all, null, "spine");
            head = head ?? Find(all, null, "head");
            lThigh = lThigh ?? Find(all, true, "thigh", "upleg", "upperleg", "leg_up", "legl_upper");
            rThigh = rThigh ?? Find(all, false, "thigh", "upleg", "upperleg", "leg_up", "legr_upper");
            lCalf = lCalf ?? Find(all, true, "calf", "knee", "lowerleg", "shin", "leg_low", "legl_lower");
            rCalf = rCalf ?? Find(all, false, "calf", "knee", "lowerleg", "shin", "leg_low", "legr_lower");
            lArm = lArm ?? Find(all, true, "upperarm", "upper_arm", "arm_up", "arml_upper");
            rArm = rArm ?? Find(all, false, "upperarm", "upper_arm", "arm_up", "armr_upper");
            if (rForearm == null && rArm != null && rArm.childCount > 0) rForearm = rArm.GetChild(0);
            if (rHand == null && rForearm != null && rForearm.childCount > 0) rHand = rForearm.GetChild(0);

            if (hips != null) hips0 = hips.localPosition;
        }

        /// <summary>Bone by name keywords; side: true = left, false = right, null = any.</summary>
        private static Transform Find(Transform[] all, bool? left, params string[] keys)
        {
            foreach (Transform t in all)
            {
                string n = t.name.ToLowerInvariant().Replace(" ", "").Replace(":", "");
                if (!keys.Any(n.Contains)) continue;
                if (n.Contains("twist") || n.Contains("end") || n.Contains("nub")) continue;
                if (left == null) return t;
                bool isLeft = n.Contains("left") || n.StartsWith("l_") || n.Contains(".l") || n.EndsWith("_l") || n.Contains("legl") || n.Contains("arml") || n.Contains("bip001l");
                bool isRight = n.Contains("right") || n.StartsWith("r_") || n.Contains(".r") || n.EndsWith("_r") || n.Contains("legr") || n.Contains("armr") || n.Contains("bip001r");
                if (left.Value ? isLeft && !isRight : isRight && !isLeft) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------- weapon

        /// <summary>Holds <paramref name="w"/> in front of the body; muzzle defines its forward.</summary>
        public void AttachWeapon(Transform w, Transform muzzle)
        {
            weapon = w;
            weaponAlign = Quaternion.identity;
            if (w == null) return;
            if (muzzle != null && muzzle != w)
            {
                Vector3 localDir = Quaternion.Inverse(w.rotation) * (muzzle.position - w.position);
                if (localDir.sqrMagnitude > 1e-6f)
                    weaponAlign = Quaternion.FromToRotation(localDir.normalized, Vector3.forward);
            }
        }

        public void Fire()
        {
            recoil = 1f;
            shootPose = 1f;
        }

        // ---------------------------------------------------------------- update

        private void Update()
        {
            Vector3 flat = Velocity; flat.y = 0f;
            smoothSpeed = Mathf.Lerp(smoothSpeed, flat.magnitude, Time.deltaTime * 10f);

            if (humanoid && !graphReady && Time.time > nextDonorSearch)
            {
                nextDonorSearch = Time.time + 5f;
                TryBuildGraph(null, log: false);
            }
            if (graphReady) UpdateGraph();

            recoil = Mathf.MoveTowards(recoil, 0f, Time.deltaTime * 6f);
            shootPose = Mathf.MoveTowards(shootPose, Shooting ? 1f : (weapon != null ? 0.75f : 0f), Time.deltaTime * 4f);
        }

        private void UpdateGraph()
        {
            float speed = Grounded || Dashing ? smoothSpeed : 0f;
            float walkW = Mathf.Clamp01(speed / WalkSpeed);
            float runW = clips[2] == clips[1] ? 0f : Mathf.Clamp01((speed - WalkSpeed) / (RunSpeed - WalkSpeed));
            mixer.SetInputWeight(0, 1f - walkW);
            mixer.SetInputWeight(1, walkW * (1f - runW));
            mixer.SetInputWeight(2, walkW * runW);

            // Loop manually (clips may not be flagged looping); scale playback with speed so
            // the feet roughly match ground speed.
            float rate = speed < 0.2f ? 1f : Mathf.Clamp(speed / (runW > 0.5f ? RunSpeed : WalkSpeed), 0.7f, 2.2f);
            for (int i = 0; i < 3; i++)
            {
                float len = Mathf.Max(0.01f, clips[i].length);
                double t = clipPlayables[i].GetTime() + Time.deltaTime * (i == 0 ? 1f : rate);
                clipPlayables[i].SetTime(t % len);
            }
        }

        private void LateUpdate()
        {
            downBlend = Mathf.MoveTowards(downBlend, Downed ? 1f : 0f, Time.deltaTime * 3f);
            transform.localRotation = Quaternion.Euler(-82f * downBlend, 0f, 0f);
            transform.localPosition = new Vector3(0f, 0.25f * downBlend, 0f);
            if (weapon != null) weapon.gameObject.SetActive(downBlend < 0.5f);
            if (!graphReady) ApplyProceduralGait();
            ApplyAirAndDash();
            ApplyAim();
            ApplyWeapon();
        }

        /// <summary>
        /// Rest rotation per bone: re-read whenever something else (the Animator) wrote the bone
        /// this frame, otherwise reuse the last one, so our offsets never accumulate.
        /// </summary>
        private readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
        private readonly Dictionary<Transform, Quaternion> written = new Dictionary<Transform, Quaternion>();
        private readonly HashSet<Transform> touchedThisFrame = new HashSet<Transform>();
        private int touchFrame = -1;

        private void BeginBone(Transform bone)
        {
            if (touchFrame != Time.frameCount) { touchFrame = Time.frameCount; touchedThisFrame.Clear(); }
            if (touchedThisFrame.Contains(bone)) return; // already reset this frame, stack on top
            touchedThisFrame.Add(bone);
            Quaternion now = bone.localRotation;
            if (!written.TryGetValue(bone, out Quaternion w) || Quaternion.Angle(w, now) > 0.01f || !rest.ContainsKey(bone))
                rest[bone] = now;
            bone.localRotation = rest[bone];
        }

        private void EndBone(Transform bone) => written[bone] = bone.localRotation;

        private void Offset(Transform bone, Vector3 worldAxis, float degrees)
        {
            if (bone == null) return;
            BeginBone(bone);
            if (Mathf.Abs(degrees) > 0.001f)
                bone.rotation = Quaternion.AngleAxis(degrees, worldAxis) * bone.rotation;
            EndBone(bone);
        }

        private void ApplyProceduralGait()
        {
            float speed = Grounded ? smoothSpeed : 0f;
            float amount = Mathf.Clamp01(speed / WalkSpeed);
            float run = Mathf.Clamp01((speed - WalkSpeed) / (RunSpeed - WalkSpeed));
            if (amount > 0.02f)
                phase += Time.deltaTime * Mathf.Lerp(7f, 11f, run) * Mathf.Clamp(speed / WalkSpeed, 0.7f, 1.6f);
            float s = Mathf.Sin(phase);
            float legSwing = Mathf.Lerp(28f, 45f, run) * amount;
            float armSwing = Mathf.Lerp(20f, 40f, run) * amount;

            Vector3 side = transform.right;
            Offset(lThigh, side, s * legSwing);
            Offset(rThigh, side, -s * legSwing);
            Offset(lCalf, side, -Mathf.Max(0f, -s) * legSwing * 1.3f);
            Offset(rCalf, side, -Mathf.Max(0f, s) * legSwing * 1.3f);
            Offset(lArm, side, -s * armSwing);
            if (weapon == null) Offset(rArm, side, s * armSwing);

            if (hips != null)
                hips.localPosition = hips0 + Vector3.up * (Mathf.Abs(Mathf.Cos(phase)) * 0.04f * amount);
        }

        private float airBlend, dashBlend;

        private void ApplyAirAndDash()
        {
            airBlend = Mathf.MoveTowards(airBlend, Grounded ? 0f : 1f, Time.deltaTime * 6f);
            dashBlend = Mathf.MoveTowards(dashBlend, Dashing ? 1f : 0f, Time.deltaTime * 8f);
            Vector3 side = transform.right;
            if (airBlend > 0.01f)
            {
                Offset(lThigh, side, -35f * airBlend);
                Offset(rThigh, side, -20f * airBlend);
                Offset(lCalf, side, 50f * airBlend);
                Offset(rCalf, side, 35f * airBlend);
            }
            if (dashBlend > 0.01f)
            {
                // Lean into the dash direction.
                Vector3 v = Velocity; v.y = 0f;
                Vector3 axis = v.sqrMagnitude > 0.1f ? Vector3.Cross(Vector3.up, v.normalized) : side;
                Offset(spine ?? hips, axis, 18f * dashBlend);
            }
        }

        private void ApplyAim()
        {
            float p = Mathf.Clamp(Pitch, -70f, 70f);
            if (chest != null || spine != null) Offset(chest ?? spine, transform.right, p * 0.35f);
            if (head != null) Offset(head, transform.right, p * 0.45f);
        }

        private void ApplyWeapon()
        {
            if (weapon == null) return;
            Quaternion aim = Quaternion.Euler(Mathf.Clamp(Pitch, -80f, 80f), transform.eulerAngles.y, 0f);
            Vector3 shoulder = rArm != null ? rArm.position : transform.position + Vector3.up * 1.45f + transform.right * 0.2f;
            float reach = Mathf.Lerp(0.3f, 0.5f, shootPose);
            Vector3 grip = shoulder + aim * new Vector3(-0.08f, Mathf.Lerp(-0.35f, -0.18f, shootPose), reach)
                           - aim * Vector3.forward * (0.1f * recoil);
            weapon.position = grip;
            weapon.rotation = Quaternion.AngleAxis(-10f * recoil, aim * Vector3.right) * aim * weaponAlign;

            // Right hand reaches the grip (2 CCD passes over forearm + upper arm).
            if (rArm == null || rForearm == null || rHand == null) return;
            BeginBone(rArm); BeginBone(rForearm);
            for (int it = 0; it < 2; it++)
            {
                Reach(rForearm, rHand.position, grip);
                Reach(rArm, rHand.position, grip);
            }
            EndBone(rArm); EndBone(rForearm);
        }

        private static void Reach(Transform bone, Vector3 effector, Vector3 target)
        {
            Vector3 from = effector - bone.position, to = target - bone.position;
            if (from.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f) return;
            bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }

        private void OnDestroy()
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
