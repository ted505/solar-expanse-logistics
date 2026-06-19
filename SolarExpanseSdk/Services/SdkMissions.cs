using System;
using System.Collections.Generic;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using Manager;

namespace SolarExpanseSdk.Services;

/// <summary>
/// High-level helpers for creating, cloning, and validating stock mission parameters.
/// This service does not replace the stock planner; it builds safer SDK-shaped inputs
/// around <see cref="PMMissionParameter"/> and translates stock validation failures.
/// </summary>
public sealed class SdkMissions
{
    private SdkLogging _log;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Creates an empty mission draft for a consumer mod. The returned draft owns a new
    /// empty <see cref="CargoAll"/> created through stock <see cref="CargoAll.CreateCargoEmpty"/>.
    /// </summary>
    public SdkMissionDraft CreateDraft(string ownerId)
    {
        return new SdkMissionDraft
        {
            OwnerId = string.IsNullOrWhiteSpace(ownerId) ? "mod" : ownerId,
            CargoAll = CargoAll.CreateCargoEmpty()
        };
    }

    /// <summary>
    /// Copies the useful public state from an existing stock mission parameter into an SDK draft.
    /// Cargo is cloned so draft cargo edits do not mutate the original parameter.
    /// </summary>
    public SdkMissionDraft FromParameter(string ownerId, PMMissionParameter parameter)
    {
        if (parameter == null)
            return CreateDraft(ownerId);

        return new SdkMissionDraft
        {
            OwnerId = string.IsNullOrWhiteSpace(ownerId) ? "mod" : ownerId,
            DispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(parameter),
            MissionName = parameter.MissionName,
            Company = parameter.FlyCompany,
            Start = parameter.Start,
            Target = parameter.Target,
            Spacecraft = parameter.SC,
            SpacecraftList = parameter.SCList == null ? null : new List<ISpacecraftInfo>(parameter.SCList),
            LaunchVehicle = parameter.LV,
            LaunchVehicleList = parameter.LVList == null ? null : new List<ILaunchVehicleInfo>(parameter.LVList),
            CargoAll = Core.SolarSdk.MissionLoadout.CloneCargo(parameter.CargoAll),
            MissionCreator = parameter.MissionCreator,
            CostType = parameter.CostType,
            MissionId = parameter.MissionID,
            DepartureTime = parameter.DepartureTimeDate,
            ArrivalTime = parameter.Arrival,
            DeltaV1 = parameter.DV11,
            DeltaV2 = parameter.DV22,
            AllFuelNeed = parameter.AllFuelNeed,
            OptimalFuelNeed = parameter.OptimalFuelNeed,
            LeftOverFuel = parameter.LeftOverFuel,
            MinFuelCost = parameter.MINFuelCost,
            LoadingFromSave = parameter.LoadingFromSave,
            LoadingFromSaveAndLaunch = parameter.LoadingFromSaveAndLaunch,
            ForCyclicalMission = parameter.ForCyclicalMission,
            Fast = parameter.Fast
        };
    }

    /// <summary>
    /// Creates a new stock parameter containing the same SDK-visible data as the supplied parameter.
    /// This is a convenience wrapper over <see cref="FromParameter"/> and <see cref="ToMissionParameter"/>.
    /// </summary>
    public PMMissionParameter CloneParameter(PMMissionParameter parameter)
    {
        return ToMissionParameter(FromParameter("clone", parameter));
    }

