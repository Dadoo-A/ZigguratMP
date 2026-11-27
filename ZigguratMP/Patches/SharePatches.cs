using HarmonyLib;
using Logic;
using Logic.Campaign;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.EarnXP))]
    internal static class EarnXPPatch
    {
        private static void Postfix(PlayerData __instance, uint experience) => SharedProgress.AfterEarnXP(__instance, experience);
    }

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.PickupKey))]
    internal static class PickupKeyPatch
    {
        private static void Postfix(PlayerData __instance, KeyPickupScript key, bool isSecret) => SharedProgress.AfterPickupKey(__instance, key, isSecret);
    }

    /// <summary>
    /// Client: the game's own mission start (briefing screen → Start) joins the host's run with
    /// the character + weapons picked there.
    /// </summary>
    [HarmonyPatch(typeof(CampaignData), nameof(CampaignData.LoadMission))]
    internal static class ClientCampaignStartPatch
    {
        private static bool Prefix(CampaignData __instance, MissionData mission)
        {
            if (RunSync.TryJoinWithLoadoutOf(mission)) return false;
            return ReadyRoom.HostBeforeStart(() => __instance.LoadMission(mission), mission);
        }
    }

    [HarmonyPatch(typeof(ExtraModesData), nameof(ExtraModesData.LoadGame))]
    internal static class ClientExtraModesStartPatch
    {
        private static bool Prefix(ExtraModesData __instance, MissionData mission)
        {
            if (RunSync.TryJoinWithLoadoutOf(mission)) return false;
            return ReadyRoom.HostBeforeStart(() => __instance.LoadGame(mission), mission);
        }
    }
}
