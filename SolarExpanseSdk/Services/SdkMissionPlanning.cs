using System;
using CustomUpdate;
using Game;
using Game.Info;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.UI.Windows.Elements.PlanMissionElements.PMScheduleElements;
using HarmonyLib;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Low-level mission-planning event bridge raised from SDK Harmony patches around stock
/// mission planner methods. Consumer mods use this for observation and small overrides,
/// while stock planning remains responsible for route and mission creation behavior.
/// </summary>
public sealed class SdkMissionPlanning
{
    private SdkLogging _log;

    /// <summary>
    /// Raised before a stock code-job mission plan starts.
    /// </summary>
    public event Action<PMMissionParameter, MissionPlanContext> BeforeCodeJobPlan;
    /// <summary>
    /// Raised before stock fastest-search behavior runs.
    /// </summary>
    public event Action<PMTabSchedule, PMMissionParameter, MissionPlanContext> BeforeFastestSearch;
    /// <summary>
    /// Raised after stock fastest-search behavior runs.
    /// </summary>
    public event Action<PMMissionParameter, MissionPlanContext> AfterFastestSearch;
    /// <summary>
    /// Raised inside the stock code-job callback wrapper immediately before stock creates the mission.
    /// </summary>
    public event Action<PMMissionParameter, MissionPlanContext> BeforeCodeJobCallback;
    /// <summary>
    /// Raised inside the stock code-job callback wrapper after stock creates the mission or the callback returns.
    /// </summary>
    public event Action<PMMissionParameter, MissionPlanContext> AfterCodeJobCallback;
    /// <summary>
    /// Raised before stock creates a scheduled flight. Return true to suppress stock launch creation.
    /// </summary>
    public event Func<SdkCreateFlyContext, bool?> BeforeCreateFly;
    /// <summary>
    /// Raised after stock create-fly behavior returns.
    /// </summary>
    public event Action<SdkCreateFlyContext> AfterCreateFly;
    /// <summary>
    /// Raised before stock creates a preview trajectory. Return true to suppress preview creation.
    /// </summary>
    public event Func<PMTabSchedule, PMMissionParameter, MissionPlanContext, bool?> SuppressPreviewTrajectory;
    /// <summary>
    /// Raised after a stock mission is completed.
    /// </summary>
    public event Action<MissionInfo> MissionCompleted;
    /// <summary>
    /// Optional handlers that can override stock self-launch checks.
    /// </summary>
    public event Func<PMMissionParameter, bool?> CheckSelfLaunchOverride;
    /// <summary>
    /// Optional handlers that can suppress stock arrival notifications.
    /// </summary>
    public event Func<Spacecraft, string, bool?> SuppressArrivalNotification;
    /// <summary>
    /// Optional handlers that can provide mission display names.
    /// </summary>
    public event Func<MissionInfo, string> MissionDisplayNameOverride;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Dispatches the pre-code-job event and attempts to correlate the parameter with an SDK dispatch ID.
    /// </summary>
    public void RaiseBeforeCodeJobPlan(PMMissionParameter parameter, MissionPlanContext context)
    {
        var dispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(parameter);
        if (!string.IsNullOrEmpty(dispatchId))
        {
            context.DispatchId = dispatchId;
            Core.SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, "missionPlanning-before");
        }
        else if (IsLikelyLogisticsPlan(parameter))
        {
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("missing-dispatch-correlation", $"{parameter?.MissionName ?? "unnamed"}:{parameter?.Start?.id ?? -1}->{parameter?.Target?.id ?? -1}");
        }
        _log?.Verbose("sdk.missionPlanning", $"codeJob-before dispatchId={context.DispatchId ?? "none"} route=\"{context.Source ?? "null"}->{context.Target ?? "null"}\" tag={context.Tag ?? "none"} subscribers={BeforeCodeJobPlan?.GetInvocationList().Length ?? 0}");
        SafeInvoke(BeforeCodeJobPlan, parameter, context);
    }

    /// <summary>
    /// Dispatches the pre-fastest-search event. Subscribers can mutate the schedule/parameter.
    /// </summary>
    public void RaiseBeforeFastestSearch(PMTabSchedule schedule, PMMissionParameter parameter, MissionPlanContext context)
    {
        RefreshDispatchContext(parameter, context, "missionPlanning-beforeFastest");
        _log?.Verbose("sdk.missionPlanning", $"fastest-before dispatchId={context.DispatchId ?? "none"} route=\"{context.Source ?? "null"}->{context.Target ?? "null"}\" tag={context.Tag ?? "none"} subscribers={BeforeFastestSearch?.GetInvocationList().Length ?? 0}");
        if (BeforeFastestSearch == null)
            return;

        foreach (Action<PMTabSchedule, PMMissionParameter, MissionPlanContext> handler in BeforeFastestSearch.GetInvocationList())
        {
            try
            {
                handler(schedule, parameter, context);
            }
            catch (Exception ex)
            {
                _log?.Warning($"Fastest-search handler failed: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("fastest-search-handler-error", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Dispatches the code-job callback pre-stock event from the SDK-owned callback wrapper.
    /// </summary>
    public void RaiseBeforeCodeJobCallback(PMMissionParameter parameter, MissionPlanContext context)
    {
        RefreshDispatchContext(parameter, context, "missionPlanning-callback-before");
        _log?.Verbose("sdk.missionPlanning", $"codeJob-callback-before dispatchId={context.DispatchId ?? "none"} route=\"{context.Source ?? "null"}->{context.Target ?? "null"}\" tag={context.Tag ?? "none"} subscribers={BeforeCodeJobCallback?.GetInvocationList().Length ?? 0}");
        SafeInvoke(BeforeCodeJobCallback, parameter, context);
    }

    /// <summary>
    /// Dispatches the code-job callback post-stock event from the SDK-owned callback wrapper.
    /// </summary>
    public void RaiseAfterCodeJobCallback(PMMissionParameter parameter, MissionPlanContext context)
    {
        RefreshDispatchContext(parameter, context, "missionPlanning-callback-after");
        _log?.Verbose("sdk.missionPlanning", $"codeJob-callback-after dispatchId={context.DispatchId ?? "none"} route=\"{context.Source ?? "null"}->{context.Target ?? "null"}\" tag={context.Tag ?? "none"} subscribers={AfterCodeJobCallback?.GetInvocationList().Length ?? 0}");
        SafeInvoke(AfterCodeJobCallback, parameter, context);
    }

    /// <summary>
    /// Dispatches the post-fastest-search event and refreshes dispatch correlation if possible.
    /// </summary>
    public void RaiseAfterFastestSearch(PMMissionParameter parameter, MissionPlanContext context)
    {
        var dispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(parameter);
        if (!string.IsNullOrEmpty(dispatchId))
        {
            context.DispatchId = dispatchId;
            Core.SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, "missionPlanning-afterFastest");
        }
        _log?.Verbose("sdk.missionPlanning", $"fastest-after dispatchId={context.DispatchId ?? "none"} route=\"{context.Source ?? "null"}->{context.Target ?? "null"}\" tag={context.Tag ?? "none"} subscribers={AfterFastestSearch?.GetInvocationList().Length ?? 0}");
        SafeInvoke(AfterFastestSearch, parameter, context);
    }

    /// <summary>
    /// Applies the stock code-job fastest-route delta-V correction for code-created plans.
    /// </summary>
    /// <remarks>
    /// Stock does not initialize the porkchop effective delta-V for ForCode mission windows.
    /// This helper computes max delta-V from full fuel and pushes the schedule fuel UI to
    /// max so stock fastest search has a valid search envelope.
    /// </remarks>
    public bool ApplyCodeFastestDeltaVCorrection(PMTabSchedule schedule, string context = null, double protectedReserveFuel = 0.0)
    {
        var pmp = schedule?.PlanMissionWindow?.PMMissionParameter;
        if (schedule?.PlanMissionWindow == null || !schedule.PlanMissionWindow.ForCode)
            return false;
        if (pmp?.SC == null || pmp.CargoAll?.cargoFuel == null)
            return false;

        var scType = pmp.SC.GetTypeSpaceCraft();
        if (scType == null || scType.NotUsePorkchope || scType.SolarSC)
            return false;

        var exhaustV = scType.GetExhaustV(pmp.FlyCompany);
        var cargoMass = pmp.CargoAll.CargoCurrent;
        var dryMass = (double)pmp.SC.GetMass() + cargoMass;
        var maxFuelCapacity = scType.GetFuelCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        var wetMass = dryMass + (double)maxFuelCapacity;
        var protectedReserve = Math.Max(0.0, Math.Min(protectedReserveFuel, maxFuelCapacity));
        var terminalMass = dryMass + protectedReserve;
        if (wetMass <= terminalMass || dryMass <= 0 || exhaustV <= 0)
            return false;

        var effectiveDeltaV = (int)((double)exhaustV * Math.Log(wetMass / terminalMass, Math.E));
        schedule.porkchop.SetEffectiveDeltaV(effectiveDeltaV);

        var fuelUI = Traverse.Create(schedule).Field("fuelSpaceCraftUI").GetValue<FuelSpaceCraftUI>();
        if (fuelUI != null)
        {
            pmp.CargoAll.cargoFuel.cargoMassPotencjal = maxFuelCapacity;
            fuelUI.SliderSetValue((float)fuelUI.MaxSlider);
        }

        _log?.Verbose("sdk.missionPlanning",
            $"fastest-dv-correction context={context ?? "none"} route=\"{pmp.Start?.ObjectName ?? "null"}->{pmp.Target?.ObjectName ?? "null"}\" effectiveDV={effectiveDeltaV} exhaustV={exhaustV:0.#} dryMass={dryMass:0.#} cargoMass={cargoMass:0.#} fuelCap={maxFuelCapacity:0.#} protectedReserve={protectedReserve:0.#} wetMass={wetMass:0.#} terminalMass={terminalMass:0.#}");
        return true;
    }

    /// <summary>
    /// Dispatches the pre-create-fly hook and returns true when stock launch creation should be suppressed.
    /// </summary>
    public bool ShouldSuppressCreateFly(PMTabSchedule schedule, PMMissionParameter parameter, MissionPlanContext context)
    {
        var createContext = new SdkCreateFlyContext
        {
            Schedule = schedule,
            Parameter = parameter,
            PlanContext = context,
            DispatchId = context?.DispatchId,
            MissionName = parameter?.MissionName,
            Source = parameter?.Start,
            Target = parameter?.Target
        };
        _log?.Verbose("sdk.missionPlanning", $"create-fly-before dispatchId={createContext.DispatchId ?? "none"} route=\"{createContext.Source?.ObjectName ?? "null"}->{createContext.Target?.ObjectName ?? "null"}\" subscribers={BeforeCreateFly?.GetInvocationList().Length ?? 0}");
        if (BeforeCreateFly == null)
            return false;

        foreach (Func<SdkCreateFlyContext, bool?> handler in BeforeCreateFly.GetInvocationList())
        {
            try
            {
                var result = handler(createContext);
                if (result == true || createContext.SuppressLaunch)
                {
                    createContext.SuppressLaunch = true;
                    _log?.Warning("sdk.missionPlanning", $"create-fly-suppressed dispatchId={createContext.DispatchId ?? "none"} route=\"{createContext.Source?.ObjectName ?? "null"}->{createContext.Target?.ObjectName ?? "null"}\" reason={createContext.SuppressReason ?? "subscriber"}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"Create-fly handler failed: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("create-fly-handler-error", ex.GetType().Name);
            }
        }

        return false;
    }

    /// <summary>
    /// Dispatches the post-create-fly hook.
    /// </summary>
    public void RaiseAfterCreateFly(PMTabSchedule schedule, PMMissionParameter parameter, MissionPlanContext context)
    {
        var createContext = new SdkCreateFlyContext
        {
            Schedule = schedule,
            Parameter = parameter,
            PlanContext = context,
            DispatchId = context?.DispatchId,
            MissionName = parameter?.MissionName,
            Source = parameter?.Start,
            Target = parameter?.Target
        };
        _log?.Verbose("sdk.missionPlanning", $"create-fly-after dispatchId={createContext.DispatchId ?? "none"} route=\"{createContext.Source?.ObjectName ?? "null"}->{createContext.Target?.ObjectName ?? "null"}\" subscribers={AfterCreateFly?.GetInvocationList().Length ?? 0}");
        if (AfterCreateFly == null)
            return;

        foreach (Action<SdkCreateFlyContext> handler in AfterCreateFly.GetInvocationList())
        {
            try
            {
                handler(createContext);
            }
            catch (Exception ex)
            {
                _log?.Warning($"After create-fly handler failed: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("create-fly-after-handler-error", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Returns true when a subscriber wants to suppress stock preview trajectory creation.
    /// </summary>
    public bool ShouldSuppressPreviewTrajectory(PMTabSchedule schedule, PMMissionParameter parameter, MissionPlanContext context)
    {
        RefreshDispatchContext(parameter, context, "missionPlanning-preview-trajectory");
        if (SuppressPreviewTrajectory == null)
            return false;

        foreach (Func<PMTabSchedule, PMMissionParameter, MissionPlanContext, bool?> handler in SuppressPreviewTrajectory.GetInvocationList())
        {
            try
            {
                var result = handler(schedule, parameter, context);
                if (result == true)
                {
                    _log?.Verbose("sdk.missionPlanning", $"preview-trajectory-suppressed dispatchId={context.DispatchId ?? "none"} route=\"{context.Source ?? "null"}->{context.Target ?? "null"}\"");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"Preview trajectory handler failed: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("preview-trajectory-handler-error", ex.GetType().Name);
            }
        }

        return false;
    }

    /// <summary>
    /// Dispatches mission completion subscribers.
    /// </summary>
    public void RaiseMissionCompleted(MissionInfo missionInfo)
    {
        var dispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(missionInfo);
        if (!string.IsNullOrEmpty(dispatchId))
            Core.SolarSdk.CyclicalMissions.MarkCompleted(dispatchId, "MissionInfo.Complete");
        _log?.Verbose("sdk.missionPlanning", $"mission-complete mission={missionInfo?.id ?? -1} dispatchId={dispatchId ?? "none"} subscribers={MissionCompleted?.GetInvocationList().Length ?? 0}");
        if (MissionCompleted == null)
            return;

        foreach (Action<MissionInfo> handler in MissionCompleted.GetInvocationList())
        {
            try
            {
                handler(missionInfo);
            }
            catch (Exception ex)
            {
                _log?.Warning($"Mission completion handler failed: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("mission-complete-handler-error", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Invokes self-launch override handlers until one returns a value.
    /// </summary>
    public bool? RaiseCheckSelfLaunchOverride(PMMissionParameter parameter)
    {
        if (CheckSelfLaunchOverride == null)
            return null;

        foreach (Func<PMMissionParameter, bool?> handler in CheckSelfLaunchOverride.GetInvocationList())
        {
            try
            {
                var result = handler(parameter);
                if (result.HasValue)
                {
                    _log?.Verbose("sdk.missionPlanning", $"self-launch-override result={result.Value} route=\"{parameter?.Start?.ObjectName ?? "null"}->{parameter?.Target?.ObjectName ?? "null"}\"");
                    return result;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"Self launch override handler failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Invokes arrival-notification suppression handlers until one returns a value.
    /// </summary>
    public bool ShouldSuppressArrivalNotification(Spacecraft spacecraft, string context)
    {
        if (SuppressArrivalNotification == null)
            return false;

        foreach (Func<Spacecraft, string, bool?> handler in SuppressArrivalNotification.GetInvocationList())
        {
            try
            {
                var result = handler(spacecraft, context);
                if (result.HasValue)
                {
                    _log?.Verbose("sdk.missionPlanning", $"arrival-notification context={context} ship={spacecraft?.ID ?? -1} suppress={result.Value}");
                    return result.Value;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"Arrival notification handler failed: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Invokes mission display-name handlers until one returns a non-empty name.
    /// </summary>
    public string GetMissionDisplayNameOverride(MissionInfo missionInfo)
    {
        if (MissionDisplayNameOverride == null)
            return null;

        foreach (Func<MissionInfo, string> handler in MissionDisplayNameOverride.GetInvocationList())
        {
            try
            {
                var result = handler(missionInfo);
                if (!string.IsNullOrEmpty(result))
                {
                    _log?.Verbose("sdk.missionPlanning", $"mission-display-name mission={missionInfo?.id ?? -1} result=\"{result}\"");
                    return result;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"Mission name override handler failed: {ex.Message}");
            }
        }

        return null;
    }

    private static void RefreshDispatchContext(PMMissionParameter parameter, MissionPlanContext context, string registrationContext)
    {
        if (context == null)
            return;

        var dispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(parameter);
        if (string.IsNullOrEmpty(dispatchId))
            return;

        context.DispatchId = dispatchId;
        Core.SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, registrationContext);
    }

    private void SafeInvoke(Action<PMMissionParameter, MissionPlanContext> action, PMMissionParameter parameter, MissionPlanContext context)
    {
        if (action == null)
            return;

        foreach (Action<PMMissionParameter, MissionPlanContext> handler in action.GetInvocationList())
        {
            try
            {
                handler(parameter, context);
            }
            catch (Exception ex)
            {
                _log?.Warning($"Mission planning handler failed: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("mission-planning-handler-error", ex.GetType().Name);
            }
        }
    }

    private static bool IsLikelyLogisticsPlan(PMMissionParameter parameter)
    {
        return parameter?.MissionName != null
            && parameter.MissionName.StartsWith("[LOGI", StringComparison.Ordinal);
    }
}

/// <summary>
/// Lightweight context object passed with low-level mission-planning events.
/// </summary>
public sealed class MissionPlanContext
{
    /// <summary>Source object name, when known.</summary>
    public string Source { get; set; }
    /// <summary>Target object name, when known.</summary>
    public string Target { get; set; }
    /// <summary>Mission or cycle tag, when known.</summary>
    public string Tag { get; set; }
    /// <summary>SDK dispatch ID resolved for the planning operation.</summary>
    public string DispatchId { get; set; }
    /// <summary>Mod owner tag for the dispatch.</summary>
    public string OwnerTag { get; set; }
}

/// <summary>
/// Context passed around SDK create-fly hooks.
/// </summary>
public sealed class SdkCreateFlyContext
{
    /// <summary>Stock schedule tab.</summary>
    public PMTabSchedule Schedule { get; set; }
    /// <summary>Stock mission parameter about to launch.</summary>
    public PMMissionParameter Parameter { get; set; }
    /// <summary>General mission plan context.</summary>
    public MissionPlanContext PlanContext { get; set; }
    /// <summary>Resolved SDK dispatch ID, if any.</summary>
    public string DispatchId { get; set; }
    /// <summary>Mission display name at the boundary.</summary>
    public string MissionName { get; set; }
    /// <summary>Source object.</summary>
    public ObjectInfo Source { get; set; }
    /// <summary>Target object.</summary>
    public ObjectInfo Target { get; set; }
    /// <summary>Set true to skip stock CreateFly.</summary>
    public bool SuppressLaunch { get; set; }
    /// <summary>Diagnostic reason for suppression.</summary>
    public string SuppressReason { get; set; }
}
