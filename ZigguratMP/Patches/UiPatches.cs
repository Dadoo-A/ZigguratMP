using HarmonyLib;
using UnityEngine;
using ZigguratMP.Sync;
using ZigguratMP.UI;

namespace ZigguratMP.Patches
{
    /// <summary>Ally markers on every minimap / big map instance.</summary>
    [HarmonyPatch(typeof(Minimap), "Start")]
    internal static class MinimapStartPatch
    {
        private static void Postfix(Minimap __instance)
        {
            if (__instance.GetComponent<MinimapAllies>() == null) __instance.gameObject.AddComponent<MinimapAllies>();
        }
    }

    /// <summary>Downed players can't move but may still turn left/right (pitch is not gated by the game).</summary>
    [HarmonyPatch(typeof(MilkFirstPersonController), "UpdateLook")]
    internal static class DownedLookPatch
    {
        private static void Postfix(MilkFirstPersonController __instance)
        {
            if (!DownedSync.LocalDowned || !__instance.enabled || __instance.EnableInput) return;
            Player p = LocalPlayerAccess.Get();
            if (p == null || p.Controller != __instance || __instance.Input == null) return;
            float yaw = __instance.Input.YawLook;
            if (yaw != 0f)
                __instance.CharacterController.SetRotation(__instance.transform.rotation * Quaternion.AngleAxis(yaw, Vector3.up));
        }
    }
}
