using Game;
using Game.Info;
using Game.UI.Windows.Elements.PlanMissionElements;
using HarmonyLib;
using SolarExpanseSdk.Core;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class CyclicalMissionPatches
{
    [HarmonyPatch(typeof(SpaceCraftCyclicalMissionController), nameof(SpaceCraftCyclicalMissionController.TryPlanCycleMission))]
    [HarmonyPrefix]
    private static bool TryPlanCycleMissionPrefix(SpaceCraftCyclicalMissionController __instance)
    {
        return !SolarSdk.CyclicalMissions.ShouldSuppressTryPlanCycleMission(__instance);
    }

    [HarmonyPatch(typeof(SpaceCraftCyclicalMissionController), nameof(SpaceCraftCyclicalMissionController.ShowNotification))]
    [HarmonyPrefix]
    private static bool ShowNotificationPrefix(SpaceCraftCyclicalMissionController __instance, ObjectInfo start, PMMissionParameter PMMissionParameter)
    {
        return !SolarSdk.CyclicalMissions.ShouldSuppressCycleNotification(__instance, start, PMMissionParameter);
    }
}