    /// <summary>
    /// Converts an SDK draft into a new stock <see cref="PMMissionParameter"/> using stock setter methods.
    /// If the draft has a dispatch ID, the parameter and carrier are registered with the cycle tracker.
    /// </summary>
    /// <remarks>
    /// This mutates the newly-created parameter, not the draft. It may reuse the draft's cargo object,
    /// so clone cargo first if the caller needs copy-on-write behavior.
    /// </remarks>
    public PMMissionParameter ToMissionParameter(SdkMissionDraft draft)
    {
        if (draft == null)
            return null;

        var parameter = new PMMissionParameter();
        if (draft.Company != null)
            parameter.SetCompany(draft.Company);
        if (draft.Start != null || draft.Target != null)
            parameter.SetTabDestination(draft.Start, draft.Target);
        if (draft.SpacecraftList != null)
            parameter.SetTabSC(draft.SpacecraftList, Math.Max(1, draft.SpacecraftList.Count));
        else if (draft.Spacecraft != null)
            parameter.SetTabSC(draft.Spacecraft);
        if (draft.LaunchVehicleList != null)
            parameter.SetTabLV(draft.LaunchVehicleList, Math.Max(1, draft.LaunchVehicleList.Count));
        else if (draft.LaunchVehicle != null)
            parameter.SetTabLV(draft.LaunchVehicle);

        parameter.SetTabCargo(draft.CargoAll ?? CargoAll.CreateCargoEmpty());
        parameter.SetMissionOrigin(draft.MissionCreator);
        parameter.SetCostType(draft.CostType);
        if (draft.MissionId.HasValue)
            parameter.SetMissionID(draft.MissionId.Value);
        if (!string.IsNullOrEmpty(draft.MissionName))
            parameter.ChangeMissionName(draft.MissionName, _manualChangeName: true);
        if (draft.DepartureTime.HasValue && draft.ArrivalTime.HasValue)
            parameter.SetTabDateFromPorkchope(draft.DepartureTime.Value, draft.ArrivalTime.Value);
        if (draft.DeltaV1.HasValue && draft.DeltaV2.HasValue)
            parameter.SetDeltaV(draft.DeltaV1.Value, draft.DeltaV2.Value);
        if (draft.AllFuelNeed.HasValue || draft.MinFuelCost.HasValue || draft.LeftOverFuel.HasValue || draft.FlightCost.HasValue || draft.LaunchVehicleFuelNeed.HasValue)
        {
            parameter.SetFuelNeed(
                draft.AllFuelNeed ?? parameter.AllFuelNeed,
                draft.MinFuelCost ?? parameter.MINFuelCost,
                draft.LeftOverFuel ?? parameter.LeftOverFuel,
                draft.FlightCost ?? 0.0,
                0.0,
                draft.LaunchVehicleFuelNeed ?? 0.0);
        }
        if (draft.OptimalFuelNeed.HasValue)
            parameter.SetOptimalFuelNeed((float)draft.OptimalFuelNeed.Value);

        if (!string.IsNullOrEmpty(draft.DispatchId))
        {
            Core.SolarSdk.CyclicalMissions.RegisterMissionParameter(draft.DispatchId, parameter, "mission-draft");
            Core.SolarSdk.CyclicalMissions.RegisterCarrier(draft.DispatchId, draft.Spacecraft as Spacecraft, "mission-draft");
        }

        return parameter;
    }

    /// <summary>
    /// Validates a draft with SDK structural checks and, by default, stock planner validation.
    /// Stock validation is skipped when blocking structural failures are found.
    /// </summary>
    public SdkMissionValidationResult Validate(SdkMissionDraft draft, SdkMissionValidationOptions options = null)
    {
        var result = new SdkMissionValidationResult { Context = SdkMissionContext.FromDraft(draft) };
        if (draft == null)
        {
            result.Add(SdkMissionFailureKind.InvalidDraft, "Mission draft is null.", retryable: false, playerAction: false);
            return result;
        }

        ValidateStructure(draft, result, options ?? new SdkMissionValidationOptions());
        if ((options?.RunStockValidation ?? true) && !result.HasBlockingStructuralFailure)
            Validate(ToMissionParameter(draft), options, result);

        return result;
    }

    /// <summary>
    /// Validates an existing stock mission parameter by calling stock schedule and planner checks.
    /// </summary>
    /// <remarks>
    /// Use this when the relevant planner/game state is available. Stock validation reads
    /// substantial mission-planning state and should not be treated as a pure data-only check.
    /// </remarks>
    public SdkMissionValidationResult Validate(PMMissionParameter parameter, SdkMissionValidationOptions options = null)
    {
        return Validate(parameter, options, new SdkMissionValidationResult { Context = SdkMissionContext.FromParameter(parameter) });
    }

