using System.Collections.Generic;
using HarmonyLib;
using Logic;
using UnityEngine;
using ZigguratMP.Net;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    /// <summary>Client: the host owns all enemies. Block every local spawn except our puppets.</summary>
    [HarmonyPatch(typeof(StageScript), nameof(StageScript.SpawnEnemy))]
    internal static class SpawnEnemyPatch
    {
        private static bool Prefix()
        {
            if (!Session.IsClient || NetEnemies.AllowLocalSpawn || StageParameters.IsTutorial) return true;
            return false;
        }
    }

    /// <summary>Host: register + broadcast every enemy once the game has initialised it.</summary>
    [HarmonyPatch(typeof(StageScript), "EnemySpawned")]
    internal static class EnemySpawnedPatch
    {
        private static void Postfix(GameObject enemyGO, EnemyDefinition definition, bool isBoss)
        {
            if (!Session.IsHost || StageParameters.IsTutorial || enemyGO == null) return;
            NetEnemies.HostOnEnemySpawned(enemyGO.GetComponent<Enemy>(), definition, isBoss);
        }
    }

    /// <summary>Host: replicate attack choice so clients replay it (bullets included).</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.StartAttack), typeof(EnemyAttackDefinition.EnemyAttack), typeof(Character))]
    internal static class EnemyStartAttackPatch
    {
        private static void Postfix(Enemy __instance, EnemyAttackDefinition.EnemyAttack attack, Character target)
        {
            if (Session.IsHost) NetEnemies.HostOnEnemyAttack(__instance, attack, target);
        }
    }

    /// <summary>
    /// Client puppets: skip AI while idle/moving (we drive them from snapshots); let the game run
    /// while spawning, attacking (replayed attack) or dying, then correct the position.
    /// </summary>
    [HarmonyPatch(typeof(Enemy), "Update")]
    internal static class EnemyUpdatePatch
    {
        private static bool Prefix(Enemy __instance, out bool __state)
        {
            __state = false;
            if (!NetEnemies.IsPuppet(__instance, out NetEnemy ne)) return true;
            if (__instance.Data == null || !BaseStageScript.HasInstance) return true;

            if (__instance.State == Enemy.EnemyState.Spawning || __instance.State == Enemy.EnemyState.Dead
                || __instance.MovementState == Enemy.EnemyMovementState.Attacking)
            {
                __state = true;
                return true;
            }

            NetEnemies.DrivePuppet(__instance, ne);
            return false;
        }

        private static void Postfix(Enemy __instance, bool __state)
        {
            if (__state && NetEnemies.IsPuppet(__instance, out NetEnemy ne))
                NetEnemies.CorrectPuppet(__instance, ne);
        }
    }

    [HarmonyPatch(typeof(Enemy), "DetermineNextMovement")]
    internal static class EnemyDetermineNextMovementPatch
    {
        private static bool Prefix(Enemy __instance, ref bool __result)
        {
            if (!NetEnemies.IsPuppet(__instance, out _)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.CanAttack))]
    internal static class EnemyCanAttackPatch
    {
        private static bool Prefix(Enemy __instance, ref bool __result)
        {
            if (!NetEnemies.IsPuppet(__instance, out _)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Client puppets die only when the host says so.</summary>
    [HarmonyPatch(typeof(Enemy), "Die", typeof(DeathReason))]
    internal static class EnemyDiePatch
    {
        private static bool Prefix(Enemy __instance)
        {
            return !NetEnemies.IsPuppet(__instance, out NetEnemy ne) || ne.AllowDeath;
        }
    }

    /// <summary>Client: damage on a puppet is sent to the host instead of applied locally.</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.Hurt))]
    internal static class EnemyHurtPatch
    {
        private static bool Prefix(Enemy __instance, DamageData damage, ref bool __result)
        {
            if (ShotSync.IsReplay(damage)) { __result = false; return false; } // remote shot replay: visual only
            if (!NetEnemies.IsPuppet(__instance, out NetEnemy ne))
            {
                if (damage.DamageSource?.Character is Player) NetEnemies.HostNoteLocalHit(__instance);
                return true;
            }
            if (damage.DamageSource != null && damage.DamageSource.Character is Player) ne.LastLocalSource = damage.DamageSource;
            if (damage.Team == Team.Enemy) { __result = false; return false; } // enemy friendly fire: host decides

            __result = __instance.IsAlive;
            if (__result)
            {
                NetEnemies.ClientForwardHurt(ne, damage);
                NetEnemies.ClientLocalHitFeedback(__instance, damage);
            }
            return false;
        }
    }

    /// <summary>
    /// Host: most enemies "fixate on player" and drop every target that is not a <see cref="Player"/>
    /// (meant to ignore turrets/summons). Remote players must count as players.
    /// </summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.GetTargets))]
    internal static class EnemyGetTargetsPatch
    {
        private static readonly AccessTools.FieldRef<Enemy, bool> FixateRef =
            AccessTools.FieldRefAccess<Enemy, bool>("fixateOnPlayer");

        private static bool Prefix(Enemy __instance, ref IEnumerable<Character> __result)
        {
            if (!Session.IsHost || LocalPlayerAccess.Logic == null) return true;
            __result = Targets(__instance, FixateRef(__instance));
            return false;
        }

        private static IEnumerable<Character> Targets(Enemy e, bool fixate)
        {
            foreach (Character c in LocalPlayerAccess.Logic.GetEnemyTargets(e.Team))
            {
                if (c == null) continue;
                if (c is Player && DownedSync.LocalDowned) continue;
                if (c is RemotePlayerCharacter rp && rp.Downed) continue;
                if (fixate && e.Team == Team.Enemy && !(c is Player) && !(c is RemotePlayerCharacter)) continue;
                yield return c;
            }
        }
    }
}
