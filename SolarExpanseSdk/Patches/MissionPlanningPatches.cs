using CustomUpdate;
using Game;
using Game.Info;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.UI.Windows.Elements.PlanMissionElements.PMScheduleElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Manager;
using SolarExpanseSdk.Core;
using System;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class MissionPlanningPatches
{
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetPMParameterForCodeJobSystem))]
    [HarmonyPrefix]
    private static void SetPMParameterForCodeJobSystemPrefix(PMMissionParameter _pmMissionParameter, ref Action result)
    {
        var context = BuildContext(_pmMissionParameter, "code-job");
        SolarSdk.MissionPlanning.RaiseBeforeCodeJobPlan(_pmMissionParameter, context);

        var original = result;
        result = () =>
        {
            SolarSdk.MissionPlanning.RaiseBeforeCodeJobCallback(_pmMissionParameter, context);
            try
            {
                original?.Invoke();
            }
            finally
            {
                SolarSdk.MissionPlanning.RaiseAfterCodeJobCallback(_pmMissionParameter, context);
            }
        };
    }

    [HarmonyPatch(typeof(PMTabSchedule), nameof(PMTabSchedule.ButtonFastestClickButton))]
    [HarmonyPrefix]
    private static void ButtonFastestClickButtonPrefix(PMTabSchedule __instance)
    {
        var pmp = __instance?.PlanMissionWindow?.PMMissionParameter;
        SolarSdk.MissionPlanning.RaiseBeforeFastestSearch(__instance, pmp, BuildContext(pmp, "fastest"));
    }

    [HarmonyPatch(typeof(PMTabSchedule), nameof(PMTabSchedule.ButtonFastestClickButton))]
    [HarmonyPostfix]
    private static void ButtonFastestClickButtonPostfix(PMTabSchedule __instance)
    {
        var pmp = __instance?.PlanMissionWindow?.PMMissionParameter;
        SolarSdk.MissionPlanning.RaiseAfterFastestSearch(pmp, BuildContext(pmp, "fastest"));
    }

    [HarmonyPatch(typeof(PMTabSchedule), "CreateFly")]
    [HarmonyPostfix]
    private static void CreateFlyPostfix(PMTabSchedule __instance)
    {
        var pmp = __instance?.PlanMissionWindow?.PMMissionParameter;
        SolarSdk.MissionPlanning.RaiseAfterCreateFly(__instance, pmp, BuildContext(pmp, "create-fly"));
    }

    [HarmonyPatch(typeof(PMTabSchedule), "CreatedTrajectory")]
    [HarmonyPrefix]
    private static bool CreatedTrajectoryPrefix(PMTabSchedule __instance)
    {
        var pmp = __instance?.PlanMissionWindow?.PMMissionParameter;
        return !SolarSdk.MissionPlanning.ShouldSuppressPreviewTrajectory(__instance, pmp, BuildContext(pmp, "preview-trajectory"));
    }

    [HarmonyPatch(typeof(MissionInfo), nameof(MissionInfo.Complete))]
    [HarmonyPostfix]
    private static void MissionInfoCompletePostfix(MissionInfo __instance)
    {
        SolarSdk.MissionPlanning.RaiseMissionCompleted(__instance);
    }

    [HarmonyPatch(typeof(PMMissionParameter), nameof(PMMissionParameter.CheckLVFullListOrNone))]
    [HarmonyPrefix]
    private static bool CheckLVFullListOrNonePrefix(PMMissionParameter __instance, ref bool __result)
    {
        var result = SolarSdk.MissionPlanning.RaiseCheckSelfLaunchOverride(__instance);
        if (!result.HasValue)
            return true;

        __result = result.Value;
        return false;
    }

    [HarmonyPatch(typeof(Spacecraft), "ShowNotificationLand")]
    [HarmonyPrefix]
    private static bool ShowNotificationLandPrefix(Spacecraft __instance)
    {
        return !SolarSdk.MissionPlanning.ShouldSuppressArrivalNotification(__instance, "land");
    }

    [HarmonyPatch(typeof(Spacecraft), "ShowNotificationAsteroidImpact")]
    [HarmonyPrefix]
    private static bool ShowNotificationAsteroidImpactPrefix(Spacecraft __instance)
    {
        return !SolarSdk.MissionPlanning.ShouldSuppressArrivalNotification(__instance, "asteroid-impact");
    }

    [HarmonyPatch(typeof(Spacecraft), "ShowNotificationSolarSystem")]
    [HarmonyPrefix]
    private static bool ShowNotificationSolarSystemPrefix(Spacecraft __instance)
    {
        return !SolarSdk.MissionPlanning.ShouldSuppressArrivalNotification(__instance, "solar-system");
    }

    [HarmonyPatch(typeof(Spacecraft), "ShowNotificationAsteroidPull")]
    [HarmonyPrefix]
    private static bool ShowNotificationAsteroidPullPrefix(Spacecraft __instance)
    {
        return !SolarSdk.MissionPlanning.ShouldSuppressArrivalNotification(__instance, "asteroid-pull");
    }

    private static Services.MissionPlanContext BuildContext(PMMissionParameter pmp, string tag)
    {
        return new Services.MissionPlanContext
        {
            Source = pmp?.Start?.ObjectName,
            Target = pmp?.Target?.ObjectName,
            Tag = tag
        };
    }
}