    /// <summary>
    /// Converts a stock planner result flag set into SDK validation issues without rerunning validation.
    /// </summary>
    public IReadOnlyList<SdkMissionValidationIssue> Explain(PMMissionParameter.EPlanMissionResult result)
    {
        var validation = new SdkMissionValidationResult();
        AddStockResultIssues(result, validation);
        return validation.Issues;
    }

    /// <summary>
    /// Maps stock <see cref="PMMissionParameter.EPlanMissionResult"/> flags to a primary SDK failure kind.
    /// </summary>
    public SdkMissionFailureKind TranslateFailureKind(PMMissionParameter.EPlanMissionResult result)
    {
        if (result == PMMissionParameter.EPlanMissionResult.AllOk)
            return SdkMissionFailureKind.None;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongSC))
            return SdkMissionFailureKind.InvalidSpacecraft;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV) || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongCheckLvOk))
            return SdkMissionFailureKind.InvalidLaunchVehicle;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.NoFuelCantBuy) || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongRemoveFuel))
            return SdkMissionFailureKind.InsufficientFuel;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongThrust))
            return SdkMissionFailureKind.InsufficientThrust;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLifeSupport))
            return SdkMissionFailureKind.InsufficientLifeSupport;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongResourcesCargoStartHaveResource))
            return SdkMissionFailureKind.InsufficientCargoResource;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongResourcesCargoLoadLimit))
            return SdkMissionFailureKind.CargoOverLimit;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongMaxCapacityFuelOk) || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongScNoLVFuelOk))
            return SdkMissionFailureKind.FuelCapacity;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongTransferLambertOK))
            return SdkMissionFailureKind.InvalidTransfer;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongSolarDistanceOk))
            return SdkMissionFailureKind.InvalidSolarDistance;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongAsteroidImpactEndGameOK))
            return SdkMissionFailureKind.InvalidAsteroidImpact;
        return SdkMissionFailureKind.StockRejected;
    }

    /// <summary>
    /// Returns true when any validation issue was marked as worth retrying after state changes.
    /// </summary>
    public bool IsRetryable(SdkMissionValidationResult result)
    {
        return result != null && result.Issues.Exists(i => i.Retryable);
    }

    /// <summary>
    /// Returns true when any validation issue likely needs player or mod policy intervention.
    /// </summary>
    public bool RequiresPlayerAction(SdkMissionValidationResult result)
    {
        return result != null && result.Issues.Exists(i => i.RequiresPlayerAction);
    }

    private SdkMissionValidationResult Validate(PMMissionParameter parameter, SdkMissionValidationOptions options, SdkMissionValidationResult result)
    {
        options ??= new SdkMissionValidationOptions();
        result.Context ??= SdkMissionContext.FromParameter(parameter);
        if (parameter == null)
        {
            result.Add(SdkMissionFailureKind.InvalidParameter, "Mission parameter is null.", retryable: false, playerAction: false);
            return result;
        }

        try
        {
            var scheduleOk = parameter.CheckScheduleFly();
            result.ScheduleOk = scheduleOk;
            if (!scheduleOk)
                result.Add(SdkMissionFailureKind.ScheduleRejected, "Stock CheckScheduleFly returned false.", retryable: true, playerAction: true);
        }
        catch (Exception ex)
        {
            result.Add(SdkMissionFailureKind.StockException, $"CheckScheduleFly threw {ex.GetType().Name}: {ex.Message}", retryable: true, playerAction: false);
        }

        if (options.RunStockValidation)
        {
            try
            {
                var stock = parameter.CheckCanPlanMission();
                result.StockResult = stock;
                result.StockPlanResult = stock.planMissionResult;
                AddStockResultIssues(stock.planMissionResult, result);
            }
            catch (Exception ex)
            {
                result.Add(SdkMissionFailureKind.StockException, $"CheckCanPlanMission threw {ex.GetType().Name}: {ex.Message}", retryable: true, playerAction: false);
            }
        }

        _log?.Verbose("sdk.missionPlanning", $"mission-validate valid={result.Valid} route=\"{parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}\" issues={result.Issues.Count}");
        return result;
    }

    private void ValidateStructure(SdkMissionDraft draft, SdkMissionValidationResult result, SdkMissionValidationOptions options)
    {
        if (draft.Company == null)
            result.Add(SdkMissionFailureKind.MissingCompany, "Mission company is null.", retryable: false, playerAction: true, structural: true);
        if (draft.Start == null)
            result.Add(SdkMissionFailureKind.MissingStart, "Mission start object is null.", retryable: false, playerAction: true, structural: true);
        if (draft.Target == null)
            result.Add(SdkMissionFailureKind.MissingTarget, "Mission target object is null.", retryable: false, playerAction: true, structural: true);
        if (draft.Spacecraft == null && (draft.SpacecraftList == null || draft.SpacecraftList.Count == 0))
            result.Add(SdkMissionFailureKind.MissingSpacecraft, "Mission spacecraft is null.", retryable: false, playerAction: true, structural: true);
        if (draft.CargoAll == null)
            result.Add(SdkMissionFailureKind.InvalidCargo, "Mission cargo is null.", retryable: false, playerAction: true, structural: true);
        else
            ValidateCargo(draft.CargoAll, result);

        var spacecraft = draft.Spacecraft as Spacecraft;
        if (spacecraft != null)
        {
            if (!options.AllowBusySpacecraft && spacecraft.CurrentPhase != Spacecraft.EPhase.None)
                result.Add(SdkMissionFailureKind.SpacecraftBusy, $"Spacecraft {spacecraft.ID} is busy ({spacecraft.CurrentPhase}).", retryable: true, playerAction: false);
            if (spacecraft.ID < 0 && !draft.AllowSyntheticCarrier)
                result.Add(SdkMissionFailureKind.SyntheticCarrier, $"Spacecraft {spacecraft.ID} is a synthetic carrier.", retryable: false, playerAction: false);
        }
    }

    private static void ValidateCargo(CargoAll cargo, SdkMissionValidationResult result)
    {
        if (cargo.cargoFuel == null)
            result.Add(SdkMissionFailureKind.InvalidFuelCargo, "Cargo fuel entry is null.", retryable: false, playerAction: true);
        else if (cargo.cargoFuel.cargoMass < 0.0 || cargo.cargoFuel.cargoMassPotencjal < 0.0)
            result.Add(SdkMissionFailureKind.InvalidFuelCargo, "Cargo fuel mass is negative.", retryable: false, playerAction: true);

        ValidateCargoList(cargo.listCargo, "cargo", result);
        ValidateCargoList(cargo.listCargoToOrbit, "orbit cargo", result);
        ValidateCargoList(cargo.listCargoGravityAssists, "gravity-assist cargo", result);
    }

    private static void ValidateCargoList(IEnumerable<Cargo> list, string label, SdkMissionValidationResult result)
    {
        if (list == null)
            return;

        foreach (var item in list)
        {
            if (item == null)
            {
                result.Add(SdkMissionFailureKind.InvalidCargo, $"{label} contains a null entry.", retryable: false, playerAction: true);
                continue;
            }
            if (item.cargoMass < 0.0)
                result.Add(SdkMissionFailureKind.InvalidCargo, $"{label} contains negative cargo mass.", retryable: false, playerAction: true);
            if (item.resourceTypeType == EResourceTypeType.resorces && item.resourceType == null)
                result.Add(SdkMissionFailureKind.InvalidCargo, $"{label} resource cargo has no resource definition.", retryable: false, playerAction: true);
            if (item.resourceTypeType == EResourceTypeType.modules && item.moduleData == null)
                result.Add(SdkMissionFailureKind.InvalidCargo, $"{label} module cargo has no module descriptor.", retryable: false, playerAction: true);
        }
    }

    private void AddStockResultIssues(PMMissionParameter.EPlanMissionResult planResult, SdkMissionValidationResult result)
    {
        if (planResult == PMMissionParameter.EPlanMissionResult.AllOk)
            return;

        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongSC, SdkMissionFailureKind.InvalidSpacecraft, "Stock planner rejected the spacecraft.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongLV, SdkMissionFailureKind.InvalidLaunchVehicle, "Stock planner rejected the launch vehicle.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.NoFuelCantBuy, SdkMissionFailureKind.InsufficientFuel, "Fuel is unavailable and cannot be bought.", true, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongThrust, SdkMissionFailureKind.InsufficientThrust, "Spacecraft thrust is insufficient.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongLifeSupport, SdkMissionFailureKind.InsufficientLifeSupport, "Life support is insufficient.", true, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongResourcesCargoStartHaveResource, SdkMissionFailureKind.InsufficientCargoResource, "Source lacks requested cargo resources.", true, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongResourcesCargoLoadLimit, SdkMissionFailureKind.CargoOverLimit, "Cargo exceeds load limit.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongRemoveFuel, SdkMissionFailureKind.InsufficientFuel, "Fuel removal failed.", true, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongMaxCapacityFuelOk, SdkMissionFailureKind.FuelCapacity, "Fuel requirement exceeds capacity.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongScNoLVFuelOk, SdkMissionFailureKind.MissingLaunchVehicle, "Spacecraft needs a launch vehicle for this fuel case.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongSolarDistanceOk, SdkMissionFailureKind.InvalidSolarDistance, "Solar-distance constraint failed.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongCheckLvOk, SdkMissionFailureKind.InvalidLaunchVehicle, "Launch vehicle check failed.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongAsteroidImpactEndGameOK, SdkMissionFailureKind.InvalidAsteroidImpact, "Asteroid-impact/endgame constraint failed.", false, true, result);
        AddFlag(planResult, PMMissionParameter.EPlanMissionResult.WrongTransferLambertOK, SdkMissionFailureKind.InvalidTransfer, "Lambert transfer check failed.", true, false, result);
    }

    private static void AddFlag(PMMissionParameter.EPlanMissionResult planResult, PMMissionParameter.EPlanMissionResult flag,
        SdkMissionFailureKind kind, string message, bool retryable, bool playerAction, SdkMissionValidationResult result)
    {
        if (planResult.HasFlag(flag))
            result.Add(kind, message, retryable, playerAction);
    }
}

