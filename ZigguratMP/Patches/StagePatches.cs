using HarmonyLib;
using Logic.Fights;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    [HarmonyPatch(typeof(DungeonLogic), "Fight_OnClear")]
    internal static class FightClearPatch
    {
        private static void Postfix(Fight fight)
        {
            if (Session.IsHost) FightSync.HostOnFightClear(fight);
        }
    }

    [HarmonyPatch(typeof(DungeonLogic), nameof(DungeonLogic.StartFight))]
    internal static class StartFightPatch
    {
        private static void Postfix(RoomScript room, Fight fight)
        {
            if (Session.IsClient) FightSync.ClientOnFightStarted(room, fight);
            else GatherSync.HostOnFightStarted(room, fight);
        }
    }

    /// <summary>Floor exit (boss portal). Host broadcasts; client clears at most once per floor.</summary>
    [HarmonyPatch(typeof(StageScript), nameof(StageScript.LevelCleared))]
    internal static class LevelClearedPatch
    {
        private static bool Prefix() => RunSync.BeforeLevelCleared();
        private static void Postfix() => RunSync.AfterLevelCleared();
    }

    /// <summary>Client: the boss is the host's puppet, not a local spawn.</summary>
    [HarmonyPatch(typeof(StageScript), nameof(StageScript.SpawnBossEnemy))]
    internal static class SpawnBossEnemyPatch
    {
        private static bool Prefix(System.Action<Enemy> callback)
        {
            if (!Session.IsClient || StageParameters.IsTutorial) return true;
            NetEnemies.ClientRequestBoss(callback);
            return false;
        }
    }

    /// <summary>Client: cutscene finished before the host's boss arrived → defer the boss fight.</summary>
    [HarmonyPatch(typeof(RoomScript), nameof(RoomScript.StartBossFight))]
    internal static class StartBossFightPatch
    {
        private static bool Prefix(RoomScript __instance, Enemy bossInstance)
        {
            if (!Session.IsClient || bossInstance != null) return true;
            NetEnemies.ClientDeferBossFight(__instance);
            return false;
        }
    }

    /// <summary>Client waits for the host's next floor before building it.</summary>
    [HarmonyPatch(typeof(StageScript), nameof(StageScript.LoadNextLevel))]
    internal static class LoadNextLevelPatch
    {
        private static bool Prefix(StageScript __instance) => RunSync.BeforeLoadNextLevel(__instance);
    }
}
