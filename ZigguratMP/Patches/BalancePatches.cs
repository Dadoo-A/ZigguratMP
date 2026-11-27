using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Logic;
using Logic.Campaign;
using UnityEngine;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    // ---------------------------------------------------------------- host briefing -> clients

    [HarmonyPatch]
    internal static class BriefingInitPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("CampaignMissionBriefingScreen"), "Init");
        private static void Postfix(object[] __args) => ReadyRoom.HostOpenedBriefing(__args.Length > 0 ? __args[0] : null);
    }

    [HarmonyPatch]
    internal static class ExtraModesSelectInitPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("ExtraModesSelectCharacter"), "Init");
        private static void Postfix(object[] __args) { if (__args.Length > 0 && __args[0] != null) ReadyRoom.HostOpenedExtraModes((int)__args[0]); }
    }

    // ---------------------------------------------------------------- 13. leaderboards

    [HarmonyPatch(typeof(ExtraModesData), "SendLeaderboardData")]
    internal static class LeaderboardPatch
    {
        private static bool Prefix()
        {
            if (!BalanceSync.CoopRun) return true;
            Plugin.Log.LogInfo("Co-op run: leaderboard submission skipped");
            return false;
        }
    }

    // ---------------------------------------------------------------- 12. shared economy

    [HarmonyPatch]
    internal static class FountainUsePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("FountainScript"), "Interact");
        private static void Postfix(Component __instance, bool __result) => BalanceSync.OnLocalEconomyUse(BalanceSync.EcoFountain, __instance, __result);
    }

    [HarmonyPatch(typeof(ShrineScript), nameof(ShrineScript.Interact))]
    internal static class ShrineUsePatch
    {
        private static void Postfix(ShrineScript __instance, bool __result) => BalanceSync.OnLocalEconomyUse(BalanceSync.EcoShrine, __instance, __result);
    }

    [HarmonyPatch(typeof(ShopScript), nameof(ShopScript.Interact))]
    internal static class ShopUsePatch
    {
        private static void Postfix(ShopScript __instance, bool __result) => BalanceSync.OnLocalEconomyUse(BalanceSync.EcoShop, __instance, __result);
    }

    // ---------------------------------------------------------------- 10. enemy run modifiers

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.ApplyRunModifier))]
    internal static class RunModifierPatch
    {
        private static void Postfix(RunModifierPair pair) => BalanceSync.OnLocalRunModifier(pair);
    }

    // ---------------------------------------------------------------- 8-9. turrets, skills, explosions

    [HarmonyPatch]
    internal static class TurretAmuletPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.Inner(AccessTools.TypeByName("TurretAmulet"), "Instance"), "Bullet_OnExplode");

        private static void Postfix(object __instance, Projectile arg1, Vector3 arg2, Vector3 impactNormal, bool isHardSurface)
        {
            Vector3 pos = isHardSurface ? arg2 + impactNormal * 0.5f : arg2;
            SummonSync.OnLocalTurret(__instance, pos, arg1 != null ? arg1.transform.forward : Vector3.forward);
        }
    }

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.ActivateSkill))]
    internal static class SkillActivatePatch
    {
        private static void Postfix(bool __result) => SummonSync.OnLocalSkill(__result);
    }

    [HarmonyPatch(typeof(ExplosionDefinition), nameof(ExplosionDefinition.Explode))]
    internal static class ExplosionFxPatch
    {
        private static void Postfix(ExplosionDefinition __instance, Vector3 position, Vector3 forwardVector, ElementalDamage damage,
            IDamageSource damageSource, Team team, Vector3? explosionNormal, bool isHardSurface)
            => SummonSync.OnExplode(__instance, position, forwardVector, damage, damageSource, team, explosionNormal, isHardSurface);
    }
}
