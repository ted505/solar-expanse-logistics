using System;
using System.Collections.Generic;
using System.Linq;
using CustomUpdate;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using Manager;
using ScriptableObjectScripts;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Tracks SDK dispatch IDs across stock cycle data, mission parameters, carriers, mission info,
/// and verbose diagnostics. This service observes and correlates cycles; it does not construct
/// stock <see cref="CycleMissionsData"/> objects yet.
/// </summary>
public sealed class SdkCyclicalMissions
{
    private readonly Dictionary<string, SdkCycleTracker> _trackersByDispatchId = new Dictionary<string, SdkCycleTracker>();
    private readonly Dictionary<CycleMissionsData, string> _dispatchByCycle = new Dictionary<CycleMissionsData, string>();
    private readonly Dictionary<PMMissionParameter, string> _dispatchByMissionParameter = new Dictionary<PMMissionParameter, string>();
    private readonly Dictionary<int, string> _dispatchByShipId = new Dictionary<int, string>();
    private readonly Dictionary<int, string> _dispatchByCarrierInstanceId = new Dictionary<int, string>();
    private readonly Dictionary<int, string> _dispatchByMissionId = new Dictionary<int, string>();
    private int _dispatchSequence;
    private SdkLogging _log;

    /// <summary>
    /// Lets consumer mods decide whether stock should skip a cyclical mission planning attempt.
    /// </summary>
    /// <remarks>
    /// Return <c>true</c> to suppress the stock call, <c>false</c> to explicitly allow it, or
    /// <c>null</c> when the subscriber does not own the cycle.
    /// </remarks>
    public event Func<SdkCycleReplanContext, bool?> CheckCycleReplan;

    /// <summary>
    /// Raised before stock displays a cyclical mission failure notification.
    /// </summary>
    /// <remarks>
    /// Subscribers can annotate the context, record mod state, and set
    /// <see cref="SdkCycleNotificationContext.SuppressNotification"/> to hide repeat or owned
    /// diagnostics notifications.
    /// </remarks>
    public event Action<SdkCycleNotificationContext> CyclePlanNotification;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Returns all stock cycle missions for a company, filtering null entries.
    /// </summary>
    public List<CycleMissionsData> GetAll(Company company)
    {
        if (company == null)
            return new List<CycleMissionsData>();

        var manager = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        return manager?.GetAllCycleMission(company)?.Where(c => c != null).ToList()
            ?? new List<CycleMissionsData>();
    }

    /// <summary>
    /// Returns stock cycle missions whose custom names start with a mod-owned tag prefix.
    /// </summary>
    public List<CycleMissionsData> FindTagged(Company company, string tagPrefix)
    {
        return GetAll(company)
            .Where(c => c.customNameFromPlanMission != null
                && c.customNameFromPlanMission.StartsWith(tagPrefix, System.StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Returns true when a stock cycle mission name starts with the supplied tag prefix.
    /// </summary>
    public bool IsTagged(CycleMissionsData data, string tagPrefix)
    {
        return data?.customNameFromPlanMission != null
            && data.customNameFromPlanMission.StartsWith(tagPrefix, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates a stable-looking dispatch ID for one mission/cycle attempt.
    /// </summary>
    /// <remarks>
    /// IDs are process-local and diagnostic, not durable save identifiers. The current shape is
    /// <c>owner-yyyyMMdd-NNNN</c>.
    /// </remarks>
    public string CreateDispatchId(string ownerTag)
    {
        var prefix = string.IsNullOrWhiteSpace(ownerTag) ? "sdk" : ownerTag.Trim().ToLowerInvariant();
        var id = $"{prefix}-{System.DateTime.UtcNow:yyyyMMdd}-{++_dispatchSequence:0000}";
        _log?.Verbose("sdk.cycles", $"dispatch-id create owner={ownerTag ?? "null"} id={id}");
        return id;
    }

    /// <summary>
    /// Associates a stock cycle mission with an SDK dispatch ID and optional primary real spacecraft.
    /// </summary>
    /// <remarks>
    /// Call this immediately after the mod or stock code adds a cycle through <see cref="CycleMissionManager"/>.
    /// </remarks>
    public void RegisterPlannedCycle(string dispatchId, string ownerTag, CycleMissionsData cycle, Spacecraft primaryShip, string routeSummary)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || cycle == null)
            return;

        var tracker = new SdkCycleTracker
        {
            DispatchId = dispatchId,
            OwnerTag = ownerTag,
            CycleName = cycle.customNameFromPlanMission,
            RouteSummary = routeSummary,
            PrimaryShipId = primaryShip?.ID ?? -1,
            PrimaryShipName = primaryShip?.GetSpacecraftName(),
            SourceId = cycle.A?.id ?? -1,
            SourceName = cycle.A?.ObjectName,
            TargetId = cycle.B?.id ?? -1,
            TargetName = cycle.B?.ObjectName,
            CreatedAtUtc = System.DateTime.UtcNow,
            LastPhase = "planned"
        };

        _trackersByDispatchId[dispatchId] = tracker;
        _dispatchByCycle[cycle] = dispatchId;
        if (primaryShip?.ID >= 0)
            _dispatchByShipId[primaryShip.ID] = dispatchId;
        RegisterCarrier(dispatchId, primaryShip, "planned-cycle");

        _log?.Verbose("sdk.cycles", $"create requestId={dispatchId} tag={ownerTag ?? "none"} route=\"{routeSummary ?? tracker.SourceName + "->" + tracker.TargetName}\" ship={tracker.PrimaryShipId} cycle=\"{tracker.CycleName ?? "null"}\"");
    }

    /// <summary>
    /// Associates a stock mission parameter with an existing dispatch ID as soon as stock planning exposes it.
    /// </summary>
    public void RegisterMissionParameter(string dispatchId, PMMissionParameter parameter, string context = null)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || parameter == null)
            return;

        _dispatchByMissionParameter[parameter] = dispatchId;
        RegisterCarrier(dispatchId, parameter.SC as Spacecraft, context ?? "mission-parameter");
        if (_trackersByDispatchId.TryGetValue(dispatchId, out var tracker))
        {
            tracker.LastContext = context;
            tracker.UpdatedAtUtc = System.DateTime.UtcNow;
        }
        _log?.Verbose("sdk.cycles", $"mission-parameter requestId={dispatchId} context={context ?? "none"} route=\"{parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}\" carrier={(parameter.SC as Spacecraft)?.ID ?? -1}");
    }

    /// <summary>
    /// Associates a real or synthetic carrier instance with a dispatch ID.
    /// </summary>
    /// <remarks>
    /// Real spacecraft also map by positive ship ID. Synthetic carriers are resolved by Unity instance ID.
    /// </remarks>
    public void RegisterCarrier(string dispatchId, Spacecraft carrier, string context = null)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || carrier == null)
            return;

