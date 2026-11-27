using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Logic;
using UnityEngine;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    [HarmonyPatch(typeof(Bullet), nameof(Bullet.Init))]
    internal static class BulletInitPatch
    {
        private static void Postfix(Bullet __instance, BulletDefinition definition, ElementalDamage damage, IDamageSource damageSource, Quaternion attackRotation, int bulletIndex)
            => ShotSync.OnBulletInit(__instance, definition, damage, damageSource, attackRotation, bulletIndex);
    }

    [HarmonyPatch(typeof(Beam), nameof(Beam.Init))]
    internal static class BeamInitPatch
    {
        private static void Postfix(Beam __instance, BeamDefinition definition, ElementalDamage damagePerSecond, IDamageSource damageSource)
            => ShotSync.OnBeamInit(__instance, definition, damagePerSecond, damageSource);
    }

    [HarmonyPatch(typeof(Player), "WeaponShoot")]
    internal static class PlayerWeaponShootPatch
    {
        private static void Postfix(int attackIndex) => ShotSync.OnLocalWeaponShoot(attackIndex);
    }

    /// <summary>
    /// Replayed (remote) projectiles never deal damage, on any kind of character
    /// (Enemy.Hurt is handled in <see cref="EnemyHurtPatch"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class ReplayHurtBlockPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Assembly game = typeof(Character).Assembly;
            foreach (System.Type t in game.GetTypes())
            {
                if (!typeof(Character).IsAssignableFrom(t) || t == typeof(Enemy)) continue;
                MethodInfo m = t.GetMethod("Hurt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(DamageData) }, null);
                if (m != null && !m.IsAbstract) yield return m;
            }
        }

        private static bool Prefix(DamageData damage, ref bool __result)
        {
            if (!ShotSync.IsReplay(damage)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Remote weapon models keep a disabled WeaponAnimator with no Weapon: skip its teardown.</summary>
    [HarmonyPatch(typeof(WeaponAnimator), "OnDestroy")]
    internal static class WeaponAnimatorOnDestroyPatch
    {
        private static readonly AccessTools.FieldRef<WeaponAnimator, Weapon> WeaponRef =
            AccessTools.FieldRefAccess<WeaponAnimator, Weapon>("weapon");

        private static bool Prefix(WeaponAnimator __instance) => WeaponRef(__instance) != null;
    }

    [HarmonyPatch(typeof(RoomScript), nameof(RoomScript.PlayerEntered))]
    internal static class RoomPlayerEnteredPatch
    {
        private static void Postfix(RoomScript __instance) => RoomSync.OnLocalRoomEntered(__instance);
    }
}