/// <summary>
/// SDK-owned mission intent. Drafts are easy for mods to build, inspect, validate, and then
/// convert into stock <see cref="PMMissionParameter"/> objects.
/// </summary>
public sealed class SdkMissionDraft
{
    /// <summary>Stable owner string for diagnostics and future policy scoping.</summary>
    public string OwnerId { get; set; }
    /// <summary>Optional dispatch ID used to correlate cycles, parameters, mission info, and logs.</summary>
    public string DispatchId { get; set; }
    /// <summary>Desired stock mission display/name value.</summary>
    public string MissionName { get; set; }
    /// <summary>Company that will own and launch the mission.</summary>
    public Company Company { get; set; }
    /// <summary>Starting object for the mission.</summary>
    public ObjectInfo Start { get; set; }
    /// <summary>Target object for the mission.</summary>
    public ObjectInfo Target { get; set; }
    /// <summary>Primary spacecraft or synthetic carrier for the mission.</summary>
    public ISpacecraftInfo Spacecraft { get; set; }
    /// <summary>Optional multi-spacecraft stock selection.</summary>
    public List<ISpacecraftInfo> SpacecraftList { get; set; }
    /// <summary>Optional launch vehicle for surface launch cases.</summary>
    public ILaunchVehicleInfo LaunchVehicle { get; set; }
    /// <summary>Optional multi-launch-vehicle stock selection.</summary>
    public List<ILaunchVehicleInfo> LaunchVehicleList { get; set; }
    /// <summary>Stock cargo object. Some loadout helpers mutate this object in place.</summary>
    public CargoAll CargoAll { get; set; }
    /// <summary>Stock mission creator value used by mission info creation and filtering.</summary>
    public MissionInfo.EMissionCreator MissionCreator { get; set; } = MissionInfo.EMissionCreator.Manual;
    /// <summary>Stock cost type, usually optimal or fastest depending on route policy.</summary>
    public MissionInfo.ECostType CostType { get; set; } = MissionInfo.ECostType.Optimal;
    /// <summary>Optional stock mission ID to preserve during save/load flows.</summary>
    public int? MissionId { get; set; }
    /// <summary>Optional planned departure time.</summary>
    public DateTime? DepartureTime { get; set; }
    /// <summary>Optional planned arrival time.</summary>
    public DateTime? ArrivalTime { get; set; }
    /// <summary>Optional first delta-v value copied from stock planner state.</summary>
    public double? DeltaV1 { get; set; }
    /// <summary>Optional second delta-v value copied from stock planner state.</summary>
    public double? DeltaV2 { get; set; }
    /// <summary>Optional total fuel need copied to stock <see cref="PMMissionParameter.AllFuelNeed"/>.</summary>
    public double? AllFuelNeed { get; set; }
    /// <summary>Optional optimal fuel need copied to stock planner state.</summary>
    public double? OptimalFuelNeed { get; set; }
    /// <summary>Optional stock leftover fuel value.</summary>
    public double? LeftOverFuel { get; set; }
    /// <summary>Optional stock minimum fuel cost value.</summary>
    public double? MinFuelCost { get; set; }
    /// <summary>Optional mission flight cost value used when setting fuel need.</summary>
    public double? FlightCost { get; set; }
    /// <summary>Optional launch vehicle fuel need used when setting fuel need.</summary>
    public double? LaunchVehicleFuelNeed { get; set; }
    /// <summary>True when draft data came from save loading.</summary>
    public bool LoadingFromSave { get; set; }
    /// <summary>True when draft data came from save loading and should launch immediately.</summary>
    public bool LoadingFromSaveAndLaunch { get; set; }
    /// <summary>True when the mission is being planned for a stock cyclical mission.</summary>
    public bool ForCyclicalMission { get; set; }
    /// <summary>True when the mission should prefer stock fast-route behavior.</summary>
    public bool Fast { get; set; }
    /// <summary>Allows validation to accept synthetic carrier spacecraft such as LV payload containers.</summary>
    public bool AllowSyntheticCarrier { get; set; }
    /// <summary>Mod-defined tags for future routing or diagnostics.</summary>
    public HashSet<string> Tags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Mod-defined metadata for future routing or diagnostics.</summary>
    public Dictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Options controlling how deeply the SDK validates a draft or stock mission parameter.
/// </summary>
public sealed class SdkMissionValidationOptions
{
    /// <summary>When true, calls stock schedule and mission-planning validation.</summary>
    public bool RunStockValidation { get; set; } = true;
    /// <summary>When true, busy spacecraft do not produce an SDK validation issue.</summary>
    public bool AllowBusySpacecraft { get; set; }
}

/// <summary>
/// Structured mission validation result containing stock result data and SDK-normalized issues.
/// </summary>
public sealed class SdkMissionValidationResult
{
    /// <summary>Compact route, mission, and carrier context for logs and snapshots.</summary>
    public SdkMissionContext Context { get; set; }
    /// <summary>Raw nullable result returned by stock <see cref="PMMissionParameter.CheckCanPlanMission"/>.</summary>
    public PMMissionParameter.ResultCheckCanPlanMission? StockResult { get; set; }
    /// <summary>Raw stock planner flag set, when stock validation ran.</summary>
    public PMMissionParameter.EPlanMissionResult? StockPlanResult { get; set; }
    /// <summary>Result from stock <see cref="PMMissionParameter.CheckScheduleFly"/>, when available.</summary>
    public bool? ScheduleOk { get; set; }
    /// <summary>SDK-normalized validation issues.</summary>
    public List<SdkMissionValidationIssue> Issues { get; } = new List<SdkMissionValidationIssue>();
    /// <summary>True when no blocking SDK or stock issues were recorded.</summary>
    public bool Valid => Issues.Count == 0 || Issues.TrueForAll(i => i.Kind == SdkMissionFailureKind.None);
    /// <summary>True when the draft cannot be safely converted into stock validation.</summary>
    public bool HasBlockingStructuralFailure => Issues.Exists(i => i.Structural && !i.Retryable);

