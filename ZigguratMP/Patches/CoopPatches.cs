using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Logic;
using Logic.Campaign;
using UnityEngine;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    // ---------------------------------------------------------------- 1. time never stops

    [HarmonyPatch(typeof(BaseStageScript), nameof(BaseStageScript.Pause))]
    internal static class CoopPausePatch
    {
        private static void Prefix(BaseStageScript __instance) => CoopPause.BeforePause(__instance);
    }

    /// <summary>
    /// In co-op a menu doesn't stop time, but the game never expects hits while paused (a hit with
    /// the map open broke the player). Players in a menu take no damage and no knockback.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Hurt))]
    internal static class MenuDamagePatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!CoopPause.InMenu) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ApplyImpulse))]
    internal static class MenuImpulsePatch
    {
        private static bool Prefix(Vector3 vector) =>
            !CoopPause.InMenu && !(float.IsNaN(vector.x) || float.IsNaN(vector.y) || float.IsNaN(vector.z));
    }

    // ---------------------------------------------------------------- 2-3. gathering

    [HarmonyPatch(typeof(DungeonLogic), nameof(DungeonLogic.ExitRoom))]
    internal static class ExitRoomPatch
    {
        private static void Postfix() => GatherSync.OnLocalRoomExit();
    }

    [HarmonyPatch(typeof(StageScript), nameof(StageScript.StartBossCutscene))]
    internal static class BossCutsceneGatherPatch
    {
        private static void Postfix() => GatherSync.HostOnBossCutscene();
    }

    // ---------------------------------------------------------------- 4. special fight objectives

    [HarmonyPatch(typeof(Obelisk), nameof(Obelisk.Hurt))]
    internal static class ObeliskHurtPatch
    {
        private static void Prefix(Obelisk __instance, DamageData damage) => ObjectiveSync.OnLocalObeliskHurt(__instance, damage);
    }

    [HarmonyPatch(typeof(Obelisk), "Die")]
    internal static class ObeliskDiePatch
    {
        private static void Postfix(Obelisk __instance) => ObjectiveSync.OnObeliskDied(__instance);
    }

    [HarmonyPatch(typeof(SequenceItem), nameof(SequenceItem.Interact))]
    internal static class SequenceInteractPatch
    {
        private static void Prefix(SequenceItem __instance) => ObjectiveSync.OnLocalSequenceInteract(__instance);
    }

    [HarmonyPatch(typeof(PickupScript), "BeginPickup")]
    internal static class PickupBeginPatch
    {
        private static void Postfix(PickupScript __instance) => ObjectiveSync.OnLocalPickup(__instance);
    }

    /// <summary>Overload items charge when any player (not just the local one) is near.</summary>
    [HarmonyPatch(typeof(OverloadItem), nameof(OverloadItem.Update))]
    internal static class OverloadDistancePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo distance = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance));
            MethodInfo ours = AccessTools.Method(typeof(ObjectiveSync), nameof(ObjectiveSync.DistanceToNearestPlayer));
            int replaced = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(distance)) { ci.opcode = OpCodes.Call; ci.operand = ours; replaced++; }
                yield return ci;
            }
            if (replaced == 0) Plugin.Log.LogWarning("OverloadItem.Update: distance call not found (overload stays local-player only)");
        }
    }

    // ---------------------------------------------------------------- 5. portal

    [HarmonyPatch(typeof(PortalScript), nameof(PortalScript.Interact))]
    internal static class PortalInteractPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (PortalSync.BeforePortalInteract()) return true;
            __result = false;
            return false;
        }
    }

    // ---------------------------------------------------------------- 6. campaign safety for clients

    /// <summary>
    /// A client plays the host's mission inside their own campaign. If it is a story mission the
    /// client hasn't reached, completing it must not advance (or skip) the client's own story:
    /// rewards are kept, the story step is not.
    /// </summary>
    [HarmonyPatch(typeof(CampaignData), nameof(CampaignData.MissionComplete))]
    internal static class ClientMissionCompletePatch
    {
        private static readonly MethodInfo StorySetter = AccessTools.PropertySetter(typeof(MissionData), nameof(MissionData.StoryMission));

        private static void Prefix(CampaignData __instance, CampaignMissionResult result)
        {
            if (!RunSync.PlayedHostRun || result?.MissionData?.StoryMission == null || StorySetter == null) return;
            MissionDefinition story = result.MissionData.StoryMission;
            bool mine = __instance.CompletedStoryMissions.Contains(story) || __instance.GetPendingStoryMissions(false).Contains(story);
            if (mine) return;
            Plugin.Log.LogInfo($"[client] Host's story mission {story.FullId} isn't part of your campaign yet: rewards kept, story not advanced");
            StorySetter.Invoke(result.MissionData, new object[] { null });
        }
    }

    // ---------------------------------------------------------------- 7. client skill effects on host enemies

    /// <summary>Knockback / pushes on a puppet go to the host (puppet position comes from the host).</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyImpulse))]
    internal static class PuppetImpulsePatch
    {
        private static bool Prefix(Enemy __instance, Vector3 impulse, bool isKnockback)
        {
            if (!NetEnemies.IsPuppet(__instance, out NetEnemy ne)) return true;
            if (isKnockback) NetEnemies.ClientForwardImpulse(ne, impulse, true);
            return false;
        }
    }

    /// <summary>Status effects a client applies directly (skills) are applied on the host too.</summary>
    [HarmonyPatch(typeof(EnemyData), nameof(EnemyData.StartStatusEffect))]
    internal static class PuppetStatusPatch
    {
        private static readonly AccessTools.FieldRef<EnemyData, Enemy> EnemyRef = AccessTools.FieldRefAccess<EnemyData, Enemy>("enemy");

        private static void Prefix(EnemyData __instance, EnemyStatusEffects statusEffect)
        {
            if (NetEnemies.ApplyingRemoteStatus) return;
            if (NetEnemies.IsPuppet(EnemyRef(__instance), out NetEnemy ne)) NetEnemies.ClientForwardStatus(ne, statusEffect);
        }
    }
}