        _dispatchByCarrierInstanceId[carrier.GetInstanceID()] = dispatchId;
        if (carrier.ID >= 0)
            _dispatchByShipId[carrier.ID] = dispatchId;
        _log?.Verbose("sdk.cycles", $"carrier requestId={dispatchId} context={context ?? "none"} carrier={carrier.GetSpacecraftName() ?? "null"}#{carrier.ID} instance={carrier.GetInstanceID()}");
    }

    /// <summary>
    /// Associates created stock mission info with a dispatch ID.
    /// </summary>
    public void RegisterMissionInfo(string dispatchId, MissionInfo missionInfo)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || missionInfo == null)
            return;

        var alreadyRegistered = _dispatchByMissionId.TryGetValue(missionInfo.id, out var existingDispatchId)
            && string.Equals(existingDispatchId, dispatchId, System.StringComparison.Ordinal);
        if (_trackersByDispatchId.TryGetValue(dispatchId, out var tracker))
        {
            tracker.MissionInfoId = missionInfo.id;
            tracker.LastPhase = "mission-info";
            tracker.UpdatedAtUtc = System.DateTime.UtcNow;
            tracker.MissionInfoRegistrationCount++;
        }
        _dispatchByMissionId[missionInfo.id] = dispatchId;
        if (alreadyRegistered)
        {
            _log?.VerboseThrottled("sdk.cycles", $"mission-info-repeat-{dispatchId}-{missionInfo.id}", $"mission-info-repeat requestId={dispatchId} mission={missionInfo.id} name=\"{missionInfo.missionName ?? "null"}\"", 10.0);
            return;
        }

        _log?.Verbose("sdk.cycles", $"mission-info requestId={dispatchId} mission={missionInfo.id} name=\"{missionInfo.missionName ?? "null"}\"");
    }

    /// <summary>Marks a dispatch as having entered the code-job planner.</summary>
    public void MarkCodeJobStarted(string dispatchId, string context = null) => Mark(dispatchId, "codejob-started", context, null);
    /// <summary>Marks a dispatch as having completed the code-job planner callback path.</summary>
    public void MarkCodeJobCompleted(string dispatchId, string context = null) => Mark(dispatchId, "codejob-completed", context, null);
    /// <summary>Marks a dispatch as having failed in the code-job planner callback path.</summary>
    public void MarkCodeJobFailed(string dispatchId, string reason, string context = null) => Mark(dispatchId, "codejob-failed", context, reason);
    /// <summary>Marks a dispatch as completed.</summary>
    public void MarkCompleted(string dispatchId, string context = null) => Mark(dispatchId, "completed", context, null);
    /// <summary>Marks a dispatch as failed and writes a rate-limited diagnostics snapshot.</summary>
    public void MarkFailed(string dispatchId, string reason, string context = null) => Mark(dispatchId, "failed", context, reason);

    /// <summary>
    /// Creates an <see cref="EndsResourceCountData"/> for one resource.
    /// </summary>
    public EndsResourceCountData CreateResourceCount(ResourceDefinition resource, double amount)
    {
        var data = new EndsResourceCountData();
        if (resource != null && amount > 0)
            data.listData.Add(new EndsResourceCountDataPart { rd = resource, count = amount });
        return data;
    }

    /// <summary>
    /// Creates resource-count completion data from a cargo manifest, with a fallback resource.
    /// </summary>
    public EndsResourceCountData CreateResourceCountFromCargo(CargoAll cargoAll, ResourceDefinition fallbackResource, double fallbackAmount)
    {
        var data = new EndsResourceCountData();
        foreach (var cargo in Core.SolarSdk.MissionLoadout.GetRegularResourceCargoItems(cargoAll))
        {
            if (cargo?.resourceType == null || cargo.cargoMass <= 0)
                continue;
            data.listData.Add(new EndsResourceCountDataPart { rd = cargo.resourceType, count = cargo.cargoMass });
        }

        if (data.listData.Count == 0 && fallbackResource != null && fallbackAmount > 0)
            data.listData.Add(new EndsResourceCountDataPart { rd = fallbackResource, count = fallbackAmount });
        return data;
    }

    /// <summary>
    /// Formats resource-count data for diagnostics.
    /// </summary>
    public string FormatResourceCount(EndsResourceCountData data)
    {
        if (data?.listData == null || data.listData.Count == 0)
            return "empty";

        var parts = new List<string>();
        foreach (var part in data.listData)
        {
            if (part?.rd == null)
                continue;
            parts.Add($"{part.rd.ID}:{part.count:0.#}");
        }
        return parts.Count == 0 ? "empty" : string.Join(",", parts);
    }

    /// <summary>
    /// Builds a stock <see cref="CycleMissionsData"/> from an SDK cycle draft.
    /// </summary>
    public CycleMissionsData CreateCycle(SdkCycleDraft draft)
    {
        if (draft == null)
            throw new ArgumentNullException(nameof(draft));

        var data = new CycleMissionsDataData
        {
            CustomNameFromPlanMission = draft.CustomName,
            A = draft.Source,
            B = draft.Target,
            Company = draft.Company,
            CargoStart = draft.CargoStart,
            CargoEnd = draft.CargoEnd,
            CargoAllStart = draft.CargoAllStart ?? CargoAll.CreateCargoEmpty(),
            CargoAllEnd = draft.CargoAllEnd ?? CargoAll.CreateCargoEmpty(),
            LvTypeA = draft.LaunchVehicleTypeA,
            LvTypeB = draft.LaunchVehicleTypeB,
            TransferType = draft.TransferType,
            Ends = draft.Ends,
            EndsObjectUntil = draft.EndsObjectUntil,
            EndsObjectThisManyTimes = draft.EndsObjectThisManyTimes,
            EndsResourceCountDataA = draft.EndsResourceCountDataA ?? new EndsResourceCountData(),
            EndsResourceCountMaxA = draft.EndsResourceCountMaxA ?? new EndsResourceCountData(),
            EndsResourceCountDataB = draft.EndsResourceCountDataB ?? new EndsResourceCountData(),
            EndsResourceCountMaxB = draft.EndsResourceCountMaxB ?? new EndsResourceCountData(),
            ListSC = draft.Spacecraft
        };

        var cycle = new CycleMissionsData(data);
        if (!string.IsNullOrEmpty(draft.CustomName))
            cycle.customNameFromPlanMission = draft.CustomName;
        _log?.Verbose("sdk.cycles", $"cycle-created name=\"{cycle.customNameFromPlanMission ?? "null"}\" route=\"{cycle.A?.ObjectName ?? "null"}->{cycle.B?.ObjectName ?? "null"}\" ends={cycle.Ends} transfer={cycle.TransferType}");
        return cycle;
    }

    /// <summary>
    /// Builds, adds, registers, and optionally reserves a stock cycle mission from an SDK draft.
    /// </summary>
    public SdkCycleCreateResult CreateAndAddCycle(SdkCycleDraft draft, Spacecraft primaryShip, string ownerTag, string reservationOwnerId, string context, bool reserveCarrier = true)
    {
        try
        {
            var cycle = CreateCycle(draft);
            return AddAndRegisterCycle(cycle, primaryShip, draft?.Spacecraft, ownerTag, reservationOwnerId, context, reserveCarrier);
        }
        catch (Exception ex)
        {
            _log?.Error("sdk.cycles", $"cycle-create-failed owner={ownerTag ?? "none"} context={context ?? "none"} error={ex}");
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("cycle-create-failed", $"{ownerTag ?? "none"}:{context ?? "none"}");
            return new SdkCycleCreateResult
            {
                Success = false,
                FailureCode = "cycle-create-exception",
                FailureReason = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Adds an existing stock cycle to <see cref="CycleMissionManager"/> and registers SDK dispatch state.
    /// </summary>
    public SdkCycleCreateResult AddAndRegisterCycle(CycleMissionsData cycle, Spacecraft primaryShip, List<ISpacecraftInfo> spacecraft, string ownerTag, string reservationOwnerId, string context, bool reserveCarrier = true)
    {
        if (cycle == null || primaryShip == null)
            return new SdkCycleCreateResult { Success = false, FailureCode = "invalid-arguments", FailureReason = "Cycle or primary ship was null." };

        var manager = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (manager == null)
            return new SdkCycleCreateResult { Success = false, FailureCode = "missing-cycle-manager", FailureReason = "CycleMissionManager was unavailable." };

        var list = spacecraft ?? cycle.ListSC ?? new List<ISpacecraftInfo> { primaryShip };
        manager.AddCycleMission(primaryShip, cycle, list);

        var dispatchId = CreateDispatchId(ownerTag);
        var routeSummary = $"{cycle.A?.ObjectName ?? "null"}->{cycle.B?.ObjectName ?? "null"}";
        RegisterPlannedCycle(dispatchId, ownerTag, cycle, primaryShip, routeSummary);

        if (reserveCarrier && !string.IsNullOrWhiteSpace(reservationOwnerId))
        {
            if (primaryShip.ID >= 0)
                Core.SolarSdk.Fleet.ReserveSpacecraft(primaryShip.ID, reservationOwnerId, context, dispatchId, cycle.A?.id ?? -1, cycle.B?.id ?? -1);
            else
                Core.SolarSdk.Fleet.TrackSyntheticCarrier(dispatchId, reservationOwnerId, primaryShip, context, cycle.A?.id ?? -1, cycle.B?.id ?? -1);
        }

        _log?.Verbose("sdk.cycles", $"cycle-add-register requestId={dispatchId} owner={ownerTag ?? "none"} context={context ?? "none"} route=\"{routeSummary}\" ship={primaryShip.ID}");
        return new SdkCycleCreateResult
        {
            Success = true,
            DispatchId = dispatchId,
            Cycle = cycle
        };
    }

    /// <summary>
    /// Hands an already-created stock cycle to <see cref="SpaceCraftCyclicalMissionController"/>
    /// while keeping SDK dispatch state in sync.
    /// </summary>
    /// <remarks>
    /// Mods still own route selection and <see cref="CycleMissionsData"/> construction. This
    /// wrapper owns the brittle controller setup, dispatch/carrier registration, planner-start
    /// marking, callback registration, not-started detection, and failure diagnostics around the
    /// stock call.
    /// </remarks>
    public SdkCycleHandoffResult HandOffToStockPlanner(
        Spacecraft spacecraft,
        CycleMissionsData cycle,
        string context,
        Action<SdkCycleHandoffCallbackContext> afterPlanned = null,
        Action<SdkCycleHandoffFailureContext> onNotStarted = null)
    {
        var result = new SdkCycleHandoffResult
        {
            DispatchId = FindDispatchId(cycle),
            Context = context,
            SpacecraftId = spacecraft?.ID ?? -1,
            CycleName = cycle?.customNameFromPlanMission,
            SourceName = cycle?.A?.ObjectName,
            TargetName = cycle?.B?.ObjectName
        };

        if (spacecraft == null || cycle == null)
        {
            result.FailureCode = "invalid-arguments";
            result.FailureReason = "Spacecraft or cycle was null.";
            _log?.Warning("sdk.cycles", $"handoff-failed requestId={result.DispatchId ?? "none"} context={context ?? "none"} reason=invalid-arguments");
            if (!string.IsNullOrEmpty(result.DispatchId))
                MarkCodeJobFailed(result.DispatchId, result.FailureCode, context);
            onNotStarted?.Invoke(new SdkCycleHandoffFailureContext(result, null, cycle, result.FailureCode, result.FailureReason));
            return result;
        }

        if (!string.IsNullOrEmpty(result.DispatchId))
        {
            RegisterCarrier(result.DispatchId, spacecraft, context ?? "handoff");
            MarkCodeJobStarted(result.DispatchId, context ?? "handoff");
        }

        try
        {
            var controller = spacecraft.gameObject.GetComponent<SpaceCraftCyclicalMissionController>()
                ?? spacecraft.gameObject.AddComponent<SpaceCraftCyclicalMissionController>();
            result.Controller = controller;

            controller.CycleMissionPlanFlyWas = false;
            controller.SetSC(spacecraft);
            _log?.Verbose("sdk.cycles", $"handoff-start requestId={result.DispatchId ?? "none"} context={context ?? "none"} route=\"{result.SourceName ?? "null"}->{result.TargetName ?? "null"}\" ship={spacecraft.ID}");

            controller.TryPlanCycleMission(null, parameter =>
            {
                if (!string.IsNullOrEmpty(result.DispatchId))
                {
                    RegisterMissionParameter(result.DispatchId, parameter, context ?? "handoff-callback");
                    MarkCodeJobCompleted(result.DispatchId, context ?? "handoff-callback");
                }

                result.CallbackObserved = true;
                result.Parameter = parameter;
                afterPlanned?.Invoke(new SdkCycleHandoffCallbackContext(result, controller, cycle, spacecraft, parameter));
            });

            result.StockCallReturned = true;
            result.Started = cycle.wasSetPMParameterForCodeJobSystem || controller.CycleMissionPlanFlyWas || result.CallbackObserved;
            if (!result.Started)
            {
                result.FailureCode = "planner-not-started";
                result.FailureReason = "Stock cycle planner returned without setting planner state or creating a callback.";
                _log?.Warning("sdk.cycles", $"handoff-not-started requestId={result.DispatchId ?? "none"} context={context ?? "none"} route=\"{result.SourceName ?? "null"}->{result.TargetName ?? "null"}\" ship={spacecraft.ID} phase={spacecraft.CurrentPhase} ctrlCMD={controller.CycleMissionsData != null} ctrlPlanFly={controller.CycleMissionPlanFlyWas} cmdWasSet={cycle.wasSetPMParameterForCodeJobSystem}");
                if (!string.IsNullOrEmpty(result.DispatchId))
                    MarkCodeJobFailed(result.DispatchId, result.FailureCode, context);
                onNotStarted?.Invoke(new SdkCycleHandoffFailureContext(result, controller, cycle, result.FailureCode, result.FailureReason));
            }

            return result;
        }
        catch (Exception ex)
        {
            result.FailureCode = "planner-exception";
            result.FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            _log?.Error("sdk.cycles", $"handoff-exception requestId={result.DispatchId ?? "none"} context={context ?? "none"} route=\"{result.SourceName ?? "null"}->{result.TargetName ?? "null"}\" ship={spacecraft.ID} error={ex}");
            if (!string.IsNullOrEmpty(result.DispatchId))
                MarkCodeJobFailed(result.DispatchId, result.FailureCode, context);
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("cycle-handoff-exception", result.DispatchId ?? context ?? "unknown");
            onNotStarted?.Invoke(new SdkCycleHandoffFailureContext(result, null, cycle, result.FailureCode, result.FailureReason));
            return result;
        }
    }

    /// <summary>
    /// Called by the SDK Harmony patch before stock attempts to plan a cycle mission.
    /// </summary>
    public bool ShouldSuppressTryPlanCycleMission(SpaceCraftCyclicalMissionController controller)
    {
        var cycle = controller?.CycleMissionsData;
        var context = new SdkCycleReplanContext
        {
            Controller = controller,
            Cycle = cycle,
            DispatchId = FindDispatchId(cycle),
            CycleName = cycle?.customNameFromPlanMission,
            PlanFlyWas = controller?.CycleMissionPlanFlyWas ?? false
        };

        var subscriberCount = CheckCycleReplan?.GetInvocationList().Length ?? 0;
        _log?.Verbose("sdk.cycles", $"try-plan-before requestId={context.DispatchId ?? "none"} name=\"{context.CycleName ?? "null"}\" planFlyWas={context.PlanFlyWas} subscribers={subscriberCount}");
        if (CheckCycleReplan == null)
            return false;

        foreach (Func<SdkCycleReplanContext, bool?> subscriber in CheckCycleReplan.GetInvocationList())
        {
            try
            {
                var decision = subscriber(context);
                if (decision == true)
                {
                    context.SuppressPlanning = true;
                    _log?.Verbose("sdk.cycles", $"try-plan-suppressed requestId={context.DispatchId ?? "none"} name=\"{context.CycleName ?? "null"}\" reason={context.Reason ?? "subscriber"}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Error("sdk.cycles", $"try-plan-subscriber-error requestId={context.DispatchId ?? "none"} error={ex}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("cycle-replan-handler-exception", context.DispatchId ?? context.CycleName ?? "unknown");
            }
        }

        return false;
    }

    /// <summary>
    /// Called by the SDK Harmony patch before stock displays a cycle-planning notification.
    /// </summary>
    public bool ShouldSuppressCycleNotification(SpaceCraftCyclicalMissionController controller, ObjectInfo start, PMMissionParameter parameter)
    {
        var cycle = controller?.CycleMissionsData;
        var context = new SdkCycleNotificationContext
        {
            Controller = controller,
            Cycle = cycle,
            Start = start,
            MissionParameter = parameter,
            DispatchId = FindDispatchId(cycle) ?? FindDispatchId(parameter),
            CycleName = cycle?.customNameFromPlanMission,
            SourceName = cycle?.A?.ObjectName,
            TargetName = cycle?.B?.ObjectName
        };

        var subscriberCount = CyclePlanNotification?.GetInvocationList().Length ?? 0;
        _log?.Verbose("sdk.cycles", $"notification-before requestId={context.DispatchId ?? "none"} route=\"{context.SourceName ?? "null"}->{context.TargetName ?? "null"}\" name=\"{context.CycleName ?? "null"}\" subscribers={subscriberCount}");
        if (CyclePlanNotification == null)
            return false;

        foreach (Action<SdkCycleNotificationContext> subscriber in CyclePlanNotification.GetInvocationList())
        {
            try
            {
                subscriber(context);
            }
            catch (Exception ex)
            {
                _log?.Error("sdk.cycles", $"notification-subscriber-error requestId={context.DispatchId ?? "none"} error={ex}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("cycle-notification-handler-exception", context.DispatchId ?? context.CycleName ?? "unknown");
            }
        }

        if (!string.IsNullOrEmpty(context.FailureReason) && !string.IsNullOrEmpty(context.DispatchId))
            MarkCodeJobFailed(context.DispatchId, context.FailureReason, context.Context ?? "cycle-notification");

        _log?.Verbose("sdk.cycles", $"notification-after requestId={context.DispatchId ?? "none"} suppress={context.SuppressNotification} reason={context.FailureReason ?? "none"}");
        return context.SuppressNotification;
    }

    /// <summary>
    /// Finds a dispatch ID for a previously registered stock cycle mission.
    /// </summary>
    public string FindDispatchId(CycleMissionsData cycle)
    {
        if (cycle != null && _dispatchByCycle.TryGetValue(cycle, out var id))
            return id;
        return null;
    }

    /// <summary>
    /// Finds a dispatch ID for a stock mission parameter using explicit parameter mappings,
    /// carrier instance identity, real spacecraft ID, then active stock cycle lookup.
    /// </summary>
    public string FindDispatchId(PMMissionParameter parameter)
    {
        if (parameter == null)
            return null;

        if (_dispatchByMissionParameter.TryGetValue(parameter, out var parameterId))
            return parameterId;

        if (parameter.SC is Spacecraft sc)
        {
            if (_dispatchByCarrierInstanceId.TryGetValue(sc.GetInstanceID(), out var instanceId))
                return instanceId;
            if (sc.ID >= 0 && _dispatchByShipId.TryGetValue(sc.ID, out var shipId))
                return shipId;

            var activeCycle = MonoBehaviourSingleton<CycleMissionManager>.Instance?.GetCycleMission(sc);
            var cycleId = FindDispatchId(activeCycle);
            if (!string.IsNullOrEmpty(cycleId))
                return cycleId;
        }
        return null;
    }

    /// <summary>
    /// Finds a dispatch ID for created stock mission info by mission ID or associated spacecraft.
    /// </summary>
    public string FindDispatchId(MissionInfo missionInfo)
    {
        if (missionInfo != null && _dispatchByMissionId.TryGetValue(missionInfo.id, out var id))
            return id;
        if (missionInfo?.spacecraftInfo2 is Spacecraft sc)
        {
            if (_dispatchByCarrierInstanceId.TryGetValue(sc.GetInstanceID(), out var instanceId))
                return instanceId;
            if (sc.ID >= 0 && _dispatchByShipId.TryGetValue(sc.ID, out var shipId))
                return shipId;
        }
        return null;
    }

    /// <summary>
    /// Removes the cycle-to-dispatch mapping and marks the dispatch as unregistered.
    /// </summary>
    public void UnregisterCycle(CycleMissionsData cycle, string reason = null)
    {
        var dispatchId = FindDispatchId(cycle);
        if (dispatchId != null)
            Mark(dispatchId, "unregistered", reason, null);
        if (cycle != null)
            _dispatchByCycle.Remove(cycle);
    }

    /// <summary>
    /// Logs a concise reconciliation summary for tagged cycle missions owned by a mod.
    /// </summary>
    public void LogReconcile(string ownerTag, Company company)
    {
        var cycles = FindTagged(company, ownerTag);
        _log?.Verbose("sdk.cycles", $"reconcile tag={ownerTag} company={company?.Definition?.ID ?? "null"} active={cycles.Count}");
    }

    /// <summary>
    /// Returns a snapshot copy of current dispatch trackers for diagnostics JSON or debug UI.
    /// </summary>
    public List<SdkCycleTracker> GetTrackersSnapshot()
    {
        return _trackersByDispatchId.Values.ToList();
    }

    /// <summary>
    /// Clears all cycle correlation state for a mod owner.
    /// </summary>
    public void ClearOwner(string ownerTag)
    {
        if (string.IsNullOrWhiteSpace(ownerTag))
            return;

        var dispatchIds = new HashSet<string>(_trackersByDispatchId
            .Where(p => string.Equals(p.Value.OwnerTag, ownerTag, System.StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key));
        if (dispatchIds.Count == 0)
            return;

        foreach (var dispatchId in dispatchIds)
            _trackersByDispatchId.Remove(dispatchId);
        foreach (var cycle in _dispatchByCycle.Where(p => dispatchIds.Contains(p.Value)).Select(p => p.Key).ToList())
            _dispatchByCycle.Remove(cycle);
        foreach (var parameter in _dispatchByMissionParameter.Where(p => dispatchIds.Contains(p.Value)).Select(p => p.Key).ToList())
            _dispatchByMissionParameter.Remove(parameter);
        foreach (var shipId in _dispatchByShipId.Where(p => dispatchIds.Contains(p.Value)).Select(p => p.Key).ToList())
            _dispatchByShipId.Remove(shipId);
        foreach (var instanceId in _dispatchByCarrierInstanceId.Where(p => dispatchIds.Contains(p.Value)).Select(p => p.Key).ToList())
            _dispatchByCarrierInstanceId.Remove(instanceId);
        foreach (var missionId in _dispatchByMissionId.Where(p => dispatchIds.Contains(p.Value)).Select(p => p.Key).ToList())
            _dispatchByMissionId.Remove(missionId);

        _log?.Verbose("sdk.cycles", $"clear-owner owner={ownerTag} dispatches={dispatchIds.Count}");
    }

    private void Mark(string dispatchId, string phase, string context, string reason)
    {
        if (string.IsNullOrWhiteSpace(dispatchId))
            return;

        if (_trackersByDispatchId.TryGetValue(dispatchId, out var tracker))
        {
            var samePhase = string.Equals(tracker.LastPhase, phase, System.StringComparison.Ordinal)
                && string.Equals(tracker.LastContext, context, System.StringComparison.Ordinal)
                && string.Equals(tracker.LastReason, reason, System.StringComparison.Ordinal);
            tracker.LastPhase = phase;
            tracker.LastContext = context;
            tracker.LastReason = reason;
            tracker.UpdatedAtUtc = System.DateTime.UtcNow;
            tracker.PhaseUpdateCount++;
            if (samePhase)
            {
                _log?.VerboseThrottled("sdk.cycles", $"phase-repeat-{dispatchId}-{phase}", $"phase-repeat requestId={dispatchId} phase={phase} context={context ?? "none"} reason={reason ?? "none"}", 10.0);
                return;
            }
        }

        _log?.Verbose("sdk.cycles", $"phase requestId={dispatchId} phase={phase} context={context ?? "none"} reason={reason ?? "none"}");
        if (phase != null && phase.IndexOf("failed", System.StringComparison.OrdinalIgnoreCase) >= 0)
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce($"cycle-{phase}", FailureSnapshotKey(dispatchId, phase, reason));
    }

    private string FailureSnapshotKey(string dispatchId, string phase, string reason)
    {
        if (!_trackersByDispatchId.TryGetValue(dispatchId, out var tracker) || tracker == null)
            return dispatchId;

        var route = $"{tracker.SourceId}->{tracker.TargetId}";
        var cycle = string.IsNullOrWhiteSpace(tracker.CycleName) ? "unnamed" : tracker.CycleName;
        var failure = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        return $"{phase}:{tracker.OwnerTag ?? "none"}:{route}:{cycle}:{failure}";
    }
}

/// <summary>
/// Small request DTO for future SDK-owned cycle creation.
/// </summary>
public sealed class SdkCycleRequest
{
    /// <summary>Existing or desired dispatch ID for the cycle request.</summary>
    public string DispatchId { get; set; }
    /// <summary>Mod owner tag, such as <c>logi</c>.</summary>
    public string OwnerTag { get; set; }
    /// <summary>Human-readable route summary for logs and snapshots.</summary>
    public string RouteSummary { get; set; }
}

/// <summary>
/// SDK description of a stock cyclical mission before constructing <see cref="CycleMissionsData"/>.
/// </summary>
public sealed class SdkCycleDraft
{
    /// <summary>Custom stock cycle name.</summary>
    public string CustomName { get; set; }
    /// <summary>Cycle endpoint A.</summary>
    public ObjectInfo Source { get; set; }
    /// <summary>Cycle endpoint B.</summary>
    public ObjectInfo Target { get; set; }
    /// <summary>Owning company.</summary>
    public Company Company { get; set; }
    /// <summary>Cargo load mode at endpoint A.</summary>
    public ECargoStart CargoStart { get; set; } = ECargoStart.FlyWithWhatIsAvailable;
    /// <summary>Cargo load mode at endpoint B.</summary>
    public ECargoStart CargoEnd { get; set; } = ECargoStart.FlyWithWhatIsAvailable;
    /// <summary>Cargo manifest for endpoint A.</summary>
    public CargoAll CargoAllStart { get; set; }
    /// <summary>Cargo manifest for endpoint B.</summary>
    public CargoAll CargoAllEnd { get; set; }
    /// <summary>Launch vehicle type at endpoint A.</summary>
    public LaunchVehicleType LaunchVehicleTypeA { get; set; }
    /// <summary>Launch vehicle type at endpoint B.</summary>
    public LaunchVehicleType LaunchVehicleTypeB { get; set; }
    /// <summary>Stock transfer type.</summary>
    public ETransferType TransferType { get; set; } = ETransferType.Optimal;
    /// <summary>Stock cycle end mode.</summary>
    public EEnds Ends { get; set; } = EEnds.ThisManyTimes;
    /// <summary>End date for until-mode cycles.</summary>
    public DateTime EndsObjectUntil { get; set; }
    /// <summary>Count for this-many-times cycles.</summary>
    public int EndsObjectThisManyTimes { get; set; } = 1;
    /// <summary>Done resource counts for endpoint A.</summary>
    public EndsResourceCountData EndsResourceCountDataA { get; set; }
    /// <summary>Maximum resource counts for endpoint A.</summary>
    public EndsResourceCountData EndsResourceCountMaxA { get; set; }
    /// <summary>Done resource counts for endpoint B.</summary>
    public EndsResourceCountData EndsResourceCountDataB { get; set; }
    /// <summary>Maximum resource counts for endpoint B.</summary>
    public EndsResourceCountData EndsResourceCountMaxB { get; set; }
    /// <summary>Spacecraft list for the stock cycle.</summary>
    public List<ISpacecraftInfo> Spacecraft { get; set; }
}

/// <summary>
/// Result DTO for future SDK-owned cycle creation.
/// </summary>
public sealed class SdkCycleCreateResult
{
    /// <summary>True when the cycle was created or registered successfully.</summary>
    public bool Success { get; set; }
    /// <summary>Dispatch ID for the attempted cycle.</summary>
    public string DispatchId { get; set; }
    /// <summary>Created or registered stock cycle, when successful.</summary>
    public CycleMissionsData Cycle { get; set; }
    /// <summary>Machine-readable failure code, when creation failed.</summary>
    public string FailureCode { get; set; }
    /// <summary>Human-readable failure reason, when creation failed.</summary>
    public string FailureReason { get; set; }
}

/// <summary>
/// Result from handing an existing stock cycle to the stock cyclical mission planner.
/// </summary>
public sealed class SdkCycleHandoffResult
{
    /// <summary>Dispatch ID associated with the cycle, when registered.</summary>
    public string DispatchId { get; set; }
    /// <summary>Caller-supplied context label.</summary>
    public string Context { get; set; }
    /// <summary>True when stock accepted or began the planning attempt.</summary>
    public bool Started { get; set; }
    /// <summary>True when the stock method returned without throwing.</summary>
    public bool StockCallReturned { get; set; }
    /// <summary>True when the stock code-job callback path ran.</summary>
    public bool CallbackObserved { get; set; }
    /// <summary>Machine-readable failure code when handoff did not start.</summary>
    public string FailureCode { get; set; }
    /// <summary>Human-readable failure reason when handoff did not start.</summary>
    public string FailureReason { get; set; }
    /// <summary>Primary real or synthetic spacecraft ID.</summary>
    public int SpacecraftId { get; set; }
    /// <summary>Stock cycle name.</summary>
    public string CycleName { get; set; }
    /// <summary>Stock source object name.</summary>
    public string SourceName { get; set; }
    /// <summary>Stock target object name.</summary>
    public string TargetName { get; set; }
    /// <summary>Stock controller used for the attempt.</summary>
    public SpaceCraftCyclicalMissionController Controller { get; set; }
    /// <summary>Mission parameter observed in the code-job callback, when any.</summary>
    public PMMissionParameter Parameter { get; set; }
}

/// <summary>
/// Callback context emitted after stock exposes a code-job mission parameter for a cycle.
/// </summary>
public sealed class SdkCycleHandoffCallbackContext
{
    public SdkCycleHandoffCallbackContext(SdkCycleHandoffResult result, SpaceCraftCyclicalMissionController controller, CycleMissionsData cycle, Spacecraft spacecraft, PMMissionParameter parameter)
    {
        Result = result;
        Controller = controller;
        Cycle = cycle;
        Spacecraft = spacecraft;
        MissionParameter = parameter;
    }

    /// <summary>Mutable handoff result.</summary>
    public SdkCycleHandoffResult Result { get; }
    /// <summary>Stock controller used for the attempt.</summary>
    public SpaceCraftCyclicalMissionController Controller { get; }
    /// <summary>Stock cycle being planned.</summary>
    public CycleMissionsData Cycle { get; }
    /// <summary>Carrier spacecraft used by stock.</summary>
    public Spacecraft Spacecraft { get; }
    /// <summary>Mission parameter exposed by stock.</summary>
    public PMMissionParameter MissionParameter { get; }
}

/// <summary>
/// Failure context emitted when the stock cycle planner returns without starting or throws.
/// </summary>
public sealed class SdkCycleHandoffFailureContext
{
    public SdkCycleHandoffFailureContext(SdkCycleHandoffResult result, SpaceCraftCyclicalMissionController controller, CycleMissionsData cycle, string failureCode, string failureReason)
    {
        Result = result;
        Controller = controller;
        Cycle = cycle;
        FailureCode = failureCode;
        FailureReason = failureReason;
    }

    /// <summary>Mutable handoff result.</summary>
    public SdkCycleHandoffResult Result { get; }
    /// <summary>Stock controller used for the attempt, when created.</summary>
    public SpaceCraftCyclicalMissionController Controller { get; }
    /// <summary>Stock cycle that failed to start.</summary>
    public CycleMissionsData Cycle { get; }
    /// <summary>Machine-readable failure code.</summary>
    public string FailureCode { get; }
    /// <summary>Human-readable failure reason.</summary>
    public string FailureReason { get; }
}

/// <summary>
/// Context passed to consumers before stock attempts to plan a cycle mission.
/// </summary>
public sealed class SdkCycleReplanContext
{
    /// <summary>Stock controller about to plan.</summary>
    public SpaceCraftCyclicalMissionController Controller { get; set; }
    /// <summary>Stock cycle about to plan.</summary>
    public CycleMissionsData Cycle { get; set; }
    /// <summary>Known SDK dispatch ID.</summary>
    public string DispatchId { get; set; }
    /// <summary>Stock cycle name.</summary>
    public string CycleName { get; set; }
    /// <summary>Current value of stock CycleMissionPlanFlyWas.</summary>
    public bool PlanFlyWas { get; set; }
    /// <summary>Set by subscribers when planning should be suppressed.</summary>
    public bool SuppressPlanning { get; set; }
    /// <summary>Optional diagnostic reason for suppression.</summary>
    public string Reason { get; set; }
}

/// <summary>
/// Context passed to consumers before stock displays a cycle-planning failure notification.
/// </summary>
public sealed class SdkCycleNotificationContext
{
    /// <summary>Stock controller raising the notification.</summary>
    public SpaceCraftCyclicalMissionController Controller { get; set; }
    /// <summary>Stock cycle associated with the notification.</summary>
    public CycleMissionsData Cycle { get; set; }
    /// <summary>Stock start object passed to ShowNotification.</summary>
    public ObjectInfo Start { get; set; }
    /// <summary>Mission parameter being validated, when stock provided one.</summary>
    public PMMissionParameter MissionParameter { get; set; }
    /// <summary>Known SDK dispatch ID.</summary>
    public string DispatchId { get; set; }
    /// <summary>Stock cycle name.</summary>
    public string CycleName { get; set; }
    /// <summary>Stock source object name.</summary>
    public string SourceName { get; set; }
    /// <summary>Stock target object name.</summary>
    public string TargetName { get; set; }
    /// <summary>Set true to suppress the stock notification.</summary>
    public bool SuppressNotification { get; set; }
    /// <summary>Optional translated failure reason to record against the dispatch.</summary>
    public string FailureReason { get; set; }
    /// <summary>Optional caller context for logs and dispatch phase updates.</summary>
    public string Context { get; set; }
}

/// <summary>
/// Runtime diagnostics record for one SDK dispatch ID.
/// </summary>
public sealed class SdkCycleTracker
{
    /// <summary>SDK dispatch ID.</summary>
    public string DispatchId { get; set; }
    /// <summary>Mod owner tag.</summary>
    public string OwnerTag { get; set; }
    /// <summary>Stock cycle custom name, when available.</summary>
    public string CycleName { get; set; }
    /// <summary>Human-readable route summary.</summary>
    public string RouteSummary { get; set; }
    /// <summary>Positive real spacecraft ID, or -1 when no real primary ship is known.</summary>
    public int PrimaryShipId { get; set; }
    /// <summary>Primary spacecraft display name, when known.</summary>
    public string PrimaryShipName { get; set; }
    /// <summary>Stock source object ID.</summary>
    public int SourceId { get; set; }
    /// <summary>Stock source object name.</summary>
    public string SourceName { get; set; }
    /// <summary>Stock target object ID.</summary>
    public int TargetId { get; set; }
    /// <summary>Stock target object name.</summary>
    public string TargetName { get; set; }
    /// <summary>Created stock mission info ID, when available.</summary>
    public int MissionInfoId { get; set; }
    /// <summary>Number of times mission info has been registered for this dispatch.</summary>
    public int MissionInfoRegistrationCount { get; set; }
    /// <summary>Last recorded SDK dispatch phase.</summary>
    public string LastPhase { get; set; }
    /// <summary>Last code context associated with the phase.</summary>
    public string LastContext { get; set; }
    /// <summary>Last failure or cleanup reason associated with the phase.</summary>
    public string LastReason { get; set; }
    /// <summary>Number of phase updates recorded for this dispatch.</summary>
    public int PhaseUpdateCount { get; set; }
    /// <summary>UTC time when the tracker was created.</summary>
    public System.DateTime CreatedAtUtc { get; set; }
    /// <summary>UTC time when the tracker was last updated.</summary>
    public System.DateTime UpdatedAtUtc { get; set; }
}
