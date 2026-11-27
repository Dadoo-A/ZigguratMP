using HarmonyLib;
using Logic;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    [HarmonyPatch(typeof(PlayerData), "Die", typeof(IDamageSource))]
    internal static class PlayerDataDiePatch
    {
        private static bool Prefix(PlayerData __instance) => DownedSync.BeforeDie(__instance);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Hurt))]
    internal static class PlayerHurtDownedPatch
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (!DownedSync.BlockHurt(__instance)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.SetTriggerValue))]
    internal static class WeaponTriggerDownedPatch
    {
        private static void Prefix(ref bool value)
        {
            if (DownedSync.BlockShooting) value = false;
        }
    }

    /// <summary>Client puppets: status effects come from the host; never tick (DOT, expiry) locally.</summary>
    [HarmonyPatch(typeof(EnemyData), nameof(EnemyData.Update))]
    internal static class EnemyDataUpdatePatch
    {
        private static readonly AccessTools.FieldRef<EnemyData, Enemy> EnemyRef = AccessTools.FieldRefAccess<EnemyData, Enemy>("enemy");

        private static bool Prefix(EnemyData __instance) => !NetEnemies.IsPuppet(EnemyRef(__instance), out _);
    }

    /// <summary>Client puppets: boss stage transitions are driven by the host (no local end / spawns).</summary>
    [HarmonyPatch]
    internal static class StageTransitionUpdatePatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.Inner(typeof(EnemyStageTransitionData), "Instance"), "Update");

        private static readonly System.Reflection.FieldInfo EnemyField =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyStageTransitionData), "Instance"), "enemy");

        private static bool Prefix(object __instance, ref bool __result)
        {
            if (!(EnemyField?.GetValue(__instance) is Enemy e) || !NetEnemies.IsPuppet(e, out _)) return true;
            __result = false;
            return false;
        }
    }
}
