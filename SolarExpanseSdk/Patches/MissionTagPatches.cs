using System.Collections.Generic;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.MissionsElements;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.UI.Windows.Elements.PlanMissionElements.PMScheduleElements;
using Game.UI.Windows.Windows;
using Game.VisualizationScripts;
using HarmonyLib;
using Manager;
using SolarExpanseSdk.Core;
using SolarExpanseSdk.Services;
using TMPro;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class MissionTagPatches
{
    [HarmonyPatch(typeof(PMMissionParameter), nameof(PMMissionParameter.ChangeMissionName))]
    [HarmonyPrefix]
    private static void PMMissionParameterChangeMissionNamePrefix(PMMissionParameter __instance, ref string _missionName)
    {
        if (string.IsNullOrEmpty(_missionName) || !_missionName.StartsWith("Cyclical missions", System.StringComparison.Ordinal))
            return;

        var name = SolarSdk.MissionTags.ResolveName(__instance);
        if (!string.IsNullOrEmpty(name))
            _missionName = name;
    }

    [HarmonyPatch(typeof(PMTabDestination), nameof(PMTabDestination.ChangeMissionName), new System.Type[] { })]
    [HarmonyPostfix]
    private static void PMTabDestinationChangeMissionNamePostfix(PMTabDestination __instance)
    {
        var pmw = Traverse.Create(__instance).Field("planMissionWindow").GetValue<PlanMissionWindow>();
        var pmp = pmw?.PMMissionParameter;
        if (pmp == null)
            return;

        SolarSdk.MissionTags.ApplyMissionParameterName(pmp, "destination-change");
    }

    [HarmonyPatch(typeof(PMTabSchedule), "CreateFly")]
    [HarmonyPrefix]
    private static bool PMTabScheduleCreateFlyPrefix(PMTabSchedule __instance)
    {
        var pmw = Traverse.Create(__instance).Field("planMissionWindow").GetValue<PlanMissionWindow>();
        var pmp = pmw?.PMMissionParameter;
        SolarSdk.MissionTags.ApplyMissionParameterName(pmp, "create-fly");
        return !SolarSdk.MissionPlanning.ShouldSuppressCreateFly(__instance, pmp, new MissionPlanContext
        {
            Source = pmp?.Start?.ObjectName,
            Target = pmp?.Target?.ObjectName,
            Tag = "create-fly",
            DispatchId = SolarSdk.CyclicalMissions.FindDispatchId(pmp)
        });
    }

    [HarmonyPatch(typeof(PMTabSchedule), nameof(PMTabSchedule.OnClickScheduleButtonForCode))]
    [HarmonyPostfix]
    private static void OnClickScheduleButtonForCodePostfix(ref MissionInfo __result)
    {
        if (__result == null)
            return;

        SolarSdk.MissionTags.ApplyMissionInfoName(__result, null, "schedule-code-postfix");
    }

    [HarmonyPatch(typeof(MissionInfoManager), nameof(MissionInfoManager.CreateMissionInfo))]
    [HarmonyPrefix]
    private static void CreateMissionInfoPrefix(ISpacecraftInfo spacecraftInfo, List<ISpacecraftInfo> listSC,
        MissionInfo.EMissionCreator missionCreator, CargoAll cargoAll, Company company,
        TrajectoryObject trajectoryObject, ref string missionName, ref string __state)
    {
        __state = null;
        var name = SolarSdk.MissionTags.ResolveName(new SdkMissionNameContext
        {
            SpacecraftInfo = spacecraftInfo,
            SpacecraftInfos = listSC,
            CargoAll = cargoAll,
            Company = company,
            TrajectoryObject = trajectoryObject,
            MissionCreator = missionCreator,
            ExistingName = missionName,
            Start = trajectoryObject?.StartObjectInfo,
            Target = trajectoryObject?.EndObjectInfo
        });
        if (string.IsNullOrEmpty(name))
            return;

        missionName = name;
        __state = name;
    }

    [HarmonyPatch(typeof(MissionInfoManager), nameof(MissionInfoManager.CreateMissionInfo))]
    [HarmonyPostfix]
    private static void CreateMissionInfoPostfix(MissionInfo __result, string __state)
    {
        if (__result == null)
            return;

        SolarSdk.MissionTags.ApplyMissionInfoName(__result, __state, "mission-info-create");
    }

    [HarmonyPatch(typeof(MissionsLabelsMainUI), nameof(MissionsLabelsMainUI.SetData))]
    [HarmonyPostfix]
    private static void MissionsLabelsSetDataPostfix(MissionsLabelsMainUI __instance, MissionInfo mi)
    {
        var name = SolarSdk.MissionTags.ResolveName(mi);
        if (string.IsNullOrEmpty(name))
            return;

        mi.fromCyclicalMission = true;
        var txtMissionName = Traverse.Create(__instance).Field("txtMissionName").GetValue<TextMeshProUGUI>();
        if (txtMissionName != null)
            txtMissionName.text = name;
        SolarSdk.Log.Verbose("sdk.missionPlanning", $"mission-label-name mission={mi.id} name=\"{name}\"");
    }

    [HarmonyPatch(typeof(MissionRow), "SetMissionInfo")]
    [HarmonyPostfix]
    private static void MissionRowSetMissionInfoPostfix(MissionRow __instance, MissionInfo _missionInfo)
    {
        var name = SolarSdk.MissionTags.ResolveName(_missionInfo);
        if (string.IsNullOrEmpty(name))
            return;

        var text = Traverse.Create(__instance).Field("nameTextTextMeshPro").GetValue<TextMeshProUGUI>();
        if (text != null)
            text.text = name;
        _missionInfo.fromCyclicalMission = true;
        SolarSdk.Log.Verbose("sdk.missionPlanning", $"mission-row-name mission={_missionInfo.id} name=\"{name}\"");
    }

    [HarmonyPatch(typeof(MissionRowNew), "SetMissionInfo", new System.Type[] { typeof(MissionInfo), typeof(UnityEngine.Color), typeof(string), typeof(MissionListByType.EMissionType) })]
    [HarmonyPostfix]
    private static void MissionRowNewSetMissionInfoPostfix(MissionInfo _missionInfo)
    {
        var name = SolarSdk.MissionTags.ResolveName(_missionInfo);
        if (string.IsNullOrEmpty(name))
            return;

        _missionInfo.missionName = name;
        _missionInfo.fromCyclicalMission = true;
        SolarSdk.Log.Verbose("sdk.missionPlanning", $"mission-row-new-name mission={_missionInfo.id} name=\"{name}\"");
    }

    [HarmonyPatch(typeof(MissionsLabelsMainUIManager), nameof(MissionsLabelsMainUIManager.RemoveMissions))]
    [HarmonyPrefix]
    private static bool RemoveMissionsSafePrefix(MissionsLabelsMainUIManager __instance, MissionsLabelsMainUI missionsLabelsMainUI)
    {
        if (missionsLabelsMainUI == null)
            return false;

        var missionInfo = missionsLabelsMainUI.MissionInfo;
        if (!SolarSdk.MissionTags.IsTaggedMission(missionInfo))
            return true;

        try
        {
            var labels = Traverse.Create(__instance)
                .Field("listMissionsLabelsMainUI")
                .GetValue<List<MissionsLabelsMainUI>>();
            labels?.Remove(missionsLabelsMainUI);

            var trajectory = missionInfo?.trajectoryObject;
            if (trajectory != null)
                UnityEngine.Object.Destroy(trajectory.gameObject);

            if (missionsLabelsMainUI.gameObject != null)
                UnityEngine.Object.Destroy(missionsLabelsMainUI.gameObject);

            missionInfo?.Complete();
            SolarSdk.Log.Verbose("sdk.missionPlanning", $"mission-label-safe-remove mission={missionInfo?.id ?? -1} name=\"{missionInfo?.missionName ?? "null"}\"");
        }
        catch (System.Exception ex)
        {
            SolarSdk.Log.Warning("sdk.missionPlanning", $"mission-label-safe-remove-error mission={missionInfo?.id ?? -1} error={ex.GetType().Name}: {ex.Message}");
            SolarSdk.Diagnostics.WriteSnapshotOnce("mission-label-remove-error", missionInfo?.id.ToString() ?? "unknown");
        }

        return false;
    }
}
