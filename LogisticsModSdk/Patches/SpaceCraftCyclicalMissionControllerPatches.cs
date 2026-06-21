using HarmonyLib;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.MissionsElements;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.UI.Windows.Elements.PlanMissionElements.PMScheduleElements;
using Game.UI.Windows.Windows;
using Game.VisualizationScripts;
using LogisticsModSdk.Data;
using LogisticsModSdk.Logic;
using Manager;
using ScriptableObjectScripts;
using SolarExpanseSdk.Core;
using SolarExpanseSdk.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;

namespace LogisticsModSdk.Patches;

internal static class SpaceCraftCyclicalMissionControllerPatches
{
    // Migrated to SolarSdk.MissionPlanning callback events; retained as a private helper
    // during the parity transition but no longer registered as a Harmony patch.
    private static void SetPMParameterForCodeJobSystemPrefix(PMMissionParameter _pmMissionParameter, ref Action result)
    {
        using (LogisticsObserver.TimeScope($"SetPMParameterForCodeJobSystemPrefix {_pmMissionParameter?.Start?.ObjectName ?? "null"}->{_pmMissionParameter?.Target?.ObjectName ?? "null"}"))
        {
        var cmdFromShip = (_pmMissionParameter?.SC as Spacecraft) == null
            ? null
            : MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission((Spacecraft)_pmMissionParameter.SC);
        var isLogi = cmdFromShip?.customNameFromPlanMission != null
            && cmdFromShip.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal);
        if (!isLogi)
            isLogi = LogisticsObserver.IsLogisticsPlan(_pmMissionParameter);
        if (!isLogi
            && cmdFromShip?.customNameFromPlanMission != null
            && cmdFromShip.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            isLogi = true;
            if (LogisticsObserver.VerboseLoggingEnabled)
                LogisticsObserver.LogVerbose(
                    $"LOGI-CODEJOB recovered-cycle-map: pmpName=\"{_pmMissionParameter?.MissionName ?? "null"}\" " +
                    $"cmdName=\"{cmdFromShip.customNameFromPlanMission}\" sc={DescribeSpacecraft(_pmMissionParameter?.SC)} " +
                    $"route={_pmMissionParameter?.Start?.ObjectName ?? "null"}->{_pmMissionParameter?.Target?.ObjectName ?? "null"}");
        }
        if (!isLogi) return;
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(_pmMissionParameter);
        if (string.IsNullOrEmpty(dispatchId))
            dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(cmdFromShip);
        if (!string.IsNullOrEmpty(dispatchId))
        {
            SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, _pmMissionParameter, "SetPMParameterForCodeJobSystemPrefix");
            SolarSdk.CyclicalMissions.RegisterCarrier(dispatchId, _pmMissionParameter.SC as Spacecraft, "SetPMParameterForCodeJobSystemPrefix");
        }
        if (!string.IsNullOrEmpty(dispatchId))
            SolarSdk.CyclicalMissions.MarkCodeJobStarted(dispatchId, "SetPMParameterForCodeJobSystemPrefix");

        _pmMissionParameter.TryFixWrongThrust = true;
        if (cmdFromShip != null)
        {
            cmdFromShip.wasSetPMParameterForCodeJobSystem = true;

            // Stock bug: for MoonCase routes, TryPlanCycleMission hardcodes
            // TransferTypeMoonCase = Optimal and only sets ClickFastestButton
            // in the !MoonCase branch. Override all three flags here
            // unconditionally — MoonCase may not be set yet at prefix time
            // (it's computed later inside GravityEngineCalculate), but
            // TransferTypeMoonCase is read during trajectory selection.
            if (cmdFromShip.TransferType == ETransferType.Fastest
                && !ShouldLetStockHandleMoonCaseAsOptimal(_pmMissionParameter))
            {
                _pmMissionParameter.ClickFastestButton = true;
                _pmMissionParameter.TryFastAsPossible = true;
                _pmMissionParameter.TransferTypeMoonCase = ETransferType.Fastest;
            }
            else if (cmdFromShip.TransferType == ETransferType.Fastest)
            {
                _pmMissionParameter.ClickFastestButton = false;
                _pmMissionParameter.TryFastAsPossible = false;
                _pmMissionParameter.TransferTypeMoonCase = ETransferType.Optimal;
                LogisticsObserver.LogVerbose(
                    $"LOGI-CODEJOB moon-fastest-safe-fallback: route={_pmMissionParameter.Start?.ObjectName ?? "null"}->{_pmMissionParameter.Target?.ObjectName ?? "null"}");
            }
        }
        if (LogisticsObserver.VerboseLoggingEnabled)
            LogisticsObserver.LogVerbose(
                $"LOGI-CODEJOB prefix: sc={DescribeSpacecraft(_pmMissionParameter.SC)} " +
                $"route={_pmMissionParameter.Start?.ObjectName ?? "null"}->{_pmMissionParameter.Target?.ObjectName ?? "null"}");

