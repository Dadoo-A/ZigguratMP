using HarmonyLib;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// The game stops time (timeScale 0) for the pause menu, level-up perks, map, omens and the
    /// character sheet. In co-op that would freeze the host's enemies for everyone (or desync a
    /// client), so menus keep time running; a player in a menu takes reduced damage and shows
    /// [MENU] to the others.
    /// </summary>
    internal static class CoopPause
    {
        private const float MenuDamageMultiplier = 0.5f;

        private static readonly AccessTools.FieldRef<BaseStageScript, bool> PauseWithTimeScale =
            AccessTools.FieldRefAccess<BaseStageScript, bool>("PauseWithTimeScale");

        public static bool Active => Session.InLobby && Session.Members.Count > 1 && !StageParameters.IsTutorial;

        public static bool InMenu => Active && BaseStageScript.HasInstance && BaseStageScript.Instance.IsPaused;

        /// <summary>BaseStageScript.Pause prefix.</summary>
        public static void BeforePause(BaseStageScript stage)
        {
            if (stage == null) return;
            PauseWithTimeScale(stage) = !Active;
        }

        /// <summary>Player.Hurt prefix: softer hits while in a menu (time keeps running in co-op).</summary>
        public static void ScaleMenuDamage(ref Logic.DamageData damage)
        {
            if (InMenu) damage.Damage.Damage *= MenuDamageMultiplier;
        }
    }

    /// <summary>Host-side damage source standing for a remote player (kill credit, aggro).</summary>
    internal sealed class NetPlayerSource : IDamageSource
    {
        public Character Character { get; private set; }
        public bool IsCloseQuarters => false;
        public bool IsTrap => false;
        public float ProcDmgMultiplier => 1f;
        public Vector3 Position { get; private set; }
        public void DidHurt(Character damageReceiver, float damage, bool didHit) { }
        public void DidKill(Character damageReceiver) { }

        public static NetPlayerSource For(CSteamID who, Vector3 pos) =>
            new NetPlayerSource { Character = RemotePlayers.GetCharacter(who), Position = pos };
    }
}
