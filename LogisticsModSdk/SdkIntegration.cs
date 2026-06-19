using System;
using System.Collections.Generic;
using System.Linq;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.UI.Windows.Elements.PlanMissionElements.PMScheduleElements;
using Game.UI.Windows.Windows;
using LogisticsModSdk.Data;
using LogisticsModSdk.Logic;
using Manager;
using SolarExpanseSdk.Core;
using SolarExpanseSdk.Services;

namespace LogisticsModSdk;

internal static class SdkIntegration
{
    private static bool _registered;
    private static readonly HashSet<string> _notifiedCycleRoutes = new HashSet<string>();

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        SolarSdk.ObjectInfoUi.RegisterWindowComponent<UI.LogisticsUI>();
        SolarSdk.ObjectInfoUi.RegisterRocketRowDecorator(BuildLogisticsReservationMarker);
        SolarSdk.Events.SaveLoading += ResetLoadState;
        SolarSdk.Events.BeforeSave += saveName => Data.LogisticsPersistence.Save(saveName);
        SolarSdk.Events.SaveLoaded += OnSaveLoaded;
        SolarSdk.Events.DayTick += Logic.LogisticsObserver.OnDayChange;
        SolarSdk.Events.PostLoadFirstTick += () => Logic.LogisticsObserver.OnDayChange(0);
        SolarSdk.Events.ObjectInfoChanged += OnObjectInfoChanged;
        SolarSdk.Events.ObjectInfoRebuild += OnObjectInfoRebuild;
        SolarSdk.Diagnostics.RegisterSnapshotProvider("logistics", Logic.LogisticsObserver.BuildSdkDebugSnapshot);
        SolarSdk.MissionTags.RegisterMissionPrefix("[LOGI");
        SolarSdk.MissionTags.RegisterNameResolver("logisticsmodsdk", ResolveLogisticsMissionName);
        SolarSdk.MissionTags.MissionInfoNameApplied += OnSdkMissionInfoNameApplied;
        SolarSdk.MissionPlanning.SuppressArrivalNotification += ShouldSuppressArrivalNotification;
        SolarSdk.MissionPlanning.CheckSelfLaunchOverride += CheckSelfLaunchOverride;
        SolarSdk.MissionPlanning.BeforeCodeJobPlan += OnBeforeCodeJobPlan;
        SolarSdk.MissionPlanning.BeforeCodeJobCallback += OnBeforeCodeJobCallback;
        SolarSdk.MissionPlanning.AfterCodeJobCallback += OnAfterCodeJobCallback;
        SolarSdk.MissionPlanning.BeforeFastestSearch += OnBeforeFastestSearch;
        SolarSdk.MissionPlanning.AfterFastestSearch += OnAfterFastestSearch;
        SolarSdk.MissionPlanning.BeforeCreateFly += OnBeforeCreateFly;
        SolarSdk.MissionPlanning.AfterCreateFly += OnAfterCreateFly;
        SolarSdk.MissionPlanning.SuppressPreviewTrajectory += ShouldSuppressPreviewTrajectory;
        SolarSdk.MissionPlanning.MissionCompleted += OnMissionCompleted;
        SolarSdk.CyclicalMissions.CheckCycleReplan += ShouldSuppressCycleReplan;
        SolarSdk.CyclicalMissions.CyclePlanNotification += OnCyclePlanNotification;
        SolarSdk.MissionLoadout.CargoCreatedForCycle += OnCycleCargoCreated;
        SolarSdk.Market.ShouldSuppressOfferNotification += ShouldSuppressMarketOfferNotification;
    }

    private static string ResolveLogisticsMissionName(SdkMissionNameContext context)
    {
        if (context == null)
            return null;

        if (!string.IsNullOrEmpty(context.ExistingName)
            && context.ExistingName.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return context.ExistingName;
        }

        if (context.MissionParameter != null)
            return Logic.LogisticsObserver.FindLogisticsCycleName(context.MissionParameter);

        if (context.MissionInfo != null)
        {
            if (!string.IsNullOrEmpty(context.MissionInfo.missionName)
                && context.MissionInfo.missionName.StartsWith("[LOGI", StringComparison.Ordinal))
            {
                return context.MissionInfo.missionName;
            }

            return Logic.LogisticsObserver.FindLogisticsCycleName(
                context.MissionInfo.start,
                context.MissionInfo.target,
                context.MissionInfo.company,
                context.MissionInfo.ListSpacecraftInfo2,
                context.MissionInfo.cargoAll);
        }

        var byShip = Logic.LogisticsObserver.FindLogisticsCycleName(context.SpacecraftInfo, context.SpacecraftInfos);
        if (!string.IsNullOrEmpty(byShip))
            return byShip;

        return Logic.LogisticsObserver.FindLogisticsCycleName(
            context.Start ?? context.TrajectoryObject?.StartObjectInfo,
            context.Target ?? context.TrajectoryObject?.EndObjectInfo,
            context.Company,
            context.SpacecraftInfos,
            context.CargoAll);
    }

    private static void OnSdkMissionInfoNameApplied(MissionInfo missionInfo, string name)
    {
        if (name != null && name.StartsWith("[LOGI", StringComparison.Ordinal))
            Logic.LogisticsObserver.RegisterLogisticsMissionInfo(missionInfo);
    }

    private static bool? ShouldSuppressArrivalNotification(Spacecraft sc, string context)
    {
        var mi = sc?.GetMissionInfo();
        if (mi == null || !Logic.LogisticsObserver.IsLogisticsMissionInfo(mi))
            return null;

        Logic.LogisticsObserver.LogVerbose(
            $"NOTIFY suppress-cyclical-arrival: context={context} mission={mi.id} name=\"{mi.missionName}\" " +
            $"fromCycle={mi.fromCyclicalMission} sc={DescribeSpacecraft(sc)} route={mi.start?.ObjectName ?? "null"}->{mi.target?.ObjectName ?? "null"}");
        return true;
    }

    private static bool? CheckSelfLaunchOverride(PMMissionParameter parameter)
    {
        if (!Logic.LogisticsObserver.TryOverrideLogisticsSelfLaunchCheck(parameter, out var requiresFullLaunchVehicleList))
            return null;

        return requiresFullLaunchVehicleList;
    }

    private static void OnBeforeCodeJobPlan(PMMissionParameter parameter, MissionPlanContext context)
    {
        using (Logic.LogisticsObserver.TimeScope($"Sdk.BeforeCodeJobPlan {parameter?.Start?.ObjectName ?? "null"}->{parameter?.Target?.ObjectName ?? "null"}"))
        {
            var cmdFromShip = GetCycleFromParameter(parameter);
            var isLogi = IsLogisticsCodeJob(parameter, cmdFromShip);
            if (!isLogi)
                return;

            var dispatchId = ResolveDispatchId(parameter, cmdFromShip);
            if (!string.IsNullOrEmpty(dispatchId))
            {
                context.DispatchId = dispatchId;
                SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, "logistics-before-codejob");
                SolarSdk.CyclicalMissions.RegisterCarrier(dispatchId, parameter?.SC as Spacecraft, "logistics-before-codejob");
                SolarSdk.CyclicalMissions.MarkCodeJobStarted(dispatchId, "logistics-before-codejob");
            }

            if (parameter != null)
                parameter.TryFixWrongThrust = true;

            if (cmdFromShip != null && parameter != null)
            {
                cmdFromShip.wasSetPMParameterForCodeJobSystem = true;
                ApplyLogisticsTransferFlags(parameter, cmdFromShip);
            }

            Logic.LogisticsObserver.ApplyCachedPrecalculateData(parameter);

            if (Logic.LogisticsObserver.VerboseLoggingEnabled && parameter != null)
            {
                Logic.LogisticsObserver.LogVerbose(
                    $"LOGI-CODEJOB sdk-before: dispatchId={dispatchId ?? "none"} sc={DescribeSpacecraft(parameter.SC)} " +
                    $"route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}");
            }
        }
    }

    private static void OnBeforeCodeJobCallback(PMMissionParameter parameter, MissionPlanContext context)
    {
        using (Logic.LogisticsObserver.TimeScope($"Sdk.BeforeCodeJobCallback {parameter?.Start?.ObjectName ?? "null"}->{parameter?.Target?.ObjectName ?? "null"}"))
        {
            if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter))
                return;

            var dispatchId = context?.DispatchId ?? ResolveDispatchId(parameter, GetCycleFromParameter(parameter));
            if (!string.IsNullOrEmpty(dispatchId))
                SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, "logistics-codejob-callback-before");

            RestoreLogisticsMissionName(parameter, "codejob");
            Logic.LogisticsObserver.CapLogisticsCargoForPlannerLimits(parameter);
        }
    }

    private static void OnAfterCodeJobCallback(PMMissionParameter parameter, MissionPlanContext context)
    {
        using (Logic.LogisticsObserver.TimeScope($"Sdk.AfterCodeJobCallback {parameter?.Start?.ObjectName ?? "null"}->{parameter?.Target?.ObjectName ?? "null"}"))
        {
            if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter))
                return;

            var dispatchId = context?.DispatchId ?? ResolveDispatchId(parameter, GetCycleFromParameter(parameter));
            if (!string.IsNullOrEmpty(dispatchId))
            {
                SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, "logistics-codejob-callback-after");
                SolarSdk.CyclicalMissions.MarkCodeJobCompleted(dispatchId, "logistics-codejob-callback-after");
            }

            Logic.LogisticsObserver.CachePrecalculateData(parameter, "codejob");
            LogTrajectoryDetails(parameter, "CODEJOB-CALLBACK");
        }
    }

    private static void OnBeforeFastestSearch(PMTabSchedule schedule, PMMissionParameter parameter, MissionPlanContext context)
    {
        if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter))
            return;

        var protectedReserve = Logic.LogisticsObserver.GetProtectedReturnFuelReserve(parameter);
        if (protectedReserve > 0.0)
            Logic.LogisticsObserver.ApplyProtectedReturnFuelReserve(parameter, SdkReservePropellantMode.Fastest);

        if (SolarSdk.MissionPlanning.ApplyCodeFastestDeltaVCorrection(schedule, "logistics-fastest", protectedReserve))
        {
            Logic.LogisticsObserver.LogVerbose(
                $"FASTEST-PREFIX: dispatchId={context?.DispatchId ?? "none"} route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} " +
                $"protectedReserve={protectedReserve:0.#} " +
                $"sc={DescribeSpacecraft(parameter.SC)} moonCase={parameter.MoonCase} transferTypeMC={parameter.TransferTypeMoonCase} " +
                $"clickFastest={parameter.ClickFastestButton} tryFast={parameter.TryFastAsPossible}");
        }
    }

    private static void OnAfterFastestSearch(PMMissionParameter parameter, MissionPlanContext context)
    {
        if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter))
            return;

        LogTrajectoryDetails(parameter, "FASTEST-RESULT");
    }

    private static bool? OnBeforeCreateFly(SdkCreateFlyContext context)
    {
        var parameter = context?.Parameter;
        if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter))
            return null;

        var found = SolarSdk.MissionTags.ResolveName(parameter);
        var missionName = found ?? parameter?.MissionName;
        if (!Logic.LogisticsObserver.VerifyProtectedReturnFuelReserve(parameter, out var reserveFailure))
        {
            var sc = parameter.SC as Spacecraft;
            var cmd = sc == null ? null : MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
            Logic.LogisticsObserver.LogWarning(
                $"PLAN blocked-return-fuel-reserve: reason=\"{reserveFailure}\" " +
                $"route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} " +
                $"sc={DescribeSpacecraft(parameter.SC)} cargo={SolarSdk.MissionLoadout.FormatCargo(parameter.CargoAll)}");
            if (cmd != null)
                Logic.LogisticsObserver.RemoveLogisticsCycle(MonoBehaviourSingleton<CycleMissionManager>.Instance, cmd);
            context.SuppressReason = "return-fuel-reserve-not-preserved";
            return true;
        }

        if (parameter != null
            && !string.IsNullOrEmpty(missionName)
            && missionName.StartsWith("[LOGI]", StringComparison.Ordinal)
            && !HasPositiveNormalResourceCargo(parameter.CargoAll))
        {
            var sc = parameter.SC as Spacecraft;
            var cmd = sc == null ? null : MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
            Logic.LogisticsObserver.LogWarning(
                $"PLAN blocked-empty-logi-flight: name=\"{missionName}\" " +
                $"route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} " +
                $"sc={DescribeSpacecraft(parameter.SC)} cmd={cmd != null} cargo={SolarSdk.MissionLoadout.FormatCargo(parameter.CargoAll)}");
            if (cmd != null)
                Logic.LogisticsObserver.RemoveLogisticsCycle(MonoBehaviourSingleton<CycleMissionManager>.Instance, cmd);
            context.SuppressReason = "empty-logistics-cargo";
            return true;
        }

        if (parameter != null && !string.IsNullOrEmpty(found))
        {
            Logic.LogisticsObserver.LogVerbose(
                $"NAMING TRACE createfly-prefix: pmpName=\"{parameter.MissionName}\" found=\"{found ?? "null"}\" " +
                $"sc={DescribeSpacecraft(parameter.SC)} route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}");
        }

        if (parameter != null && !string.IsNullOrEmpty(missionName) && missionName.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            var protectedReserve = Logic.LogisticsObserver.GetProtectedReturnFuelReserve(parameter);
            if (protectedReserve > 0.0)
            {
                Logic.LogisticsObserver.LogVerbose(
                    $"RETURNFUEL launch-check: name=\"{missionName}\" route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} " +
                    $"reserve={protectedReserve:0.#} leftOver={parameter.LeftOverFuel:0.#} loadedPotential={parameter.CargoAll?.cargoFuel?.cargoMassPotencjal ?? 0:0.#} " +
                    $"allFuel={parameter.AllFuelNeed:0.#} minFuel={parameter.MINFuelCost:0.#} fuelNeed={parameter.FuelNeed:0.#} " +
                    $"cargo={SolarSdk.MissionLoadout.FormatCargo(parameter.CargoAll)} sc={DescribeSpacecraft(parameter.SC)}");
            }

            Logic.LogisticsObserver.LogVerbose(
                $"LOGI-LAUNCH createfly-prefix: name=\"{missionName}\" route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} payload={parameter.CargoAll?.CargoCurrent ?? 0:0.#} cargo={SolarSdk.MissionLoadout.FormatCargo(parameter.CargoAll)} sc={DescribeSpacecraft(parameter.SC)}");
        }

        if (SolarSdk.MissionTags.ApplyMissionParameterName(parameter, "createfly"))
            Logic.LogisticsObserver.LogVerbose($"PLAN mission-name-prep: context=createfly name=\"{parameter.MissionName}\"");
        return null;
    }

    private static void OnAfterCreateFly(SdkCreateFlyContext context)
    {
        var parameter = context?.Parameter;
        if (parameter == null)
            return;
        if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter)
            && (parameter.MissionName == null || !parameter.MissionName.StartsWith("[LOGI", StringComparison.Ordinal)))
        {
            return;
        }

        var found = SolarSdk.MissionTags.ResolveName(parameter);
        if (string.IsNullOrEmpty(found) && (parameter.MissionName == null || !parameter.MissionName.StartsWith("[LOGI", StringComparison.Ordinal)))
            return;

        Logic.LogisticsObserver.LogVerbose(
            $"NAMING TRACE createfly-postfix: pmpName=\"{parameter.MissionName}\" found=\"{found ?? "null"}\" " +
            $"sc={DescribeSpacecraft(parameter.SC)} route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}");
        Logic.LogisticsObserver.LogVerbose(
            $"LOGI-LAUNCH createfly-postfix: pmpName=\"{parameter.MissionName}\" route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} payload={parameter.CargoAll?.CargoCurrent ?? 0:0.#} cargo={SolarSdk.MissionLoadout.FormatCargo(parameter.CargoAll)} sc={DescribeSpacecraft(parameter.SC)}");
        Logic.LogisticsObserver.ClearProtectedReturnFuelReserve(parameter);
    }

    private static bool? ShouldSuppressPreviewTrajectory(PMTabSchedule schedule, PMMissionParameter parameter, MissionPlanContext context)
    {
        if (schedule?.PlanMissionWindow?.ForCode == true
            && Logic.LogisticsObserver.IsLogisticsPlan(parameter))
        {
            Logic.LogisticsObserver.LogVerbose($"PLAN suppress-preview-trajectory: {parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}");
            return true;
        }

        return null;
    }

    private static void OnMissionCompleted(MissionInfo missionInfo)
    {
        if (missionInfo != null && Logic.LogisticsObserver.IsLogisticsMissionInfo(missionInfo))
            Logic.LogisticsObserver.CleanupLogisticsMissionTrajectory(missionInfo, "complete");
    }

    private static void OnCycleCargoCreated(SdkCycleCargoCreatedContext context)
    {
        var cycleName = context?.Cycle?.customNameFromPlanMission;
        if (string.IsNullOrEmpty(cycleName)
            || !cycleName.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return;
        }

        Logic.LogisticsObserver.LogVerbose(
            $"LOGI-CARGO created: name=\"{cycleName}\" " +
            $"start={context.StartObject?.ObjectName ?? "null"} " +
            $"sc={DescribeSpacecraft(context.Spacecraft)} allOnPlanet={context.AllResourceOnPlanet} " +
            $"cargo={SolarSdk.MissionLoadout.FormatCargo(context.Cargo)}");
    }

    private static bool? ShouldSuppressMarketOfferNotification(SdkMarketOfferContext context)
    {
        var offer = context?.Offer;
        if (offer == null)
            return null;

        var oi = offer.WhereOffer;
        var rd = offer.Rd;
        if (oi == null || rd == null)
            return null;

        var data = LogisticsNetwork.Get(oi);
        if (data == null)
            return null;

        if (offer.BuySell)
        {
            if (data.providers.Any(p => p.isActive && p.autoSell && p.ResourceDefinition == rd))
            {
                context.Reason = "logistics-auto-sell";
                Logic.LogisticsObserver.LogVerbose($"MARKET suppress new-buy-offer notification: body={oi.ObjectName} rd={rd.ID} (auto-sell active)");
                return true;
            }
        }
        else
        {
            if (data.requests.Any(r => r.autoBuy && r.ResourceDefinition == rd))
            {
                context.Reason = "logistics-auto-buy";
                Logic.LogisticsObserver.LogVerbose($"MARKET suppress new-sell-offer notification: body={oi.ObjectName} rd={rd.ID} (auto-buy active)");
                return true;
            }
        }

        return null;
    }

    private static bool? ShouldSuppressCycleReplan(SdkCycleReplanContext context)
    {
        var cmd = context?.Cycle;
        if (cmd?.customNameFromPlanMission == null
            || !cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return null;
        }

        if (!context.PlanFlyWas)
            return null;

        context.Reason = "logistics-already-planned";
        Logic.LogisticsObserver.LogVerbose($"SKIP LOGI replanning: {cmd.customNameFromPlanMission}");
        return true;
    }

    private static void OnCyclePlanNotification(SdkCycleNotificationContext context)
    {
        var cmd = context?.Cycle;
        if (cmd?.customNameFromPlanMission == null
            || !cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var parameter = context.MissionParameter;
            var checkResult = parameter?.CheckCanPlanMission().planMissionResult
                ?? PMMissionParameter.EPlanMissionResult.AllOk;
            var translated = Logic.LogisticsObserver.TranslatePlanMissionResult(checkResult);

            string tooltip = null;
            if (parameter != null && !parameter.CheckScheduleFly())
                tooltip = PMTabSchedule.GetTextToltip(parameter);

            var note = translated ?? tooltip;
            if (!string.IsNullOrEmpty(note) && cmd.B != null)
            {
                Logic.LogisticsObserver.SetCyclePlanFailureNote(cmd.B, cmd.cargoAllStart, note);
                Logic.LogisticsObserver.SetRelayCyclePlanFailureNote(cmd, note);
            }

            if (!string.IsNullOrEmpty(note) && cmd.ListSC != null)
                Logic.LogisticsObserver.SetShipBlockedReason(cmd.ListSC, note);

            context.FailureReason = note;
            context.Context = "logistics-cycle-notification";

            var routeKey = $"{cmd.A?.id ?? -1}->{cmd.B?.id ?? -1}";
            if (_notifiedCycleRoutes.Add(routeKey))
            {
                Logic.LogisticsObserver.LogWarning(
                    $"CYCLE notification-first: dispatchId={context.DispatchId ?? "none"} route={cmd.A?.ObjectName ?? "null"}->{cmd.B?.ObjectName ?? "null"} " +
                    $"name={cmd.customNameFromPlanMission} result={checkResult} translated=\"{translated ?? "none"}\" tooltip=\"{tooltip ?? "none"}\"");
                return;
            }

            context.SuppressNotification = true;
            Logic.LogisticsObserver.LogVerbose(
                $"CYCLE notification-suppressed: dispatchId={context.DispatchId ?? "none"} route={cmd.A?.ObjectName ?? "null"}->{cmd.B?.ObjectName ?? "null"} " +
                $"name={cmd.customNameFromPlanMission} result={checkResult} translated=\"{translated ?? "none"}\" tooltip=\"{tooltip ?? "none"}\"");
        }
        catch (Exception ex)
        {
            context.SuppressNotification = true;
            Logic.LogisticsObserver.LogError($"Sdk.CyclePlanNotification error: {ex}");
        }
    }

    private static void ResetNotifiedCycleRoutes() => _notifiedCycleRoutes.Clear();

    private static CycleMissionsData GetCycleFromParameter(PMMissionParameter parameter)
    {
        return (parameter?.SC as Spacecraft) == null
            ? null
            : MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission((Spacecraft)parameter.SC);
    }

    private static bool IsLogisticsCodeJob(PMMissionParameter parameter, CycleMissionsData cmdFromShip)
    {
        var isLogi = cmdFromShip?.customNameFromPlanMission != null
            && cmdFromShip.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal);
        if (!isLogi)
            isLogi = Logic.LogisticsObserver.IsLogisticsPlan(parameter);
        if (!isLogi
            && cmdFromShip?.customNameFromPlanMission != null
            && cmdFromShip.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal))
        {
            isLogi = true;
            Logic.LogisticsObserver.LogVerbose(
                $"LOGI-CODEJOB recovered-cycle-map: pmpName=\"{parameter?.MissionName ?? "null"}\" " +
                $"cmdName=\"{cmdFromShip.customNameFromPlanMission}\" sc={DescribeSpacecraft(parameter?.SC)} " +
                $"route={parameter?.Start?.ObjectName ?? "null"}->{parameter?.Target?.ObjectName ?? "null"}");
        }

        return isLogi;
    }

    private static string ResolveDispatchId(PMMissionParameter parameter, CycleMissionsData cmdFromShip)
    {
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(parameter);
        if (string.IsNullOrEmpty(dispatchId))
            dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(cmdFromShip);
        return dispatchId;
    }

    private static void ApplyLogisticsTransferFlags(PMMissionParameter parameter, CycleMissionsData cmdFromShip)
    {
        if (cmdFromShip.TransferType == ETransferType.Fastest
            && !ShouldLetStockHandleMoonCaseAsOptimal(parameter))
        {
            parameter.ClickFastestButton = true;
            parameter.TryFastAsPossible = true;
            parameter.TransferTypeMoonCase = ETransferType.Fastest;
        }
        else if (cmdFromShip.TransferType == ETransferType.Fastest)
        {
            parameter.ClickFastestButton = false;
            parameter.TryFastAsPossible = false;
            parameter.TransferTypeMoonCase = ETransferType.Optimal;
            Logic.LogisticsObserver.LogVerbose(
                $"LOGI-CODEJOB moon-fastest-safe-fallback: route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}");
        }
    }

    private static bool ShouldLetStockHandleMoonCaseAsOptimal(PMMissionParameter parameter)
    {
        var start = parameter?.Start;
        var target = parameter?.Target;
        if (start == null || target == null)
            return false;

        return Logic.LogisticsObserver.IsMoonCaseRoute(start, target)
            || ObjectInfo.CheckEarthMoonCase(start, target);
    }

    private static void RestoreLogisticsMissionName(PMMissionParameter parameter, string context)
    {
        if (!Logic.LogisticsObserver.IsLogisticsPlan(parameter))
            return;

        if (SolarSdk.MissionTags.ApplyMissionParameterName(parameter, context))
            Logic.LogisticsObserver.LogVerbose($"PLAN mission-name-prep: context={context} name=\"{parameter.MissionName}\"");
    }

    private static void LogTrajectoryDetails(PMMissionParameter parameter, string context)
    {
        if (parameter == null)
            return;

        try
        {
            var departure = parameter.DepartureTimeDate;
            var arrival = parameter.Arrival;
            var travelDays = (arrival - departure).TotalDays;
            var result = parameter.CheckCanPlanMission().planMissionResult;
            Logic.LogisticsObserver.LogVerbose(
                $"TRAJECTORY {context}: route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} " +
                $"sc={DescribeSpacecraft(parameter.SC)} departure={departure:yyyy-MM-dd} arrival={arrival:yyyy-MM-dd} " +
                $"travelDays={travelDays:0.#} allFuelNeed={parameter.AllFuelNeed:0.#} minFuelCost={parameter.MINFuelCost:0.#} " +
                $"moonCase={parameter.MoonCase} clickFastest={parameter.ClickFastestButton} " +
                $"transferTypeMC={parameter.TransferTypeMoonCase} planResult={result}");
            if (travelDays > 800)
            {
                Logic.LogisticsObserver.LogWarning(
                    $"TRAJECTORY-SUSPECT {context}: route={parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"} " +
                    $"travelDays={travelDays:0.#} - abnormally long trajectory detected");
            }
        }
        catch (Exception ex)
        {
            Logic.LogisticsObserver.LogWarning($"TRAJECTORY {context}: failed to log details: {ex.Message}");
        }
    }

    private static void OnSaveLoaded(string saveName)
    {
        if (!string.IsNullOrEmpty(saveName))
            Data.LogisticsPersistence.Load(saveName);

        ReconcileAfterLoad();
    }

    private static void ResetLoadState()
    {
        Data.LogisticsNetwork.ClearAll();
        Logic.LogisticsObserver.ResetRuntimeState();
        ResetNotifiedCycleRoutes();
    }

    private static void ReconcileAfterLoad()
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (player == null || cm == null)
            return;

        MatchCyclesToRequests(player, cm);
        Logic.LogisticsObserver.CleanupCompletedLogisticsMissionTrajectories(player);
    }

    private static void MatchCyclesToRequests(Company player, CycleMissionManager cm)
    {
        foreach (var requesterOI in Data.LogisticsNetwork.GetAllObjects())
        {
            var reqData = Data.LogisticsNetwork.Get(requesterOI);
            if (reqData == null)
                continue;

            foreach (var cmd in cm.GetAllCycleMission(player))
            {
                if (cmd.CheckComplete())
                    continue;
                if (cmd.B != requesterOI)
                    continue;
                if (!cmd.customNameFromPlanMission.StartsWith("[LOGI]", StringComparison.Ordinal))
                    continue;
                if (cmd.cargoAllStart?.Tab == null)
                    continue;

                foreach (var tabRes in cmd.cargoAllStart.Tab)
                {
                    foreach (var req in reqData.requests)
                    {
                        if (req.status != Data.LogisticsRequestStatus.Pending
                            && req.status != Data.LogisticsRequestStatus.InProgress)
                            continue;
                        if (req.ResourceDefinition != tabRes)
                            continue;

                        req.status = Data.LogisticsRequestStatus.InProgress;
                        if (req.relayFinalTargetObjectId <= 0)
                            req.relayFinalTargetObjectId = requesterOI.id;
                        Logic.LogisticsObserver.Log($"Reconciled cycle: {cmd.A?.ObjectName} -> {cmd.B?.ObjectName} ({tabRes.ID})");
                        break;
                    }
                }
            }
        }
    }

    private static void OnObjectInfoChanged(ObjectInfoWindow window, Game.ObjectInfoDataScripts.ObjectInfoData data, bool fromObjectName)
    {
        var ui = window?.GetComponent<UI.LogisticsUI>();
        if (ui != null && ui.isActiveAndEnabled)
            ui.RefreshData(data);
    }

    private static void OnObjectInfoRebuild(ObjectInfoWindow window)
    {
        var ui = window?.GetComponent<UI.LogisticsUI>();
        if (ui != null && ui.isActiveAndEnabled)
            ui.RebuildLayout();
    }

    private static string BuildLogisticsReservationMarker(UIRowRocket row)
    {
        var stack = row?.CurrentStackedRowRocketData;
        var first = stack?[0];
        var firstShip = first?.spacecraft;
        var type = firstShip?.spacecraftType;
        var location = firstShip?.CurrentlyOnThisObject;
        if (type == null || location == null || firstShip.GetCompany() == null)
            return null;

        var quota = LogisticsNetwork.GetQuotaEntry(location, type.ID, true)
            ?? LogisticsNetwork.GetQuotaEntry(location, type.NameRocketType ?? "SC", true);
        var markers = new List<string>();
        var reservedInRow = new HashSet<int>();

        if (quota != null && quota.count > 0)
        {
            var awayAssigned = LogisticsObserver.GetAwayLogisticsSpacecraftCountForQuota(location, quota);
            var localReserved = Math.Max(0, quota.count - awayAssigned);
            if (localReserved > 0)
            {
                var presentInRow = GetReadyShipIdsInStack(stack, location, type.ID, type.NameRocketType ?? "SC", excludeProviderAssigned: true);
                for (var i = 0; i < presentInRow.Count && i < localReserved; i++)
                    reservedInRow.Add(presentInRow[i]);
            }
        }

        foreach (var shipId in GetProviderAssignedShipIdsInStack(stack, location, type.ID, type.NameRocketType ?? "SC"))
            reservedInRow.Add(shipId);

        if (reservedInRow.Count > 0)
            markers.Add($"<color=#7EC8FF>[LOGI {reservedInRow.Count} reserved]</color>");

        var returnReserved = LogisticsObserver.GetReturnReservedSpacecraftCountAt(location, type.ID, type.NameRocketType ?? "SC");
        if (returnReserved > 0)
            markers.Add($"<color=#FFB86C>[LOGI {returnReserved} return]</color>");

        return markers.Count == 0 ? null : string.Join(" ", markers);
    }

    private static List<int> GetReadyShipIdsInStack(StackedRowRocketData stack, ObjectInfo location, string typeId, string fallbackName, bool excludeProviderAssigned)
    {
        var result = new List<int>();
        if (stack == null || location == null)
            return result;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;

        for (var i = 0; i < stack.Count; i++)
        {
            var sc = stack[i]?.spacecraft;
            if (sc?.spacecraftType == null || sc.ID < 0)
                continue;
            if (sc.CurrentlyOnThisObject != location)
                continue;
            if (!LogisticsNetwork.IsSpacecraftReadyForLogistics(sc, player, cm))
                continue;
            if (excludeProviderAssigned && LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID) != null)
                continue;
            if (!LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = typeId }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName)
                && !LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = fallbackName }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName))
                continue;

            result.Add(sc.ID);
        }

        return result;
    }

    private static List<int> GetProviderAssignedShipIdsInStack(StackedRowRocketData stack, ObjectInfo location, string typeId, string fallbackName)
    {
        var result = new List<int>();
        if (stack == null || location == null)
            return result;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;

        for (var i = 0; i < stack.Count; i++)
        {
            var sc = stack[i]?.spacecraft;
            if (sc?.spacecraftType == null || sc.ID < 0)
                continue;
            if (sc.GetCompany() != player)
                continue;
            if (sc.CurrentlyOnThisObject != location)
                continue;
            if (LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID) == null)
                continue;
            if (!LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = typeId }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName)
                && !LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = fallbackName }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName))
                continue;

            result.Add(sc.ID);
        }

        return result;
    }

    private static string DescribeSpacecraft(ISpacecraftInfo spacecraftInfo)
    {
        if (spacecraftInfo is not Spacecraft sc)
            return spacecraftInfo?.GetTypeSpaceCraft()?.NameRocketType ?? "null";

        return $"{sc.GetSpacecraftName() ?? sc.spacecraftName ?? sc.spacecraftType?.NameRocketType ?? "SC"}#{sc.ID}/phase={sc.CurrentPhase}";
    }

    private static bool HasPositiveNormalResourceCargo(CargoAll cargoAll)
    {
        return SolarSdk.MissionLoadout.GetRegularResourceCargoItems(cargoAll)
            .Any(cargo => cargo != null && cargo.cargoMass > 0);
    }
}