        LogisticsObserver.ApplyCachedPrecalculateData(_pmMissionParameter);

        var original = result;
        result = () =>
        {
            using (LogisticsObserver.TimeScope($"SetPMParameterForCodeJobSystemCallback {_pmMissionParameter?.Start?.ObjectName ?? "null"}->{_pmMissionParameter?.Target?.ObjectName ?? "null"}"))
            {
            if (!string.IsNullOrEmpty(dispatchId))
                SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, _pmMissionParameter, "SetPMParameterForCodeJobSystemCallback");
            if (!string.IsNullOrEmpty(dispatchId))
                SolarSdk.CyclicalMissions.MarkCodeJobCompleted(dispatchId, "SetPMParameterForCodeJobSystemCallback");
            using (LogisticsObserver.TimeScope("CodeJobCallback.restoreName", 1.0))
                RestoreLogisticsMissionName(_pmMissionParameter, "codejob");
            using (LogisticsObserver.TimeScope("CodeJobCallback.capCargo", 1.0))
                LogisticsObserver.CapLogisticsCargoForPlannerLimits(_pmMissionParameter);
            using (LogisticsObserver.TimeScope("CodeJobCallback.stockOriginal", 1.0))
                original?.Invoke();
            using (LogisticsObserver.TimeScope("CodeJobCallback.cachePrecalculate", 1.0))
                LogisticsObserver.CachePrecalculateData(_pmMissionParameter, "codejob");
            LogTrajectoryDetails(_pmMissionParameter, "CODEJOB-CALLBACK");
            }
        };
        }
    }

    private static bool ShouldLetStockHandleMoonCaseAsOptimal(PMMissionParameter pmp)
    {
        // Moon-case routes use a simple slider interpolation (fastest ↔ optimal)
        // instead of a porkchop plot. Setting ClickFastestButton on a moon case
        // causes ButtonFastestClickButton to run its porkchop grid search on a
        // route that has no valid grid, corrupting the trajectory and producing
        // garbage fuel estimates. Let stock handle all moon cases as Optimal.
        //
        // Stock CheckEarthMoonCase only works with orbit/NBody-resolved positions
        // (e.g. EARTH [ORBIT]), not surface bodies (EARTH). The PMP positions here
        // may or may not be resolved depending on when the prefix fires relative
        // to GravityEngineCalculate, so use our helper that handles surface bodies.
        var start = pmp?.Start;
        var target = pmp?.Target;
        if (start == null || target == null)
            return false;
        return LogisticsObserver.IsMoonCaseRoute(start, target)
            || ObjectInfo.CheckEarthMoonCase(start, target);
    }

    // Migrated to SolarSdk.CyclicalMissions.CheckCycleReplan; retained as a
    // parity reference but no longer registered as a Harmony patch.
    private static bool TryPlanCycleMissionPrefix(SpaceCraftCyclicalMissionController __instance)
    {
        var cmd = __instance.CycleMissionsData;
        if (cmd == null) return true;
        if (cmd.customNameFromPlanMission != null
            && cmd.customNameFromPlanMission.StartsWith("[LOGI")
            && __instance.CycleMissionPlanFlyWas)
        {
            LogisticsObserver.LogVerbose($"SKIP LOGI replanning: {cmd.customNameFromPlanMission}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Stock bug fix: PlanMissionWindow.Update() never calls SetEffectiveDeltaV for
    /// ForCode windows (guarded by <c>!forCode</c>), leaving EffectiveDeltaVOld at 0.
    /// ButtonFastestClickButton uses EffectiveDeltaVOld as a delta-V filter —
    /// with 0 every grid point is rejected and the fastest search silently fails,
    /// falling back to the initial (near-optimal) trajectory.
    ///
    /// Fix: before the fastest search, compute the ship's actual max delta-V
    /// (Tsiolkovsky equation with full fuel tank) and set it on the porkchop,
    /// then push the fuel slider to max so CheckScheduleFly can validate
    /// high-energy trajectories. ButtonFastestClickButton's own fuel-sweep loop
    /// will optimise fuel down to the minimum needed for the chosen trajectory.
    /// </summary>
    // Migrated to SolarSdk.MissionPlanning.BeforeFastestSearch and
    // SolarSdk.MissionPlanning.ApplyCodeFastestDeltaVCorrection.
    private static void ButtonFastestClickButtonPrefix(PMTabSchedule __instance)
    {
        var pmw = __instance.PlanMissionWindow;
        if (pmw == null || !pmw.ForCode)
            return;

        var pmp = pmw.PMMissionParameter;
        if (pmp?.SC == null || pmp.CargoAll?.cargoFuel == null)
            return;

        // Only apply delta-V fix to logistics missions, not vanilla cycles.
        if (!MightBeLogisticsPlan(pmp))
            return;

        var scType = pmp.SC.GetTypeSpaceCraft();
        if (scType == null || scType.NotUsePorkchope || scType.SolarSC)
            return;

        // 1. Compute max effective delta-V from full fuel tank
        float exhaustV = scType.GetExhaustV(pmp.FlyCompany);
        double cargoMass = pmp.CargoAll.CargoCurrent;
        double dryMass = (double)pmp.SC.GetMass() + cargoMass;
        float maxFuelCapacity = scType.GetFuelCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        double wetMass = dryMass + (double)maxFuelCapacity;

        if (wetMass <= dryMass || dryMass <= 0 || exhaustV <= 0)
            return;

        int effectiveDV = (int)((double)exhaustV * Math.Log(wetMass / dryMass, Math.E));
        __instance.porkchop.SetEffectiveDeltaV(effectiveDV);

        LogisticsObserver.LogVerbose(
            $"FASTEST-PREFIX: route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"} " +
            $"sc={DescribeSpacecraft(pmp.SC)} effectiveDV={effectiveDV} exhaustV={exhaustV:0.#} " +
            $"dryMass={dryMass:0.#} cargoMass={cargoMass:0.#} fuelCap={maxFuelCapacity:0.#} " +
            $"wetMass={wetMass:0.#} moonCase={pmp.MoonCase} transferTypeMC={pmp.TransferTypeMoonCase} " +
            $"clickFastest={pmp.ClickFastestButton} tryFast={pmp.TryFastAsPossible}");

        // 2. Push fuel slider to max so high-energy trajectories pass CheckScheduleFly.
        //    ButtonFastestClickButton has its own fuel-sweep loop that will reduce
        //    fuel to the minimum needed for the chosen trajectory.
        var fuelUI = Traverse.Create(__instance).Field("fuelSpaceCraftUI")
            .GetValue<FuelSpaceCraftUI>();
        if (fuelUI != null)
        {
            pmp.CargoAll.cargoFuel.cargoMassPotencjal = (double)maxFuelCapacity;
            fuelUI.SliderSetValue((float)fuelUI.MaxSlider);
        }
    }

    // Migrated to SolarSdk.MissionPlanning.AfterFastestSearch.
    private static void ButtonFastestClickButtonPostfix(PMTabSchedule __instance)
    {
        var pmw = __instance.PlanMissionWindow;
        if (pmw == null || !pmw.ForCode)
            return;
        var pmp = pmw.PMMissionParameter;
        if (pmp == null || !MightBeLogisticsPlan(pmp))
            return;
        LogTrajectoryDetails(pmp, "FASTEST-RESULT");
    }

    // Migrated to SolarSdk.MissionLoadout.CargoCreatedForCycle.
    private static void CreatedCargoToTakeNormalPostfix(CargoAll __result, ECargoStart cargoStart,
        CycleMissionsData cycleMissionsData, ObjectInfo startObject, Spacecraft sc, LaunchVehicle lv,
        bool allResourceOnPlanet, double? loadLimit2, int countSC, bool addSupply, TimeSpan? missionLenght)
    {
        if (!LogisticsObserver.VerboseLoggingEnabled
            || cycleMissionsData?.customNameFromPlanMission == null
            || !cycleMissionsData.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return;
        }

        var company = sc?.GetCompany() ?? cycleMissionsData.Company;
        LogisticsObserver.LogVerbose(
            $"LOGI-CARGO created: name=\"{cycleMissionsData.customNameFromPlanMission}\" " +
            $"start={startObject?.ObjectName ?? "null"} " +
            $"sc={DescribeSpacecraft(sc)} allOnPlanet={allResourceOnPlanet} " +
            $"cargo={DescribeCargo(__result)}");
    }

    // Migrated to SolarSdk.MissionTags.OnClickScheduleButtonForCodePostfix and
    // MissionInfoNameApplied registration.
    private static void OnClickScheduleButtonForCodePostfix(ref MissionInfo __result)
    {
        if (__result == null)
            return;
        if (__result.spacecraftInfo2 is not Spacecraft sc)
            return;

        var cmd = MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
        if (cmd?.customNameFromPlanMission == null
            || !cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return;
        }

        __result.missionName = cmd.customNameFromPlanMission;
        __result.fromCyclicalMission = true;
        SolarSdk.MissionTags.ApplyMissionInfoName(__result, cmd.customNameFromPlanMission, "logistics-schedule-code-postfix");
        LogisticsObserver.LogVerbose($"PLAN mission-name-restored: id={__result.id} name=\"{__result.missionName}\"");
    }

    // Migrated to SolarSdk.MissionTags MissionInfoManager.CreateMissionInfo prefix.
    private static void CreateMissionInfoPrefix(ISpacecraftInfo spacecraftInfo, List<ISpacecraftInfo> listSC,
        MissionInfo.EMissionCreator missionCreator, CargoAll cargoAll, Company company,
        TrajectoryObject trajectoryObject, ref string missionName, ref string __state)
    {
        using (LogisticsObserver.TimeScope($"CreateMissionInfoPrefix {missionCreator}"))
        {
        __state = null;
        if (missionCreator != MissionInfo.EMissionCreator.Cyclical)
            return;

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
    }

    // Migrated to SolarSdk.MissionTags MissionInfoManager.CreateMissionInfo postfix.
    private static void CreateMissionInfoPostfix(MissionInfo __result, string __state)
    {
        if (__result == null)
            return;

        var name = __state;
        if (string.IsNullOrEmpty(name)
            && __result.missionCreator != MissionInfo.EMissionCreator.Cyclical
            && (__result.missionName == null || !__result.missionName.StartsWith("[LOGI", StringComparison.Ordinal)))
        {
            return;
        }

        if (!SolarSdk.MissionTags.ApplyMissionInfoName(__result, name, "logistics-mission-info-postfix"))
        {
            if (__result.missionName != null && __result.missionName.StartsWith("[LOGI", StringComparison.Ordinal))
                __result.fromCyclicalMission = true;
            return;
        }

        LogisticsObserver.RegisterLogisticsMissionInfo(__result);
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(__result);
        if (!string.IsNullOrEmpty(dispatchId))
            SolarSdk.CyclicalMissions.RegisterMissionInfo(dispatchId, __result);
    }

    // Migrated to SolarSdk.MissionPlanning.MissionCompleted.
    private static void MissionInfoCompletePostfix(MissionInfo __instance)
    {
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(__instance);
        if (!string.IsNullOrEmpty(dispatchId))
            SolarSdk.CyclicalMissions.MarkCompleted(dispatchId, "MissionInfo.Complete");
        LogisticsObserver.CleanupLogisticsMissionTrajectory(__instance, "complete");
    }

    // Migrated to SolarSdk.MissionTags.
    private static bool ChangeMissionNamePrefix(PMTabDestination __instance)
    {
        var pmw = Traverse.Create(__instance).Field("planMissionWindow").GetValue<PlanMissionWindow>();
        if (!MightBeLogisticsPlan(pmw?.PMMissionParameter))
            return true;

        return pmw?.PMMissionParameter == null
            || string.IsNullOrEmpty(LogisticsObserver.FindLogisticsCycleName(pmw.PMMissionParameter));
    }

    // Migrated to SolarSdk.MissionTags.
    private static void PMMissionParameterChangeMissionNamePrefix(PMMissionParameter __instance, ref string _missionName)
    {
        if (string.IsNullOrEmpty(_missionName) || !_missionName.StartsWith("Cyclical missions", StringComparison.Ordinal))
            return;

        var name = SolarSdk.MissionTags.ResolveName(__instance);
        if (string.IsNullOrEmpty(name))
            return;

        _missionName = name;
    }

    // Migrated to SolarSdk.MissionPlanning.BeforeCreateFly and SolarSdk.MissionTags.
    private static bool CreateFlyPrefix(PMTabSchedule __instance)
    {
        using (LogisticsObserver.TimeScope("CreateFlyPrefix"))
        {
        var pmw = Traverse.Create(__instance).Field("planMissionWindow").GetValue<PlanMissionWindow>();
        var pmp = pmw?.PMMissionParameter;
        if (!MightBeLogisticsPlan(pmp))
            return true;

        var found = SolarSdk.MissionTags.ResolveName(pmp);
        var missionName = found ?? pmp?.MissionName;
        if (pmp != null
            && !string.IsNullOrEmpty(missionName)
            && missionName.StartsWith("[LOGI]", StringComparison.Ordinal)
            && !HasPositiveNormalResourceCargo(pmp.CargoAll))
        {
            var sc = pmp.SC as Spacecraft;
            var cmd = sc == null ? null : MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
            LogisticsObserver.LogWarning(
                $"PLAN blocked-empty-logi-flight: name=\"{missionName}\" " +
                $"route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"} " +
                $"sc={DescribeSpacecraft(pmp.SC)} cmd={cmd != null} cargo={DescribeCargo(pmp.CargoAll)}");
            if (cmd != null)
                LogisticsObserver.RemoveLogisticsCycle(MonoBehaviourSingleton<CycleMissionManager>.Instance, cmd);
            return false;
        }
        if (LogisticsObserver.VerboseLoggingEnabled && pmp != null && !string.IsNullOrEmpty(found))
        {
            LogisticsObserver.LogVerbose(
                $"NAMING TRACE createfly-prefix: pmpName=\"{pmp.MissionName}\" found=\"{found ?? "null"}\" " +
                $"sc={DescribeSpacecraft(pmp.SC)} route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"}");
        }
        if (LogisticsObserver.VerboseLoggingEnabled && pmp != null && !string.IsNullOrEmpty(missionName) && missionName.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            LogisticsObserver.LogVerbose($"LOGI-LAUNCH createfly-prefix: name=\"{missionName}\" route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"} {DescribePayload(pmp)} sc={DescribeSpacecraft(pmp.SC)}");
        }
        RestoreLogisticsMissionName(pmw?.PMMissionParameter, "createfly");
        return true;
        }
    }

    // Migrated to SolarSdk.MissionPlanning.AfterCreateFly.
    private static void CreateFlyPostfix(PMTabSchedule __instance)
    {
        var pmw = Traverse.Create(__instance).Field("planMissionWindow").GetValue<PlanMissionWindow>();
        var pmp = pmw?.PMMissionParameter;
        if (pmp == null) return;
        if (!MightBeLogisticsPlan(pmp)
            && (pmp.MissionName == null || !pmp.MissionName.StartsWith("[LOGI", StringComparison.Ordinal)))
        {
            return;
        }

        var found = SolarSdk.MissionTags.ResolveName(pmp);
        if (string.IsNullOrEmpty(found) && (pmp.MissionName == null || !pmp.MissionName.StartsWith("[LOGI", StringComparison.Ordinal)))
            return;
        if (LogisticsObserver.VerboseLoggingEnabled)
        {
            LogisticsObserver.LogVerbose(
                $"NAMING TRACE createfly-postfix: pmpName=\"{pmp.MissionName}\" found=\"{found ?? "null"}\" " +
                $"sc={DescribeSpacecraft(pmp.SC)} route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"}");
            LogisticsObserver.LogVerbose($"LOGI-LAUNCH createfly-postfix: pmpName=\"{pmp.MissionName}\" route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"} {DescribePayload(pmp)} sc={DescribeSpacecraft(pmp.SC)}");
        }
    }

    // Migrated to SolarSdk.MissionPlanning.SuppressPreviewTrajectory.
    private static bool CreatedTrajectoryPrefix(PMTabSchedule __instance)
    {
        var pmw = Traverse.Create(__instance).Field("planMissionWindow").GetValue<PlanMissionWindow>();
        var pmp = pmw?.PMMissionParameter;
        if (pmw?.ForCode == true && MightBeLogisticsPlan(pmp) && LogisticsObserver.IsLogisticsPlan(pmp))
        {
            LogisticsObserver.LogVerbose($"PLAN suppress-preview-trajectory: {pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"}");
            return false;
        }

        return true;
    }

    private static void RestoreLogisticsMissionName(PMMissionParameter pmp, string context)
    {
        if (!MightBeLogisticsPlan(pmp))
            return;

        if (SolarSdk.MissionTags.ApplyMissionParameterName(pmp, context))
            LogisticsObserver.LogVerbose($"PLAN mission-name-prep: context={context} name=\"{pmp.MissionName}\"");
    }

    // Migrated to SolarSdk.MissionTags mission label naming.
    private static void MissionsLabelsSetDataPostfix(MissionsLabelsMainUI __instance, MissionInfo mi)
    {
        if (mi == null) return;
        // Only process our own logistics missions, not vanilla cyclical missions.
        if (!SolarSdk.MissionTags.IsTaggedMission(mi)) return;

        var sc = mi.spacecraftInfo2 as Spacecraft;
        var name = SolarSdk.MissionTags.ResolveName(mi);
        if (!string.IsNullOrEmpty(name))
        {
            var txtMissionName = Traverse.Create(__instance).Field("txtMissionName").GetValue<TextMeshProUGUI>();
            if (txtMissionName != null)
                txtMissionName.text = name;
            mi.fromCyclicalMission = true;
        }

        if (LogisticsObserver.VerboseLoggingEnabled)
        {
            var cmd = sc == null ? null : MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
            LogisticsObserver.LogVerbose(
                $"NAMING TRACE flight-label-setdata: id={mi.id} miName=\"{mi.missionName}\" " +
                $"fromCycle={mi.fromCyclicalMission} sc={DescribeSpacecraft(sc)} " +
                $"cmd={cmd != null} cmdName=\"{cmd?.customNameFromPlanMission ?? "null"}\" " +
                $"route={mi.start?.ObjectName ?? "null"}->{mi.target?.ObjectName ?? "null"}");
        }
    }

    // Migrated to SolarSdk.MissionTags safe tagged mission label cleanup.
    private static bool RemoveMissionsSafePrefix(MissionsLabelsMainUIManager __instance, MissionsLabelsMainUI missionsLabelsMainUI)
    {
        if (missionsLabelsMainUI == null)
            return false;

        // Only intercept logistics missions — let vanilla missions use stock cleanup.
        var mi = missionsLabelsMainUI.MissionInfo;
        if (!LogisticsObserver.IsLogisticsMissionInfo(mi))
            return true;

        try
        {
            var labels = Traverse.Create(__instance)
                .Field("listMissionsLabelsMainUI")
                .GetValue<List<MissionsLabelsMainUI>>();
            labels?.Remove(missionsLabelsMainUI);

            var trajectory = mi?.trajectoryObject;
            if (trajectory != null)
                UnityEngine.Object.Destroy(trajectory.gameObject);

            if (missionsLabelsMainUI.gameObject != null)
                UnityEngine.Object.Destroy(missionsLabelsMainUI.gameObject);

            mi?.Complete();
        }
        catch (Exception ex)
        {
            LogisticsObserver.LogWarning($"Safe RemoveMissions suppressed stock label cleanup error: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    // Migrated to SolarSdk.MissionTags mission row naming; retained for reference logging.
    private static void MissionRowSetMissionInfoPostfix(MissionInfo _missionInfo)
    {
        if (!LogisticsObserver.VerboseLoggingEnabled) return;
        if (!LogisticsObserver.IsLogisticsMissionInfo(_missionInfo)) return;

        LogisticsObserver.LogVerbose(
            $"NAMING TRACE mission-row-set: id={_missionInfo.id} name=\"{_missionInfo.missionName}\" " +
            $"fromCycle={_missionInfo.fromCyclicalMission} sc={DescribeSpacecraft(_missionInfo.spacecraftInfo2)} " +
            $"route={_missionInfo.start?.ObjectName ?? "null"}->{_missionInfo.target?.ObjectName ?? "null"}");
    }

    // Migrated to SolarSdk.MissionTags mission row naming; retained for reference logging.
    private static void MissionRowNewSetMissionInfoPostfix(MissionInfo _missionInfo, string stringActionText, MissionListByType.EMissionType _missionType)
    {
        if (!LogisticsObserver.VerboseLoggingEnabled) return;
        if (!LogisticsObserver.IsLogisticsMissionInfo(_missionInfo)) return;

        LogisticsObserver.LogVerbose(
            $"NAMING TRACE mission-row-new-set: id={_missionInfo.id} name=\"{_missionInfo.missionName}\" " +
            $"type={_missionType} action=\"{stringActionText}\" fromCycle={_missionInfo.fromCyclicalMission} " +
            $"sc={DescribeSpacecraft(_missionInfo.spacecraftInfo2)} route={_missionInfo.start?.ObjectName ?? "null"}->{_missionInfo.target?.ObjectName ?? "null"}");
    }

    private static string FindLogisticsCycleNameFor(ISpacecraftInfo spacecraftInfo, List<ISpacecraftInfo> listSC)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null) return null;

        if (spacecraftInfo is Spacecraft sc)
        {
            var cmd = cm.GetCycleMission(sc);
            if (cmd?.customNameFromPlanMission != null
                && cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
                return cmd.customNameFromPlanMission;
        }

        if (listSC == null) return null;
        foreach (var sci in listSC)
        {
            if (sci is not Spacecraft listShip) continue;
            var cmd = cm.GetCycleMission(listShip);
            if (cmd?.customNameFromPlanMission != null
                && cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
                return cmd.customNameFromPlanMission;
        }

        return null;
    }

    private static bool MightBeLogisticsPlan(PMMissionParameter pmp)
    {
        if (pmp == null)
            return false;
        if (pmp.MissionName != null && pmp.MissionName.StartsWith("[LOGI", StringComparison.Ordinal))
            return true;
        if (pmp.SC is Spacecraft sc)
        {
            var cmd = MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
            if (cmd?.customNameFromPlanMission != null
                && cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
            {
                return true;
            }
        }
        // Only match missions we tagged — never match vanilla cyclical missions.
        return false;
    }

    private static void LogTrajectoryDetails(PMMissionParameter pmp, string context)
    {
        if (pmp == null) return;
        try
        {
            var departure = pmp.DepartureTimeDate;
            var arrival = pmp.Arrival;
            var travelDays = (arrival - departure).TotalDays;
            var result = pmp.CheckCanPlanMission().planMissionResult;
            LogisticsObserver.LogVerbose(
                $"TRAJECTORY {context}: route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"} " +
                $"sc={DescribeSpacecraft(pmp.SC)} departure={departure:yyyy-MM-dd} arrival={arrival:yyyy-MM-dd} " +
                $"travelDays={travelDays:0.#} allFuelNeed={pmp.AllFuelNeed:0.#} minFuelCost={pmp.MINFuelCost:0.#} " +
                $"moonCase={pmp.MoonCase} clickFastest={pmp.ClickFastestButton} " +
                $"transferTypeMC={pmp.TransferTypeMoonCase} planResult={result}");
            if (travelDays > 800)
                LogisticsObserver.LogWarning(
                    $"TRAJECTORY-SUSPECT {context}: route={pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"} " +
                    $"travelDays={travelDays:0.#} — abnormally long trajectory detected");
        }
        catch (Exception ex)
        {
            LogisticsObserver.LogWarning($"TRAJECTORY {context}: failed to log details: {ex.Message}");
        }
    }

    private static string DescribeSpacecraft(ISpacecraftInfo spacecraftInfo)
    {
        if (spacecraftInfo is not Spacecraft sc)
            return spacecraftInfo?.GetTypeSpaceCraft()?.NameRocketType ?? "null";

        return $"{sc.GetSpacecraftName() ?? sc.spacecraftName ?? sc.spacecraftType?.NameRocketType ?? "SC"}#{sc.ID}/phase={sc.CurrentPhase}";
    }

    private static string DescribeCargo(CargoAll cargoAll)
    {
        if (cargoAll == null)
            return "null";

        var parts = new List<string>();
        if (cargoAll.listCargo != null)
        {
            foreach (var cargo in cargoAll.listCargo)
            {
                if (cargo?.resourceType == null) continue;
                parts.Add($"{cargo.resourceType.ID}:{cargo.cargoMass:0.#}");
            }
        }

        if (cargoAll.cargoFuel?.resourceType != null && cargoAll.cargoFuel.cargoMass > 0)
            parts.Add($"fuel/{cargoAll.cargoFuel.resourceType.ID}:{cargoAll.cargoFuel.cargoMass:0.#}");

        return parts.Count == 0 ? "empty" : string.Join(",", parts);
    }

    private static string DescribePayload(PMMissionParameter pmp)
    {
        if (pmp == null)
            return "payload=null";

        var scType = pmp.SC?.GetTypeSpaceCraft();
        var capacity = (scType?.GetCargoCapacity(pmp.FlyCompany) ?? 0) * Math.Max(1, pmp.SCCount);
        var cargo = pmp.CargoAll?.CargoCurrent ?? 0;
        var propellantTarget = pmp.CargoAll?.cargoFuel?.cargoMassPotencjal ?? 0;
        var propellantActual = pmp.CargoAll?.cargoFuel?.cargoMass ?? 0;
        return $"payload={cargo:0.#}/{capacity:0.#} propellantTarget={propellantTarget:0.#} propellantActual={propellantActual:0.#} cargo={DescribeCargo(pmp.CargoAll)}";
    }

    private static string DescribeCycleCargoTabs(CycleMissionsData cmd)
    {
        if (cmd == null)
            return "tabs=null";

        return $"tabsA={DescribeTab(cmd.cargoAllStart)} tabsB={DescribeTab(cmd.cargoAllEnd)}";
    }

    private static string DescribeTab(InfoCargoCyclicalMission info)
    {
        if (info?.Tab == null || info.Tab.Length == 0)
            return "empty";

        var parts = new List<string>();
        foreach (var rd in info.Tab)
        {
            if (rd == null) continue;
            parts.Add(rd.ID);
        }

        return parts.Count == 0 ? "empty" : string.Join(",", parts);
    }

    private static string DescribeCycleEnds(CycleMissionsData cmd)
    {
        if (cmd == null)
            return "endsData=null";

        return $"endsMaxA={DescribeEndsData(cmd.EndsResourceCountMaxA)} " +
            $"endsDoneA={DescribeEndsData(cmd.EndsResourceCountDataA)} " +
            $"endsMaxB={DescribeEndsData(cmd.EndsResourceCountMaxB)} " +
            $"endsDoneB={DescribeEndsData(cmd.EndsResourceCountDataB)}";
    }

    private static string DescribeEndsData(EndsResourceCountData data)
    {
        if (data?.listData == null || data.listData.Count == 0)
            return "empty";

        var parts = new List<string>();
        foreach (var part in data.listData)
        {
            if (part?.rd == null) continue;
            parts.Add($"{part.rd.ID}:{part.count:0.#}");
        }

        return parts.Count == 0 ? "empty" : string.Join(",", parts);
    }

    private static string DescribeStock(ObjectInfo objectInfo, Company company, CycleMissionsData cmd, bool startIsA)
    {
        if (objectInfo == null || company == null || cmd == null)
            return "null";

        var data = startIsA ? cmd.EndsResourceCountMaxA : cmd.EndsResourceCountMaxB;
        if (data?.listData == null || data.listData.Count == 0)
            data = startIsA ? cmd.EndsResourceCountMaxB : cmd.EndsResourceCountMaxA;

        if (data?.listData == null || data.listData.Count == 0)
            return "no-ends-resources";

        var oid = objectInfo.GetObjectInfoData(company);
        if (oid == null)
            return "no-object-data";

        var parts = new List<string>();
        foreach (var part in data.listData)
        {
            if (part?.rd == null) continue;
            parts.Add($"{part.rd.ID}:{oid.CheckResources(part.rd):0.#}");
        }

        return parts.Count == 0 ? "empty" : string.Join(",", parts);
    }

    private static bool HasPositiveNormalResourceCargo(CargoAll cargoAll)
    {
        if (cargoAll?.listCargo == null)
            return false;

        foreach (var cargo in cargoAll.listCargo)
        {
            if (cargo == null) continue;
            if (cargo.resourceTypeType == EResourceTypeType.resorces
                && cargo.resourceType != null
                && cargo.cargoMass > 0)
                return true;
        }

        return false;
    }

    // ── Market offer notification suppression ──────────────────────────
    // Suppress "new offer" notifications for any resource where we have
    // an active auto-sell provider (for incoming buy offers) or an active
    // auto-buy request (for incoming sell offers) on the same body.

    private static readonly HashSet<string> _notifiedCycleRoutes = new HashSet<string>();

    public static void ResetNotifiedCycleRoutes() => _notifiedCycleRoutes.Clear();

    // Migrated to SolarSdk.CyclicalMissions.CyclePlanNotification; retained as a
    // parity reference but no longer registered as a Harmony patch.
    private static bool ShowNotificationPrefix(SpaceCraftCyclicalMissionController __instance, ObjectInfo start, PMMissionParameter PMMissionParameter)
    {
        var cmd = __instance.CycleMissionsData;
        if (cmd?.customNameFromPlanMission == null
            || !cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var checkResult = PMMissionParameter?.CheckCanPlanMission().planMissionResult
                ?? PMMissionParameter.EPlanMissionResult.AllOk;
            var translated = LogisticsObserver.TranslatePlanMissionResult(PMMissionParameter, checkResult);

            string tooltip = null;
            if (PMMissionParameter != null && !PMMissionParameter.CheckScheduleFly())
                tooltip = PMTabSchedule.GetTextToltip(PMMissionParameter);

            // Prefer our translated result; fall back to stock tooltip
            var note = translated ?? tooltip;

            if (!string.IsNullOrEmpty(note) && cmd.B != null)
                LogisticsObserver.SetCyclePlanFailureNote(cmd.B, cmd.cargoAllStart, note);

            // Store failure reason per-ship for dropdown status display
            if (!string.IsNullOrEmpty(note) && cmd.ListSC != null)
                LogisticsObserver.SetShipBlockedReason(cmd.ListSC, note);

            var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(cmd);
            if (!string.IsNullOrEmpty(note) && !string.IsNullOrEmpty(dispatchId))
                SolarSdk.CyclicalMissions.MarkCodeJobFailed(dispatchId, note, "cycle-notification");

            var routeKey = $"{cmd.A?.id ?? -1}->{cmd.B?.id ?? -1}";
            if (_notifiedCycleRoutes.Add(routeKey))
            {
                LogisticsObserver.LogWarning(
                    $"CYCLE notification-first: route={cmd.A?.ObjectName ?? "null"}->{cmd.B?.ObjectName ?? "null"} " +
                    $"name={cmd.customNameFromPlanMission} result={checkResult} translated=\"{translated ?? "none"}\" tooltip=\"{tooltip ?? "none"}\"");
                return true;
            }

            LogisticsObserver.LogVerbose(
                $"CYCLE notification-suppressed: route={cmd.A?.ObjectName ?? "null"}->{cmd.B?.ObjectName ?? "null"} " +
                $"name={cmd.customNameFromPlanMission} result={checkResult} translated=\"{translated ?? "none"}\" tooltip=\"{tooltip ?? "none"}\"");
        }
        catch (Exception ex)
        {
            LogisticsObserver.LogError($"ShowNotificationPrefix error: {ex}");
        }

        return false;
    }

    // Migrated to SolarSdk.Market.ShouldSuppressOfferNotification.
    private static void AddOfferSuppressNotificationPrefix(Offer offer, ref bool suppresssNotification)
    {
        if (suppresssNotification)
            return;
        if (offer == null)
            return;

        try
        {
            var oi = offer.WhereOffer;
            var rd = offer.Rd;
            if (oi == null || rd == null)
                return;

            var data = LogisticsNetwork.Get(oi);
            if (data == null)
                return;

            if (offer.BuySell)
            {
                // Buy offer from AI — suppress if we have an auto-sell provider for this resource here.
                if (data.providers.Any(p => p.isActive && p.autoSell && p.ResourceDefinition == rd))
                {
                    suppresssNotification = true;
                    if (LogisticsObserver.VerboseLoggingEnabled)
                        LogisticsObserver.Log($"MARKET suppress new-buy-offer notification: body={oi.ObjectName} rd={rd.ID} (auto-sell active)");
                }
            }
            else
            {
                // Sell offer from AI — suppress if we have an auto-buy request for this resource here.
                if (data.requests.Any(r => r.autoBuy && r.ResourceDefinition == rd))
                {
                    suppresssNotification = true;
                    if (LogisticsObserver.VerboseLoggingEnabled)
                        LogisticsObserver.Log($"MARKET suppress new-sell-offer notification: body={oi.ObjectName} rd={rd.ID} (auto-buy active)");
                }
            }
        }
        catch (Exception ex)
        {
            LogisticsObserver.LogError($"AddOfferSuppressNotificationPrefix error: {ex}");
        }
    }
}