    /// <summary>
    /// Adds a normalized validation issue to the result.
    /// </summary>
    public void Add(SdkMissionFailureKind kind, string message, bool retryable, bool playerAction, bool structural = false)
    {
        Issues.Add(new SdkMissionValidationIssue
        {
            Kind = kind,
            Message = message,
            Retryable = retryable,
            RequiresPlayerAction = playerAction,
            Structural = structural
        });
    }
}

/// <summary>
/// One SDK-normalized validation issue, optionally derived from a stock planner flag.
/// </summary>
public sealed class SdkMissionValidationIssue
{
    /// <summary>Normalized failure category.</summary>
    public SdkMissionFailureKind Kind { get; set; }
    /// <summary>Human-readable explanation suitable for logs.</summary>
    public string Message { get; set; }
    /// <summary>True when retrying later may succeed after game state changes.</summary>
    public bool Retryable { get; set; }
    /// <summary>True when player or higher-level mod policy should probably intervene.</summary>
    public bool RequiresPlayerAction { get; set; }
    /// <summary>True when the issue is a missing or invalid SDK/stock object, not a planner rejection.</summary>
    public bool Structural { get; set; }
}

/// <summary>
/// Compact mission context used in logs, snapshots, and validation results.
/// </summary>
public sealed class SdkMissionContext
{
    /// <summary>Owner ID supplied by the consumer mod.</summary>
    public string OwnerId { get; set; }
    /// <summary>Dispatch ID resolved from cycle tracking or the draft.</summary>
    public string DispatchId { get; set; }
    /// <summary>Mission name visible to stock UI or SDK diagnostics.</summary>
    public string MissionName { get; set; }
    /// <summary>Stock source object ID, or -1 when unknown.</summary>
    public int StartId { get; set; } = -1;
    /// <summary>Stock target object ID, or -1 when unknown.</summary>
    public int TargetId { get; set; } = -1;
    /// <summary>Stock source object name, when known.</summary>
    public string StartName { get; set; }
    /// <summary>Stock target object name, when known.</summary>
    public string TargetName { get; set; }
    /// <summary>Real spacecraft ID, or -1 for unknown/non-real carriers.</summary>
    public int SpacecraftId { get; set; } = -1;

