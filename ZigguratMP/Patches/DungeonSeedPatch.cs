using HarmonyLib;
using Logic.Campaign;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    /// <summary>
    /// Floor layout = f(DungeonBuilder.Parameters). Seed comes from Mission.RandSeed, but the boss
    /// pick (unlock tier) and Force*/ExtraSpecialRooms (player perks) are per-save, so the client
    /// copies the host's builder inputs for each floor.
    /// </summary>
    [HarmonyPatch(typeof(DungeonBuilder), nameof(DungeonBuilder.GenerateDungeon))]
    internal static class DungeonSeedPatch
    {
        private static void Prefix(DungeonBuilder __instance)
        {
            NetEnemies.OnFloorGenerated();
            if (Session.IsClient && __instance.Parameters != null && !StageParameters.IsTutorial)
                RunSync.ApplyFloorOverride(__instance.Parameters);
        }

        private static void Postfix(DungeonBuilder __instance, DungeonModel __result)
        {
            if (Session.IsHost && __result != null && !StageParameters.IsTutorial)
                RunSync.OnHostFloorGenerated(__instance.Parameters, __result.Seed);
        }
    }

    /// <summary>Host starts a campaign run (story / random / linear / gauntlet).</summary>
    [HarmonyPatch(typeof(CampaignData), nameof(CampaignData.LoadMission))]
    internal static class CampaignStartPatch
    {
        private static void Postfix() { if (!ReadyRoom.HostWaiting) RunSync.OnHostRunStarted(); }
    }

    /// <summary>Host starts an extra-mode run (classic / endless / daily / gauntlet).</summary>
    [HarmonyPatch(typeof(ExtraModesData), nameof(ExtraModesData.LoadGame))]
    internal static class ExtraModesStartPatch
    {
        private static void Postfix() { if (!ReadyRoom.HostWaiting) RunSync.OnHostRunStarted(); }
    }
}
