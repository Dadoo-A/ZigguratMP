using HarmonyLib;
using Logic;
using ZigguratMP.Sync;

namespace ZigguratMP.Patches
{
    [HarmonyPatch(typeof(BreakableCharacter), nameof(BreakableCharacter.Hurt))]
    internal static class BreakableHurtPatch
    {
        private static void Postfix(BreakableCharacter __instance, DamageData damage) => WorldSync.OnLocalBreak(__instance, damage);
    }
}
