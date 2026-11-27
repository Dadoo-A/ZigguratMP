using HarmonyLib;
using Steamworks;
using UnityEngine;
using ZigguratMP.Net;

namespace ZigguratMP.Sync
{
    /// <summary>
    /// The game stops time (timeScale 0) for the pause menu, level-up perks, map, omens and the
    /// character sheet. In co-op that would freeze the host's enemies for everyone (or desync a
    /// client), so menus keep time running; a player in a menu takes no damage or knockback and shows
    /// [MENU] to the others.
    /// </summary>
    internal static class CoopPause
    {
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

        private static Vector3 lastGoodPos;
        private static bool hasGoodPos;
        private static Player watched;

        /// <summary>
        /// Safety net: if the local player's position ever becomes NaN/Infinity (white screen,
        /// stuck player), put them back where they last were valid.
        /// </summary>
        public static void WatchPlayer()
        {
            Player p = LocalPlayerAccess.Get();
            if (p == null) { hasGoodPos = false; watched = null; return; }
            if (p != watched) { watched = p; hasGoodPos = false; }
            Vector3 pos = p.transform.position;
            bool bad = float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) || float.IsInfinity(pos.sqrMagnitude);
            if (!bad) { lastGoodPos = pos; hasGoodPos = true; return; }
            if (!hasGoodPos || p.Controller == null) return;
            Plugin.Log.LogWarning($"Local player position became invalid ({pos}); restoring {lastGoodPos}");
            p.Controller.Teleport(lastGoodPos + Vector3.up * 0.2f);
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