    /// <summary>
    /// Builds context from an SDK draft without creating a stock parameter.
    /// </summary>
    public static SdkMissionContext FromDraft(SdkMissionDraft draft)
    {
        return new SdkMissionContext
        {
            OwnerId = draft?.OwnerId,
            DispatchId = draft?.DispatchId,
            MissionName = draft?.MissionName,
            StartId = draft?.Start?.id ?? -1,
            TargetId = draft?.Target?.id ?? -1,
            StartName = draft?.Start?.ObjectName,
            TargetName = draft?.Target?.ObjectName,
            SpacecraftId = (draft?.Spacecraft as Spacecraft)?.ID ?? -1
        };
    }

    /// <summary>
    /// Builds context from an existing stock parameter, including dispatch lookup when available.
    /// </summary>
    public static SdkMissionContext FromParameter(PMMissionParameter parameter)
    {
        return new SdkMissionContext
        {
            DispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(parameter),
            MissionName = parameter?.MissionName,
            StartId = parameter?.Start?.id ?? -1,
            TargetId = parameter?.Target?.id ?? -1,
            StartName = parameter?.Start?.ObjectName,
            TargetName = parameter?.Target?.ObjectName,
            SpacecraftId = (parameter?.SC as Spacecraft)?.ID ?? -1
        };
    }
}

/// <summary>
/// SDK-normalized mission validation and planning failure categories.
/// </summary>
public enum SdkMissionFailureKind
{
    None,
    InvalidDraft,
    InvalidParameter,
    MissingCompany,
    MissingStart,
    MissingTarget,
    MissingSpacecraft,
    InvalidSpacecraft,
    SpacecraftBusy,
    SyntheticCarrier,
    MissingLaunchVehicle,
    InvalidLaunchVehicle,
    InvalidCargo,
    InvalidFuelCargo,
    InsufficientCargoResource,
    CargoOverLimit,
    InsufficientFuel,
    FuelCapacity,
    InsufficientThrust,
    InsufficientLifeSupport,
    InvalidSolarDistance,
    InvalidAsteroidImpact,
    InvalidTransfer,
    ScheduleRejected,
    StockRejected,
    StockException
}
