using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>Reads the local game's player (StageScript.Instance.LocalPlayer).</summary>
    internal static class LocalPlayerAccess
    {
        public static Player Get()
        {
            StageScript stage = StageScript.Instance;
            if (stage == null || !stage.HasLocalPlayer) return null;
            return stage.LocalPlayer;
        }

        /// <summary>StageScript.DungeonLogic throws outside a stage (static over a null instance).</summary>
        public static DungeonLogic Logic => StageScript.Instance != null ? StageScript.DungeonLogic : null;

        public static Logic.PlayerData Data => StageScript.Instance != null ? StageScript.PlayerData : null;

        public static bool TryGetState(out PlayerStateMsg state)
        {
            state = default;
            Player p = Get();
            if (p == null) return false;

            Transform t = p.transform;
            Camera cam = Camera.main;
            float pitch = cam != null ? cam.transform.eulerAngles.x : 0f;
            if (pitch > 180f) pitch -= 360f;

            state = new PlayerStateMsg
            {
                Time = Time.unscaledTime,
                Position = t.position,
                Yaw = t.eulerAngles.y,
                Pitch = pitch,
            };
            try
            {
                state.Velocity = p.Velocity;
                byte f = 0;
                if (p.IsGrounded) f |= PlayerStateMsg.FlagGrounded;
                if (p.Controller != null && p.Controller.IsDashing) f |= PlayerStateMsg.FlagDashing;
                if (p.Controller != null && p.Controller.IsJumping) f |= PlayerStateMsg.FlagJumping;
                if (p.CurrentWeapon != null && p.CurrentWeapon.IsShooting) f |= PlayerStateMsg.FlagShooting;
                state.Flags = f;
                state.Health = (byte)Mathf.RoundToInt(Mathf.Clamp01(p.PlayerData.Health.Progress) * 255f);
            }
            catch { /* controller not ready yet */ }
            return true;
        }
    }
}
