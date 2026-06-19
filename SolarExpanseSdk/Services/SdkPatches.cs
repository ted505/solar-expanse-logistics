using System;
using System.Collections.Generic;
using System.Reflection;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.MissionsElements;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.UI.Windows.Elements.PlanMissionElements.PMScheduleElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Manager;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Applies SDK Harmony patches and records patch validation/capability diagnostics.
/// Validation is informational so a missing target can be diagnosed without blocking unrelated patches.
/// </summary>
public sealed class SdkPatches
{
    private readonly List<string> _applied = new List<string>();
    private readonly List<string> _failed = new List<string>();
    private SdkLogging _log;

    /// <summary>True when lifecycle patch capabilities are expected to be available.</summary>
    public bool LifecycleAvailable { get; private set; }
    /// <summary>True when object-info UI patch capabilities are expected to be available.</summary>
    public bool ObjectInfoUiAvailable { get; private set; }
    /// <summary>True when mission-planning patch capabilities are expected to be available.</summary>
    public bool MissionPlanningAvailable { get; private set; }

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Validates known stock patch targets, then applies all SDK Harmony patches in the assembly.
    /// </summary>
    public void ApplyAll(Harmony harmony, Assembly assembly)
    {
        try
        {
            ValidateKnownPatchTargets();
        }
        catch (Exception ex)
        {
            _failed.Add($"validate-all: {ex.GetType().Name}: {ex.Message}");
            _log?.Warning("sdk.patches", $"patch validation aborted but PatchAll will continue: {ex}");
        }

        try
        {
            harmony.PatchAll(assembly);
            LifecycleAvailable = true;
            ObjectInfoUiAvailable = true;
            MissionPlanningAvailable = true;
            _applied.Add("assembly-patchall");
        }
        catch (Exception ex)
        {
            _failed.Add($"assembly-patchall: {ex.GetType().Name}: {ex.Message}");
            _log?.Error($"SDK PatchAll failed: {ex}");
        }
    }

    /// <summary>Records an applied patch or validation entry for startup reporting.</summary>
    public void RegisterApplied(string id) => _applied.Add(id);
    /// <summary>Records a failed patch or validation entry for startup reporting.</summary>
    public void RegisterFailed(string id, Exception ex) => _failed.Add($"{id}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// Logs capability flags and all patch validation/application records.
    /// </summary>
    public void LogSummary()
    {
        _log?.Info($"SDK capabilities: lifecycle={LifecycleAvailable} objectInfoUi={ObjectInfoUiAvailable} missionPlanning={MissionPlanningAvailable}");
        foreach (var item in _applied)
            _log?.Info($"SDK patch applied: {item}");
        foreach (var item in _failed)
            _log?.Warning($"SDK patch failed: {item}");
    }

    private void ValidateKnownPatchTargets()
    {
        Validate("lifecycle", typeof(LoadSaveManager), "ExtractAllFromSaveData");
        Validate("lifecycle", typeof(LoadSaveManager), "SaveToFile", new[] { typeof(string) });
        Validate("lifecycle", typeof(TimeController), "Update");
        Validate("objectInfoUi", typeof(ObjectInfoWindow), "Awake");
        Validate("objectInfoUi", typeof(ObjectInfoWindow), "SetData", new[] { typeof(ObjectInfoData), typeof(bool) });
        Validate("objectInfoUi", typeof(ObjectInfoWindow), "RebuildLayout");
        Validate("objectInfoUi", typeof(UIRowRocket), "SetData");
        Validate("missionPlanning", typeof(GameManager), nameof(GameManager.SetPMParameterForCodeJobSystem));
        Validate("missionPlanning", typeof(PMTabSchedule), nameof(PMTabSchedule.ButtonFastestClickButton));
        Validate("missionPlanning", typeof(PMTabSchedule), "CreatedTrajectory");
        Validate("missionPlanning", typeof(MissionInfo), nameof(MissionInfo.Complete));
        Validate("missionPlanning", typeof(PMMissionParameter), nameof(PMMissionParameter.CheckLVFullListOrNone));
        Validate("missionPlanning", typeof(Spacecraft), "ShowNotificationLand");
        Validate("missionPlanning", typeof(Spacecraft), "ShowNotificationAsteroidImpact");
        Validate("missionPlanning", typeof(Spacecraft), "ShowNotificationSolarSystem");
        Validate("missionPlanning", typeof(Spacecraft), "ShowNotificationAsteroidPull");
        Validate("cyclicalMissions", typeof(SpaceCraftCyclicalMissionController), nameof(SpaceCraftCyclicalMissionController.TryPlanCycleMission));
        Validate("cyclicalMissions", typeof(SpaceCraftCyclicalMissionController), nameof(SpaceCraftCyclicalMissionController.ShowNotification));
        Validate("missionTags", typeof(PMMissionParameter), nameof(PMMissionParameter.ChangeMissionName), new[] { typeof(string), typeof(bool) });
        Validate("missionTags", typeof(PMTabDestination), nameof(PMTabDestination.ChangeMissionName), Type.EmptyTypes);
        Validate("missionTags", typeof(PMTabSchedule), "CreateFly");
        Validate("missionTags", typeof(PMTabSchedule), nameof(PMTabSchedule.OnClickScheduleButtonForCode));
        Validate("missionTags", typeof(MissionInfoManager), nameof(MissionInfoManager.CreateMissionInfo));
        Validate("missionTags", typeof(MissionsLabelsMainUI), nameof(MissionsLabelsMainUI.SetData));
        Validate("missionTags", typeof(MissionRow), "SetMissionInfo", new[] { typeof(MissionInfo), typeof(UnityEngine.Color), typeof(string), typeof(MissionListByType.EMissionType) });
        Validate("missionTags", typeof(MissionRowNew), "SetMissionInfo", new[] { typeof(MissionInfo), typeof(UnityEngine.Color), typeof(string), typeof(MissionListByType.EMissionType) });
        Validate("missionLoadout", typeof(ObjectInfoData), nameof(ObjectInfoData.CreatedCargoToTakeNormal));
        Validate("market", typeof(MarketOfferManager), nameof(MarketOfferManager.AddOffer));
    }

    private void Validate(string capability, Type type, string methodName, Type[] args = null)
    {
        try
        {
            var method = args == null
                ? AccessTools.Method(type, methodName)
                : AccessTools.Method(type, methodName, args);
            if (method != null)
            {
                _applied.Add($"validate {capability}:{type.Name}.{methodName}");
                _log?.Verbose("sdk.patches", $"target-found capability={capability} target={type.FullName}.{methodName}");
            }
            else
            {
                _failed.Add($"validate {capability}:{type.Name}.{methodName}: target missing");
                _log?.Warning("sdk.patches", $"target-missing capability={capability} target={type.FullName}.{methodName}");
            }
        }
        catch (Exception ex)
        {
            _failed.Add($"validate {capability}:{type.Name}.{methodName}: {ex.GetType().Name}: {ex.Message}");
            _log?.Warning("sdk.patches", $"target-validation-error capability={capability} target={type.FullName}.{methodName} error={ex.GetType().Name}: {ex.Message}");
        }
    }
}
