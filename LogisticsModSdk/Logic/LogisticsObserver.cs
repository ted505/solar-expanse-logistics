using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomUpdate;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.VisualizationScripts;
using Manager;
using SolarExpanseSdk.Core;
using SolarExpanseSdk.Services;
using ScriptableObjectScripts;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace LogisticsModSdk.Logic;

// Central planner/runtime coordinator for logistics. This class intentionally keeps most of
// the mod's state in one place because stock cyclical missions are async and can complete,
// detach, or be rebuilt by stock controllers outside the original call stack.
public static class LogisticsObserver
{
    private const string SdkOwnerTag = "logi";
    private const string SdkReservationOwner = "logisticsmodsdk";
    private static StreamWriter _logWriter;
    private static int _logSession;
    // Return fuel is resolved by asking stock planning code, then reserving cargo room for
    // the return propellant. These constants are conservative guardrails for stock probe
    // failures and routes where the probe reports suspiciously low requirements.
    private const double PrePlanReturnFuelFractionOfTank = 0.1;
    private const double MaxReturnFuelCargoDisplacementFraction = 0.25;
    private const double FastestBackhaulCargoFraction = 0.25;
    private const double ReservePropellantMultiplier = 1.1;
    private static bool VerboseLogging => LogisticsModSdk.Plugin.VerboseLogging?.Value ?? false;
    public static bool VerboseLoggingEnabled => VerboseLogging;
    private static bool TimingLogging => LogisticsModSdk.Plugin.TimingLogging?.Value ?? true;
    private static double TimingLogThresholdMs => Math.Max(0, LogisticsModSdk.Plugin.TimingLogThresholdMs?.Value ?? 5.0);
    private static double CyclePlanningGraceDays => LogisticsModSdk.Plugin.CyclePlanningGraceDays?.Value ?? 3.0;
    private static double EffectiveCyclePlanningGraceDays => Math.Max(CyclePlanningGraceDays, 30.0);
    private static double BlockedMissionRetryCooldownDays => Math.Max(30.0, LogisticsModSdk.Plugin.BlockedMissionRetryCooldownDays?.Value ?? 30.0);
    private const double ReturnCycleBlockedCooldownDays = 30.0;
    private const double ReturnCycleEscalatedCooldownDays = 180.0;
    private const int ReturnCycleEscalationFailureThreshold = 3;
    private const int MaxReturnFuelProbeCacheEntries = 256;
    private static readonly TimeSpan ReturnCycleWallClockThrottle = TimeSpan.FromSeconds(10);
    // The dictionaries below are runtime-only safety rails around stock cycle creation.
    // They are cleared on save/load changes so a logistics state from one save cannot
    // reserve ships, cargo, or route locks in another save.
    private static readonly Dictionary<CycleMissionsData, DateTime> _cycleCreatedAt = new Dictionary<CycleMissionsData, DateTime>();
    private static readonly Dictionary<CycleMissionsData, int> _cyclePlanningFailures = new Dictionary<CycleMissionsData, int>();
    private const int MaxCyclePlanningFailures = 3;
    private static readonly Dictionary<string, ReturnFuelProbeState> _returnFuelProbeCache = new Dictionary<string, ReturnFuelProbeState>();
    private static readonly Queue<string> _returnFuelProbeCacheOrder = new Queue<string>();
    private static readonly Dictionary<string, DateTime> _routePlanningLocks = new Dictionary<string, DateTime>();
    private static readonly Dictionary<string, double> _committedStock = new Dictionary<string, double>();
    private static readonly Dictionary<int, string> _cycleNameByShipId = new Dictionary<int, string>();
    private static readonly Dictionary<string, string> _cycleNameByRouteKey = new Dictionary<string, string>();
    private static readonly Dictionary<string, int> _routeTierCache = new Dictionary<string, int>();
    private static readonly Dictionary<int, MissionInfo> _knownLogisticsMissionInfos = new Dictionary<int, MissionInfo>();
    private static Spacecraft[] _cachedSpacecraft;
    private static float _cachedSpacecraftTime;
    private static DateTime _committedStockWallClock;
    private static DateTime _nextCompletedTrajectoryScan;
    private static DateTime _nextOrphanTrajectoryScan;
    private const double CommittedStockWindowSeconds = 1.0;
    private const double CompletedTrajectoryScanDays = 30.0;
    private const double OrphanTrajectoryScanDays = 180.0;
    private const double RequestPlanThrottleDays = 3.0;
    private const int MaxPrecalculateRouteCacheEntries = 128;
    private const int ProviderPriorityScoreStep = 5;
    private static readonly Dictionary<string, RequestPlanThrottleState> _requestPlanThrottle = new Dictionary<string, RequestPlanThrottleState>();
    private static readonly Dictionary<string, PMMissionParameter.PrecalculateDataToShortFly> _precalculateRouteCache = new Dictionary<string, PMMissionParameter.PrecalculateDataToShortFly>();
    private static readonly Queue<string> _precalculateRouteCacheOrder = new Queue<string>();

    // Lightweight wrappers let us ask stock planner methods about hypothetical vehicles
    // without requiring an actual scene LaunchVehicle instance. This is especially useful
    // for facility-backed launch supports such as elevators/rails.
    private sealed class PlannerLaunchVehicleInfo : ILaunchVehicleInfo
    {
        private readonly LaunchVehicleType _type;
        private readonly ObjectInfo _objectInfo;
        private readonly Company _company;

        public PlannerLaunchVehicleInfo(LaunchVehicleType type, ObjectInfo objectInfo, Company company)
        {
            _type = type;
            _objectInfo = objectInfo;
            _company = company;
        }

        public LaunchVehicleType GetLaunchVehicleType() => _type;
        public ObjectInfo GetActualPosition() => _objectInfo;
        public Company GetCompany() => _company;
        public ObjectInfo GetObjectInfo() => _objectInfo;
        public bool CheckMaximumPayload(CargoAll cargo, ISpacecraftInfo spacecraft) => _type != null && _type.CheckMaximumPayload(cargo, spacecraft);
        public bool CheckMaximumPayloadFuel(float fuelNeed, ISpacecraftInfo spacecraft) => _type != null && _type.CheckMaximumPayloadFuel(fuelNeed, spacecraft);
    }

    private sealed class PlannerSpacecraftInfo : ISpacecraftInfo
    {
        private readonly SpacecraftType _type;
        private readonly Company _company;
        private readonly ObjectInfo _position;
        private readonly string _name;

        public PlannerSpacecraftInfo(Spacecraft source, ObjectInfo position)
        {
            _type = source?.GetTypeSpaceCraft();
            _company = source?.GetCompany();
            _position = position;
            _name = source?.GetSpacecraftName() ?? _type?.NameRocketType ?? "Probe spacecraft";
        }

        public string GetSpacecraftName() => _name;
        public ObjectInfo GetActualPosition() => _position;
        public MissionInfo GetMissionInfo() => null;
        public Company GetCompany() => _company;
        public float GetMass() => _type?.Mass ?? 0f;
        public SpacecraftType GetTypeSpaceCraft() => _type;
        public int GetLifeSupportCurrentWhenFly(float? lerpTime = null) => 0;
        public ObjectInfo GetObjectInfoPlan() => _position;
    }

    private sealed class ReturnFuelProbeState
    {
        public bool Pending;
        public bool Complete;
        public DateTime RequestedAt;
        public DateTime CompletedAt;
        public ResourceDefinition FuelType;
        public double FuelNeed;
        public double MinFuelCost;
        public double AllFuelNeed;
        public double LeftOverFuel;
        public double RequiredReserve;
        public PMMissionParameter.EPlanMissionResult Result;
        public string FailureReason;
    }

    private sealed class RequestPlanThrottleState
    {
        public DateTime NextEvaluation;
        public string Signature;
    }

    private static readonly Dictionary<string, DateTime> _pendingPlanningDeliveries = new Dictionary<string, DateTime>();
    private static readonly Dictionary<string, BlockedRetryState> _blockedPlanningRetries = new Dictionary<string, BlockedRetryState>();
    // Tracks ships that logistics has sent away and expects to return home. Stock only
    // reliably tracks the primary cycle ship, so we maintain our own ownership map to
    // prevent duplicate outbound dispatches and duplicate return cycles.
    private static readonly Dictionary<int, ReturnHomeState> _returnHomeByShipId = new Dictionary<int, ReturnHomeState>();

    private sealed class ReturnHomeState
    {
        public ObjectInfo Home;
        public ObjectInfo Destination;
        public ResourceDefinition Resource;
        public bool HasLeftHome;
        public string LastBlockedReason;
        public string LastBlockedStatusNote;
        public DateTime LastBlockedDate = DateTime.MinValue;
        public string PendingPlanKey;
        public PMMissionParameter PendingPlanParameter;
        public GameManager.PlanFlyCodeResult PendingPlanResult;
        public string ResolvedPlanKey;
        public bool HasResolvedPlanResult;
        public ResourceDefinition ResolvedFuelType;
        public double ResolvedFuelNeed;
        public DateTime ResolvedPlanDate = DateTime.MinValue;
        public DateTime ReturnRetryAfter = DateTime.MinValue;
        public DateTime ReturnRetryWallClockAfterUtc = DateTime.MinValue;
        public int ConsecutiveReturnCycleFailures;
    }

    private sealed class BlockedRetryState
    {
        public DateTime RetryAfter;
        public string Reason;
    }

    private sealed class PlannerSnapshot
    {
        // One-day read model. The daily planner builds this once, adds indexes, then passes
        // it through route ranking/execution so we do not repeatedly scan stock managers.
        public List<ObjectInfo> Objects = new List<ObjectInfo>();
        public List<CycleMissionsData> Cycles = new List<CycleMissionsData>();
        public List<MissionInfo> Missions = new List<MissionInfo>();
        public List<Spacecraft> Ships = new List<Spacecraft>();
        public Dictionary<string, int> ScActive = new Dictionary<string, int>();
        public Dictionary<string, int> LvActive = new Dictionary<string, int>();
        public HashSet<int> CommittedShipIds = new HashSet<int>();
        public Dictionary<int, List<LaunchSupportOption>> LaunchSupportByObjectId = new Dictionary<int, List<LaunchSupportOption>>();
        public Dictionary<ResourceDefinition, List<ObjectInfo>> ProvidersByResource = new Dictionary<ResourceDefinition, List<ObjectInfo>>();
        public Dictionary<int, List<Spacecraft>> ShipsByObjectId = new Dictionary<int, List<Spacecraft>>();
        public Dictionary<int, Spacecraft> ShipsById = new Dictionary<int, Spacecraft>();
        public Dictionary<string, int> ActiveLvUsesByOriginAndType = new Dictionary<string, int>();
        public Dictionary<string, double> InFlightCargoByTargetAndResource = new Dictionary<string, double>();
        public Dictionary<string, List<Offer>> MarketOffersByObjectResourceSide = new Dictionary<string, List<Offer>>();
    }

    private enum RouteKind
    {
        DirectSpacecraft,
        DirectSurfaceLaunch,
        StageSourceSurfaceToOrbit
    }

    private sealed class RouteCandidate
    {
        // Internal candidate model for comparing direct spacecraft, direct LV/container,
        // and one-hop source-orbit staging routes before committing to any stock cycle.
        public RouteKind Kind;
        public ObjectInfo Provider;
        public Data.LogisticsProvider ProviderRule;
        public ObjectInfo EffectiveSource;
        public ObjectInfo StageOrbit;
        public Spacecraft Spacecraft;
        public Spacecraft StageCarrier;
        public Spacecraft FinalCarrier;
        public LaunchVehicleType LaunchVehicleType;
        public double Amount;
        public double Available;
        public int Tier;
        public int HopCount;
        public bool UsesLV;
        public string Label;
        public string ScoreBreakdown;
    }

    private sealed class PlannerBlocker
    {
        // Best blocker is selected by route tier first, then priority. This keeps Pending
        // status notes focused on the most relevant route instead of whichever provider
        // happened to be scanned last.
        public int Tier = int.MaxValue;
        public int Priority = int.MaxValue;
        public string Reason;
    }

    private sealed class LaunchSupportOption
    {
        // A launch support can be a real LV or a facility-backed pseudo-LV. Route scoring
        // gives facility supports favorable adjustments so elevators/rails win when valid.
        public LaunchVehicle Vehicle;
        public LaunchVehicleType Type;
        public Facility Facility;
        public string Category;
        public string Label;
        public bool IsFacilityBacked;
        public int TierAdjustment;
    }

    public enum ShipState { Idle, InTransit, Pending, Blocked }

    public static string TranslatePlanMissionResult(PMMissionParameter.EPlanMissionResult result)
    {
        if (result == PMMissionParameter.EPlanMissionResult.AllOk)
            return null;

        var parts = new List<string>();
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.NoFuelCantBuy))
            parts.Add("Insufficient fuel at source");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongRemoveFuel))
            parts.Add("Cannot load fuel");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongThrust))
            parts.Add("Insufficient thrust for route");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongMaxCapacityFuelOk))
            parts.Add("Route requires too much fuel for any payload");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV))
            parts.Add("Launch vehicle required");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongResourcesCargoLoadLimit))
            parts.Add("Cargo exceeds load limit");

        if (parts.Count == 0)
            return $"Mission blocked ({result})";

        return string.Join("; ", parts);
    }

    public struct QuotaShipStatus
    {
        public string Name;
        public string Location;
        public string StatusText;
        public DateTime? ETA;
        public ShipState State;
    }

    private static QuotaShipStatus BuildShipStatus(Spacecraft sc, ObjectInfo home, bool forceReserved = false)
    {
        var status = new QuotaShipStatus
        {
            Name = sc?.GetSpacecraftName() ?? sc?.spacecraftType?.NameRocketType ?? "SC",
            Location = sc?.CurrentlyOnThisObject?.ObjectName ?? "?"
        };
        if (sc == null)
            return status;

        ReturnHomeState returnState = null;
        var isTracked = sc.ID >= 0 && _returnHomeByShipId.TryGetValue(sc.ID, out returnState) && returnState != null;
        var mi = sc.GetMissionInfo();

        if (sc.CurrentPhase == Spacecraft.EPhase.Fly || sc.CurrentPhase == Spacecraft.EPhase.Launch
            || sc.CurrentPhase == Spacecraft.EPhase.Landing)
        {
            status.State = ShipState.InTransit;
            status.Location = mi?.target?.ObjectName ?? sc.CurrentlyOnThisObject?.ObjectName ?? "?";
            status.StatusText = forceReserved
                ? (sc.CurrentPhase == Spacecraft.EPhase.Landing ? "Reserved landing" : "Reserved transit")
                : (sc.CurrentPhase == Spacecraft.EPhase.Landing ? "Landing" : "In transit");
            if (mi != null && mi.DateArrive != default)
                status.ETA = mi.DateArrive;
            return status;
        }

        if (sc.CurrentPhase == Spacecraft.EPhase.PlanedMission)
        {
            status.State = ShipState.Pending;
            status.Location = mi?.target?.ObjectName ?? "?";
            status.StatusText = forceReserved ? "Reserved planned" : "Planned";
            if (mi != null && mi.DateArrive != default)
                status.ETA = mi.DateArrive;
            return status;
        }

        if (isTracked && returnState != null)
        {
            var current = sc.CurrentlyOnThisObject;
            if (IsReturnRetryCoolingDown(returnState, out _)
                || (returnState.ConsecutiveReturnCycleFailures > 0 && !string.IsNullOrEmpty(returnState.LastBlockedReason)))
            {
                status.State = ShipState.Blocked;
                status.Location = current?.ObjectName ?? "?";
                status.StatusText = returnState.LastBlockedStatusNote ?? returnState.LastBlockedReason ?? "Blocked";
                return status;
            }

            if (home != null && SameObjectInfo(current, home) && sc.CurrentPhase == Spacecraft.EPhase.None)
            {
                status.State = forceReserved ? ShipState.Pending : ShipState.Idle;
                status.Location = home.ObjectName;
                status.StatusText = forceReserved ? "Reserved" : "Idle";
                return status;
            }

            if (sc.CurrentPhase == Spacecraft.EPhase.None && home != null && !SameObjectInfo(current, home))
            {
                status.State = ShipState.Pending;
                status.Location = current?.ObjectName ?? "?";
                status.StatusText = forceReserved ? "Reserved away" : "Awaiting return";
                return status;
            }

            status.State = ShipState.Pending;
            status.Location = current?.ObjectName ?? "?";
            status.StatusText = forceReserved ? "Reserved return" : "Returning";
            if (mi != null && mi.DateArrive != default)
                status.ETA = mi.DateArrive;
            return status;
        }

        status.State = forceReserved ? ShipState.Pending : ShipState.Idle;
        status.Location = sc.CurrentlyOnThisObject?.ObjectName ?? home?.ObjectName ?? "?";
        status.StatusText = forceReserved ? "Reserved" : "Idle";
        return status;
    }

    public static List<QuotaShipStatus> GetShipStatusesForQuota(ObjectInfo quotaHome, Data.ShipQuotaEntry quota)
    {
        var result = new List<QuotaShipStatus>();
        if (quotaHome == null || quota == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;

        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();

        foreach (var sc in ships)
        {
            if (sc == null || sc.spacecraftType == null || sc.GetCompany() != player)
                continue;
            if (!Data.LogisticsNetwork.QuotaMatches(quota, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"))
                continue;
            var assignedProvider = sc.ID >= 0 ? Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID) : null;
            var isAssignedToSend = assignedProvider != null;

            ReturnHomeState returnState = null;
            var isTracked = sc.ID >= 0 && _returnHomeByShipId.TryGetValue(sc.ID, out returnState) && returnState != null;
            var isHomeHere = isTracked && SameObjectInfo(returnState?.Home, quotaHome);
            var isIdleAtHome = sc.CurrentPhase == Spacecraft.EPhase.None
                && SameObjectInfo(sc.CurrentlyOnThisObject, quotaHome)
                && Data.LogisticsNetwork.IsSpacecraftReadyForLogistics(sc, player, cm);

            if (!isHomeHere && !isIdleAtHome && !(isAssignedToSend && SameObjectInfo(sc.CurrentlyOnThisObject, quotaHome)))
                continue;

            result.Add(BuildShipStatus(sc, quotaHome, isAssignedToSend));
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return result;
    }

    public static List<QuotaShipStatus> GetAllShipsForGetRequest(ObjectInfo target, ResourceDefinition rd)
    {
        var result = new List<QuotaShipStatus>();
        if (target == null || rd == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        var seenIds = new HashSet<int>();
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.MinValue;
        var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;

        // 1. Ships in-flight or planned toward target with this resource (from missions)
        if (mm?.ListMissionInfo != null)
        {
            foreach (var mi in mm.ListMissionInfo)
            {
                if (mi == null || mi.complete || mi.cancel || mi.company != player)
                    continue;
                if (mi.target != target)
                    continue;
                if (!IsLogisticsMissionInfo(mi))
                    continue;
                if (mi.cargoAll != null && !CargoContainsResource(mi.cargoAll, rd)
                    && (mi.cargoAll.listCargoToOrbit == null || !mi.cargoAll.listCargoToOrbit.Any(c => c != null && c.resourceType == rd && c.cargoMass > 0)))
                    continue;

                if (mi.spacecraftInfo2 is Spacecraft sc && sc.ID >= 0 && seenIds.Add(sc.ID))
                    result.Add(BuildShipStatus(sc, target));
            }
        }

        // 2. Ships in _returnHomeByShipId whose destination is this target for this resource
        //    (delivered cargo and are now at destination, awaiting return or blocked)
        foreach (var kv in _returnHomeByShipId)
        {
            var state = kv.Value;
            if (state == null || !SameObjectInfo(state.Destination, target) || state.Resource != rd)
                continue;
            if (!seenIds.Add(kv.Key))
                continue;

            var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
            if (ships == null) continue;
            var sc = ships.FirstOrDefault(s => s != null && s.ID == kv.Key && s.GetCompany() == player);
            if (sc != null)
                result.Add(BuildShipStatus(sc, state.Home));
        }

        // 3. Ships from active LOGI cycles targeting this body for this resource
        //    that haven't produced a mission yet (still at source, planning or blocked)
        if (cm != null)
        {
            foreach (var cmd in cm.GetAllCycleMission(player))
            {
                if (cmd == null || cmd.CheckComplete() || !IsLogisticsDeliveryMission(cmd))
                    continue;
                if (!SameObjectInfo(cmd.B, target))
                    continue;
                if (!CargoContainsResource(cmd.cargoAllStart, rd))
                    continue;
                if (cmd.ListSC == null) continue;

                foreach (var sci in cmd.ListSC)
                {
                    if (sci is Spacecraft sc && sc.GetCompany() == player && sc.ID >= 0 && seenIds.Add(sc.ID))
                        result.Add(BuildShipStatus(sc, cmd.A));
                }
            }
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            if (s != 0) return s;
            var etaA = a.ETA ?? DateTime.MaxValue;
            var etaB = b.ETA ?? DateTime.MaxValue;
            var eta = etaA.CompareTo(etaB);
            return eta != 0 ? eta : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    public static List<QuotaShipStatus> GetAllShipsForSendProvider(ObjectInfo source, ResourceDefinition rd, Data.LogisticsProvider provider)
    {
        var result = new List<QuotaShipStatus>();
        if (source == null || rd == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        var seenIds = new HashSet<int>();
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;

        // 1. Explicitly assigned ships
        if (provider?.assignedSpacecraftIds != null)
        {
            var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
            if (ships != null)
            {
                foreach (var sc in ships)
                {
                    if (sc == null || sc.GetCompany() != player || sc.ID < 0)
                        continue;
                    if (!provider.assignedSpacecraftIds.Contains(sc.ID))
                        continue;
                    if (!seenIds.Add(sc.ID))
                        continue;
                    result.Add(BuildShipStatus(sc, source));
                }
            }
        }

        // 2. Shared-pool ships: from active LOGI cycles originating here for this resource
        if (provider == null || provider.useSharedSpacecraftPool)
        {
            if (cm != null)
            {
                foreach (var cmd in cm.GetAllCycleMission(player))
                {
                    if (cmd == null || cmd.CheckComplete() || !IsLogisticsDeliveryMission(cmd))
                        continue;
                    if (!SameObjectInfo(cmd.A, source))
                        continue;
                    if (!CargoContainsResource(cmd.cargoAllStart, rd))
                        continue;
                    if (cmd.ListSC == null) continue;

                    foreach (var sci in cmd.ListSC)
                    {
                        if (sci is Spacecraft sc && sc.GetCompany() == player && sc.ID >= 0 && seenIds.Add(sc.ID))
                            result.Add(BuildShipStatus(sc, source));
                    }
                }
            }

            // Also check in-flight missions from this source
            if (mm?.ListMissionInfo != null)
            {
                foreach (var mi in mm.ListMissionInfo)
                {
                    if (mi == null || mi.complete || mi.cancel || mi.company != player)
                        continue;
                    if (mi.start != source)
                        continue;
                    if (!IsLogisticsMissionInfo(mi))
                        continue;
                    if (mi.cargoAll != null && !CargoContainsResource(mi.cargoAll, rd))
                        continue;

                    if (mi.spacecraftInfo2 is Spacecraft sc && sc.ID >= 0 && seenIds.Add(sc.ID))
                        result.Add(BuildShipStatus(sc, source));
                }
            }

            // Ships in return-home that were sent from this source with this resource
            foreach (var kv in _returnHomeByShipId)
            {
                var state = kv.Value;
                if (state == null || !SameObjectInfo(state.Home, source) || state.Resource != rd)
                    continue;
                if (!seenIds.Add(kv.Key))
                    continue;

                var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
                var sc = ships?.FirstOrDefault(s => s != null && s.ID == kv.Key && s.GetCompany() == player);
                if (sc != null)
                    result.Add(BuildShipStatus(sc, source));
            }
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    public static List<QuotaShipStatus> GetShipStatusesForAssignedIds(IEnumerable<int> shipIds, ObjectInfo home, bool forceReserved = true)
    {
        var result = new List<QuotaShipStatus>();
        var ids = shipIds?.Where(id => id >= 0).Distinct().ToHashSet();
        if (ids == null || ids.Count == 0)
            return result;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();
        foreach (var sc in ships)
        {
            if (sc == null || sc.GetCompany() != player || !ids.Contains(sc.ID))
                continue;
            result.Add(BuildShipStatus(sc, home, forceReserved));
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    public static void Log(string msg)
    {
        if (VerboseLogging)
            WriteLog("", msg);
    }

    public static void LogVerbose(string msg)
    {
        if (VerboseLogging)
            Log(msg);
    }

    public static void LogWarning(string msg)
    {
        if (!VerboseLogging)
            return;

        WriteLog("[WARN] ", msg);
        Debug.LogWarning("[LogisticsMod] " + msg);
    }

    public static void LogError(string msg)
    {
        WriteLog("[ERROR] ", msg);
        Debug.LogError("[LogisticsMod] " + msg);
    }

    public static void LogBepInEx(string msg)
    {
        if (VerboseLogging)
        {
            WriteLog("", msg);
            Debug.Log("[LogisticsMod] " + msg);
        }
    }

    // TIMING-PROFILER-REMOVE: remove TimeScope/LogTiming and associated using(...) lines when profiling is no longer needed.
    public static IDisposable TimeScope(string name)
    {
        if (!TimingLogging || string.IsNullOrEmpty(name))
            return null;
        return new TimingScope(name, TimingLogThresholdMs);
    }

    public static IDisposable TimeScope(string name, double thresholdMs)
    {
        if (!TimingLogging || string.IsNullOrEmpty(name))
            return null;
        return new TimingScope(name, Math.Max(0, thresholdMs));
    }

    public static void LogTiming(string name, double milliseconds)
    {
        LogTiming(name, milliseconds, TimingLogThresholdMs);
    }

    public static void LogTiming(string name, double milliseconds, double thresholdMs)
    {
        if (!TimingLogging || milliseconds < thresholdMs)
            return;
        WriteLog("[TIME] ", $"{name} {milliseconds:0.###}ms");
    }

    private sealed class TimingScope : IDisposable
    {
        private readonly string _name;
        private readonly double _thresholdMs;
        private readonly Stopwatch _stopwatch;

        public TimingScope(string name, double thresholdMs)
        {
            _name = name;
            _thresholdMs = thresholdMs;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            LogTiming(_name, _stopwatch.Elapsed.TotalMilliseconds, _thresholdMs);
        }
    }

    private static PlannerSnapshot BuildPlannerSnapshot(Company player)
    {
        using (TimeScope("BuildPlannerSnapshot"))
        {
        var snapshot = new PlannerSnapshot();
        if (player == null) return snapshot;

        // Capture stock state once per daily pass. Later planner stages mutate only the
        // snapshot counters, not stock manager lists, until a candidate actually dispatches.
        using (TimeScope("Snapshot.objects"))
            snapshot.Objects = Data.LogisticsNetwork.GetAllObjects();

        using (TimeScope("Snapshot.cycles"))
        {
            var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
            if (cm != null)
                snapshot.Cycles = cm.GetAllCycleMission(player);
        }

        using (TimeScope("Snapshot.missions"))
        {
            var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            if (mm?.ListMissionInfo != null)
                snapshot.Missions = mm.ListMissionInfo;
        }

        using (TimeScope("Snapshot.ships"))
        {
            snapshot.Ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
                ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();
        }
        BuildPlannerSnapshotIndexes(player, snapshot);

        return snapshot;
        }
    }

    private static void BuildPlannerSnapshotIndexes(Company player, PlannerSnapshot snapshot)
    {
        using (TimeScope("BuildPlannerSnapshotIndexes"))
        {
        if (player == null || snapshot == null)
            return;

        // Provider/resource index is the main route-candidate accelerator. Without it,
        // every GET request scans every known body for every resource.
        using (TimeScope("SnapshotIndex.providers"))
        {
            foreach (var oi in snapshot.Objects)
            {
                var data = Data.LogisticsNetwork.Get(oi);
                if (data?.providers == null)
                    continue;

                foreach (var provider in data.providers)
                {
                    var rd = provider?.ResourceDefinition;
                    if (!Data.LogisticsResourceFilter.IsSupported(rd) || !provider.isActive)
                        continue;

                    if (!snapshot.ProvidersByResource.TryGetValue(rd, out var providers))
                    {
                        providers = new List<ObjectInfo>();
                        snapshot.ProvidersByResource[rd] = providers;
                    }

                    if (!providers.Contains(oi))
                        providers.Add(oi);

                    if (provider.exportToOrbit && oi.LowOrbitCustom?.GetObjectInfo() is ObjectInfo orbitOI
                        && !providers.Contains(orbitOI))
                    {
                        providers.Add(orbitOI);
                    }
                }
            }
        }

        // Ship indexes serve both quota display semantics and ownership safety checks.
        // They also avoid falling back to Unity object scans inside route selection.
        using (TimeScope("SnapshotIndex.ships"))
        {
            foreach (var sc in snapshot.Ships)
            {
                if (sc == null || sc.spacecraftType == null)
                    continue;
                if (sc.GetCompany() != player)
                    continue;
                if (sc.ID >= 0 && !snapshot.ShipsById.ContainsKey(sc.ID))
                    snapshot.ShipsById[sc.ID] = sc;

                var location = sc.CurrentlyOnThisObject;
                if (location == null)
                    continue;

                if (!snapshot.ShipsByObjectId.TryGetValue(location.id, out var ships))
                {
                    ships = new List<Spacecraft>();
                    snapshot.ShipsByObjectId[location.id] = ships;
                }
                ships.Add(sc);
            }
        }

        // In-flight cargo counts are part of request accounting. They keep min/target and
        // one-shot requests from ordering duplicate shipments while stock missions exist.
        using (TimeScope("SnapshotIndex.inFlightCargo"))
        {
            foreach (var mi in snapshot.Missions)
            {
                if (IsLogisticsMissionInfo(mi))
                    RegisterLogisticsMissionInfo(mi);
                if (mi == null || mi.complete || mi.cancel) continue;
                if (mi.company != player) continue;
                if (mi.target == null || mi.cargoAll == null) continue;

                AddInFlightCargo(snapshot, mi.target, mi.cargoAll.listCargo);
                AddInFlightCargo(snapshot, mi.target, mi.cargoAll.listCargoToOrbit);
            }
        }

        // Market offers are indexed by body/resource/side so Auto-Buy/Auto-Sell can avoid
        // walking the entire economy for each individual logistics rule. If no automation
        // uses offers, skip the index entirely; large Earth markets can spike here.
        if (Data.LogisticsNetwork.HasMarketAutomationRules())
        {
            using (TimeScope("SnapshotIndex.marketOffers"))
            {
                var offers = MonoBehaviourSingleton<MarketOfferManager>.Instance?.Offerts;
                if (offers != null)
                {
                    foreach (var offer in offers)
                    {
                        if (offer == null || offer.OfferDone || offer.WhereOffer == null || offer.Rd == null || offer.CountLeft <= 0)
                            continue;

                        var key = MarketOfferKey(offer.WhereOffer, offer.Rd, offer.BuySell);
                        if (key == null)
                            continue;

                        if (!snapshot.MarketOffersByObjectResourceSide.TryGetValue(key, out var list))
                        {
                            list = new List<Offer>();
                            snapshot.MarketOffersByObjectResourceSide[key] = list;
                        }
                        list.Add(offer);
                    }
                }
            }
        }
        }
    }

    private static void AddInFlightCargo(PlannerSnapshot snapshot, ObjectInfo target, IEnumerable<Cargo> cargoList)
    {
        if (snapshot == null || target == null || cargoList == null)
            return;

        foreach (var cargo in cargoList)
        {
            if (cargo == null
                || cargo.resourceTypeType != EResourceTypeType.resorces
                || cargo.resourceType == null
                || cargo.cargoMass <= 0)
            {
                continue;
            }

            var key = TargetResourceKey(target, cargo.resourceType);
            if (key == null)
                continue;
            snapshot.InFlightCargoByTargetAndResource.TryGetValue(key, out var existing);
            snapshot.InFlightCargoByTargetAndResource[key] = existing + cargo.cargoMass;
        }
    }

    private static string TargetResourceKey(ObjectInfo target, ResourceDefinition rd)
    {
        if (target == null || rd == null)
            return null;
        return $"{target.id}|{rd.ID}";
    }

    private static string MarketOfferKey(ObjectInfo oi, ResourceDefinition rd, bool buySell)
    {
        if (oi == null || rd == null)
            return null;
        return $"{oi.id}|{rd.ID}|{buySell}";
    }

    public static void ApplyCachedPrecalculateData(PMMissionParameter pmp)
    {
        // Stock recalculates short-flight/moon-case data for each fresh one-shot cycle.
        // Reusing the last successful route cache avoids doing that expensive route pass
        // repeatedly for identical logistics legs.
        var key = BuildPrecalculateRouteKey(pmp);
        if (key == null) return;
        if (!_precalculateRouteCache.TryGetValue(key, out var cached)) return;

        pmp.SetPrecalculateDataToShortFly(ClonePrecalculateData(cached));
        LogVerbose($"PRECACHE apply: key={key} moonCase={cached.moonCase}");
    }

    public static void CachePrecalculateData(PMMissionParameter pmp, string context)
    {
        // Only cache the route precompute blob once stock has successfully produced it.
        // The key includes vehicle/LV/transfer choices so fastest and optimal routes do
        // not contaminate each other.
        if (pmp == null || !pmp.MoonCase)
            return;

        var key = BuildPrecalculateRouteKey(pmp);
        if (key == null) return;

        var data = new PMMissionParameter.PrecalculateDataToShortFly
        {
            moonCase = pmp.MoonCase,
            moonCaseCostMax = pmp.MoonCaseCostMax,
            moonCaseCostMin = pmp.MoonCaseCostMin,
            minDeltaVMoonCase = pmp.MinDeltaVMoonCase
        };

        if (!_precalculateRouteCache.ContainsKey(key))
            _precalculateRouteCacheOrder.Enqueue(key);
        _precalculateRouteCache[key] = data;

        while (_precalculateRouteCacheOrder.Count > MaxPrecalculateRouteCacheEntries)
        {
            var evict = _precalculateRouteCacheOrder.Dequeue();
            _precalculateRouteCache.Remove(evict);
        }

        LogVerbose($"PRECACHE store: context={context} key={key} minDV={data.minDeltaVMoonCase:0.#}");
    }

    private static PMMissionParameter.PrecalculateDataToShortFly ClonePrecalculateData(PMMissionParameter.PrecalculateDataToShortFly source)
    {
        if (source == null) return null;
        return new PMMissionParameter.PrecalculateDataToShortFly
        {
            moonCase = source.moonCase,
            moonCaseCostMax = source.moonCaseCostMax,
            moonCaseCostMin = source.moonCaseCostMin,
            minDeltaVMoonCase = source.minDeltaVMoonCase
        };
    }

    private static string BuildPrecalculateRouteKey(PMMissionParameter pmp)
    {
        if (pmp == null || pmp.FlyCompany == null)
            return null;

        var source = pmp.Start;
        var target = pmp.Target;
        if (source == null || target == null)
            return null;

        var scType = pmp.SC?.GetTypeSpaceCraft();
        var lvType = pmp.LV?.GetLaunchVehicleType();
        var scKey = scType?.ID.ToString() ?? scType?.NameRocketType ?? "no-sc";
        var lvKey = lvType?.ID.ToString() ?? lvType?.Name ?? "no-lv";
        return $"{pmp.FlyCompany.ID}|{source.id}->{target.id}|{pmp.TransferTypeMoonCase}|fast={pmp.TryFastAsPossible}|sc={scKey}|lv={lvKey}";
    }

    private static void WriteLog(string level, string msg)
    {
        if (_logWriter == null)
        {
            _logSession++;
            var path = Path.Combine(Application.dataPath, "..", "BepInEx", $"LogisticsMod_{_logSession}.log");
            _logWriter = new StreamWriter(path, false) { AutoFlush = true };
            _logWriter.WriteLine($"=== {DateTime.Now} session={_logSession} ===");
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {level}{msg}";
        _logWriter.WriteLine(line);
    }

    public static void ResetRuntimeState()
    {
        // Runtime-only state must not survive a save switch. Persisted logistics rules live
        // in LogisticsNetwork; everything here is a lock, cache, pending marker, or ownership
        // hint derived from the currently loaded save.
        var cycleCount = _cycleCreatedAt.Count;
        var pendingCount = _pendingPlanningDeliveries.Count;
        var returnCount = _returnHomeByShipId.Count;
        var failCount = _cyclePlanningFailures.Count;
        var fuelProbeCount = _returnFuelProbeCache.Count;
        var routeLockCount = _routePlanningLocks.Count;
        var committedCount = _committedStock.Count;
        var throttleCount = _requestPlanThrottle.Count;
        var precalcCount = _precalculateRouteCache.Count;
        var routeTierCount = _routeTierCache.Count;
        var knownMissionCount = _knownLogisticsMissionInfos.Count;
        _cycleCreatedAt.Clear();
        _cyclePlanningFailures.Clear();
        _pendingPlanningDeliveries.Clear();
        _returnHomeByShipId.Clear();
        _returnFuelProbeCache.Clear();
        _returnFuelProbeCacheOrder.Clear();
        _routePlanningLocks.Clear();
        _committedStock.Clear();
        _requestPlanThrottle.Clear();
        _precalculateRouteCache.Clear();
        _precalculateRouteCacheOrder.Clear();
        _routeTierCache.Clear();
        _cycleNameByShipId.Clear();
        _cycleNameByRouteKey.Clear();
        _knownLogisticsMissionInfos.Clear();
        SolarSdk.Fleet.ClearReservations(SdkReservationOwner);
        SolarSdk.CyclicalMissions.ClearOwner(SdkOwnerTag);
        _cachedSpacecraft = null;
        _nextCompletedTrajectoryScan = default;
        _nextOrphanTrajectoryScan = default;
        if (VerboseLoggingEnabled)
            LogVerbose($"RESET runtime-state: cycles={cycleCount} pending={pendingCount} returns={returnCount} failures={failCount} fuelProbes={fuelProbeCount} routeLocks={routeLockCount} committed={committedCount} throttles={throttleCount} precalc={precalcCount} routeTiers={routeTierCount} knownMissions={knownMissionCount}");
    }

    public static object BuildSdkDebugSnapshot()
    {
        var objects = Data.LogisticsNetwork.GetAllObjects()
            .Select(oi =>
            {
                var data = Data.LogisticsNetwork.Get(oi);
                return new
                {
                    id = oi?.id ?? -1,
                    name = oi?.ObjectName,
                    requests = data?.requests?.Count ?? 0,
                    providers = data?.providers?.Count ?? 0,
                    spacecraftQuota = data?.spacecraftQuota?.Count ?? 0,
                    launchVehicleQuota = data?.launchVehicleQuota?.Count ?? 0
                };
            })
            .ToList();

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var activeLogiCycles = SolarSdk.CyclicalMissions.FindTagged(player, "[LOGI")
            .Select(c => new
            {
                name = c.customNameFromPlanMission,
                source = c.A?.ObjectName,
                target = c.B?.ObjectName,
                complete = c.CheckComplete(),
                dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(c)
            })
            .ToList();

        return new
        {
            objects,
            activeLogiCycles,
            runtime = new
            {
                cycleCreated = _cycleCreatedAt.Count,
                cycleFailures = _cyclePlanningFailures.Count,
                pendingPlanning = _pendingPlanningDeliveries.Count,
                returnHome = _returnHomeByShipId.Count,
                returnFuelProbes = _returnFuelProbeCache.Count,
                routeLocks = _routePlanningLocks.Count,
                committedStock = _committedStock.Count,
                knownMissions = _knownLogisticsMissionInfos.Count
            },
            sdk = new
            {
                reservations = SolarSdk.Fleet.GetReservationsSnapshot(),
                syntheticCarriers = SolarSdk.Fleet.GetSyntheticCarrierSnapshot(),
                dispatches = SolarSdk.CyclicalMissions.GetTrackersSnapshot()
            }
        };
    }

    public static bool IsLogisticsMissionInfo(MissionInfo mi)
    {
        return mi?.missionName != null
            && mi.missionName.StartsWith("[LOGI", StringComparison.Ordinal);
    }

    public static void RegisterLogisticsMissionInfo(MissionInfo mi)
    {
        if (!IsLogisticsMissionInfo(mi))
            return;

        _knownLogisticsMissionInfos[mi.id] = mi;
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(mi);
        if (!string.IsNullOrEmpty(dispatchId))
            SolarSdk.CyclicalMissions.RegisterMissionInfo(dispatchId, mi);
    }

    public static void SetCyclePlanFailureNote(ObjectInfo target, InfoCargoCyclicalMission cargoInfo, string tooltip)
    {
        if (target == null || string.IsNullOrEmpty(tooltip)) return;
        var data = Data.LogisticsNetwork.Get(target);
        if (data?.requests == null) return;

        var resources = cargoInfo?.Tab;
        foreach (var req in data.requests)
        {
            if (req.status != Data.LogisticsRequestStatus.InProgress
                && req.status != Data.LogisticsRequestStatus.Pending)
                continue;

            if (resources != null && resources.Length > 0)
            {
                var rd = req.ResourceDefinition;
                if (rd == null || !resources.Any(r => r == rd))
                    continue;
            }

            req.statusNote = tooltip;
            LogVerbose($"CYCLE plan-failure-note: target={target.ObjectName} rd={req.ResourceDefinition?.ID} note={tooltip}");
        }
    }

    public static void SetShipBlockedReason(IEnumerable<ISpacecraftInfo> ships, string reason)
    {
        if (ships == null || string.IsNullOrEmpty(reason)) return;
        foreach (var sci in ships)
        {
            if (sci is Spacecraft sc && sc.ID >= 0
                && _returnHomeByShipId.TryGetValue(sc.ID, out var state) && state != null)
            {
                state.LastBlockedReason = reason;
                state.LastBlockedStatusNote = reason;
                LogVerbose($"SHIP blocked-reason: ship={sc.GetSpacecraftName()} id={sc.ID} reason={reason}");
            }
        }
    }

    public static void CleanupCompletedLogisticsMissionTrajectories(Company player = null)
    {
        CleanupCompletedLogisticsMissionTrajectories(player, null);
    }

    private static void CleanupCompletedLogisticsMissionTrajectories(Company player, PlannerSnapshot snapshot)
    {
        // Stock sometimes leaves the visual trajectory object after a LOGI MissionInfo is
        // already complete. We remove only confirmed logistics mission visuals.
        if (_knownLogisticsMissionInfos.Count == 0)
            return;

        foreach (var pair in _knownLogisticsMissionInfos.ToList())
        {
            var mi = pair.Value;
            if (mi == null || !mi.complete || mi.cancel) continue;
            if (player != null && mi.company != player) continue;
            CleanupLogisticsMissionTrajectory(mi, "completed-scan");
            _knownLogisticsMissionInfos.Remove(pair.Key);
        }
    }

    public static void CleanupLogisticsMissionTrajectory(MissionInfo mi, string reason)
    {
        if (!IsLogisticsMissionInfo(mi)) return;

        var trajectory = mi.trajectoryObject;
        if (trajectory == null) return;

        LogVerbose($"CLEANUP completed LOGI trajectory: mission={mi.id} name=\"{mi.missionName}\" reason={reason} arrive={mi.DateArrive:yyyy-MM-dd}");
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(mi);
        if (!string.IsNullOrEmpty(dispatchId))
        {
            SolarSdk.CyclicalMissions.MarkCompleted(dispatchId, reason);
            if (SolarSdk.Fleet.HasSyntheticCarrier(dispatchId))
                SolarSdk.Fleet.ReleaseSyntheticCarrier(dispatchId, SdkReservationOwner);
        }
        if (mi.spacecraftInfo2 is Spacecraft sc && sc.ID >= 0 && SolarSdk.Fleet.IsReserved(sc.ID))
            SolarSdk.Fleet.ReleaseSpacecraft(sc.ID, SdkReservationOwner);
        UnityEngine.Object.Destroy(trajectory.gameObject);
        _knownLogisticsMissionInfos.Remove(mi.id);
    }

    private static void CleanupOrphanLogisticsTrajectories(Company player, PlannerSnapshot snapshot)
    {
        // Orphan trajectory scans are slower than normal request planning, so OnDayChange
        // runs this on a long interval and only for routes that still match active LOGI cycles.
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (player == null || cm == null) return;

        var cycles = snapshot?.Cycles ?? cm.GetAllCycleMission(player);
        var activeRouteKeys = new HashSet<string>();
        foreach (var cmd in cycles)
        {
            if (!IsLogisticsMission(cmd) || cmd.CheckComplete()) continue;
            var key = TrajectoryRouteKey(cmd.A, cmd.B);
            if (key != null)
                activeRouteKeys.Add(key);
        }
        if (activeRouteKeys.Count == 0)
            return;

        var missionTrajectories = new HashSet<TrajectoryObject>();
        foreach (var mi in snapshot?.Missions ?? new List<MissionInfo>())
        {
            if (mi?.trajectoryObject != null)
                missionTrajectories.Add(mi.trajectoryObject);
        }

        foreach (var trajectory in UnityEngine.Object.FindObjectsOfType<TrajectoryObject>())
        {
            if (trajectory == null || missionTrajectories.Contains(trajectory)) continue;
            var start = trajectory.StartObjectInfo;
            var target = trajectory.EndObjectInfo;
            if (start == null || target == null) continue;
            if (!activeRouteKeys.Contains(TrajectoryRouteKey(start, target))) continue;

            LogWarning($"CLEANUP orphan LOGI trajectory: {start.ObjectName}->{target.ObjectName} launch={trajectory.StartDate:yyyy-MM-dd} arrive={trajectory.EndDate:yyyy-MM-dd}");
            UnityEngine.Object.Destroy(trajectory.gameObject);
        }
    }

    private static string TrajectoryRouteKey(ObjectInfo start, ObjectInfo target)
    {
        if (start == null || target == null)
            return null;
        var first = Math.Min(start.id, target.id);
        var second = Math.Max(start.id, target.id);
        return $"{first}|{second}";
    }

    private static void CleanupStaleUnlaunchedLogisticsMissions(Company player, PlannerSnapshot snapshot)
    {
        // If a stock async plan produced a MissionInfo but the ship never left its planning
        // phase, cancel that stale shell so the request can be planned again later.
        var missions = snapshot?.Missions;
        if (player == null || missions == null) return;

        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        foreach (var mi in missions.ToList())
        {
            if (mi == null || mi.complete || mi.cancel) continue;
            if (mi.company != player || !IsLogisticsMissionInfo(mi)) continue;
            if (mi.DateLaunch == default || mi.DateLaunch.AddDays(1.0) > now) continue;

            var sc = mi.spacecraftInfo2 as Spacecraft;
            if (sc == null) continue;
            if (sc.CurrentPhase != Spacecraft.EPhase.None && sc.CurrentPhase != Spacecraft.EPhase.PlanedMission)
                continue;

            LogWarning($"CLEANUP stale unlaunched LOGI mission: mission={mi.id} name=\"{mi.missionName}\" ship={sc.GetSpacecraftName()} id={sc.ID} phase={sc.CurrentPhase} launch={mi.DateLaunch:yyyy-MM-dd} now={now:yyyy-MM-dd}");
            mi.cancelFromRocketLauncher = true;
            sc.CancelMission(mi);
            mi.cancelFromRocketLauncher = false;
        }
    }

    private static bool MatchesActiveLogisticsCycle(IEnumerable<CycleMissionsData> cycles, ObjectInfo start, ObjectInfo target)
    {
        if (cycles == null || start == null || target == null) return false;
        foreach (var cmd in cycles)
        {
            if (!IsLogisticsMission(cmd) || cmd.CheckComplete()) continue;
            if ((cmd.A == start && cmd.B == target) || (cmd.B == start && cmd.A == target))
                return true;
        }
        return false;
    }

    private static void HandOffCycleToStockPlanner(Spacecraft sc, CycleMissionsData cmd, string context, string routeLockKey = null)
    {
        using (TimeScope($"HandOffCycleToStockPlanner {context} {cmd?.A?.ObjectName ?? "null"}->{cmd?.B?.ObjectName ?? "null"}"))
        {
        if (sc == null || cmd == null) return;

        SolarSdk.CyclicalMissions.HandOffToStockPlanner(sc, cmd, context,
            afterPlanned: _ =>
        {
            ReleaseRoutePlanningLock(routeLockKey, $"{context}-callback");
            if (!IsLogisticsMission(cmd))
                return;

            var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
            if (cm == null)
                return;

            LogVerbose($"CYCLE one-shot-complete: context={context} route={cmd.A?.ObjectName}->{cmd.B?.ObjectName} ship={sc.GetSpacecraftName()} id={sc.ID}");
            foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                ClearPendingPlanningDelivery(cmd.B, tabRes);
            _cycleCreatedAt.Remove(cmd);
            _cyclePlanningFailures.Remove(cmd);
            RemoveLogisticsCycle(cm, cmd);
        },
            onNotStarted: failure =>
        {
            LogWarning($"CYCLE not-started: context={context} route={cmd.A?.ObjectName ?? "null"}->{cmd.B?.ObjectName ?? "null"} ship={sc.GetSpacecraftName()} id={sc.ID} phase={sc.CurrentPhase} position={sc.CurrentlyOnThisObject?.ObjectName ?? "null"} ctrlCMD={failure.Controller?.CycleMissionsData != null} ctrlPlanFly={failure.Controller?.CycleMissionPlanFlyWas ?? false} cmdWasSet={cmd.wasSetPMParameterForCodeJobSystem} lv={cmd.LvTypeA?.Name ?? "none"} transfer={cmd.TransferType} reason={failure.FailureCode}");
            ReleaseRoutePlanningLock(routeLockKey, $"{context}-not-started");
            if (!string.IsNullOrEmpty(routeLockKey) && IsLogisticsDeliveryMission(cmd))
                RemoveUnstartedOneShotCycle(cmd, context);
        });
        }
    }

    private static void RemoveUnstartedOneShotCycle(CycleMissionsData cmd, string context)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cmd == null || cm == null) return;

        DecommitCycleStock(cmd);

        foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
            ClearPendingPlanningDelivery(cmd.B, tabRes);

        _cycleCreatedAt.Remove(cmd);
        _cyclePlanningFailures.Remove(cmd);
        LogWarning($"CYCLE one-shot-not-started: context={context} route={cmd.A?.ObjectName}->{cmd.B?.ObjectName} name={cmd.customNameFromPlanMission}; removed instead of waiting for partial scraps");
        RemoveLogisticsCycle(cm, cmd);
    }

    private static void ClearRelayState(Data.LogisticsRequest req)
    {
        if (req == null) return;
        req.relayStage = Data.RelayStage.None;
        req.relaySourceObjectId = -1;
        req.relayOrbitObjectId = -1;
        req.relayFinalTargetObjectId = -1;
    }

    private static void SetRelayState(Data.LogisticsRequest req, Data.RelayStage stage,
        ObjectInfo source, ObjectInfo orbit, ObjectInfo finalTarget)
    {
        if (req == null) return;
        req.relayStage = stage;
        req.relaySourceObjectId = source?.id ?? -1;
        req.relayOrbitObjectId = orbit?.id ?? -1;
        req.relayFinalTargetObjectId = finalTarget?.id ?? -1;
    }

    private static ObjectInfo ResolveObject(int objectId)
    {
        if (objectId <= 0) return null;
        return MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(objectId);
    }

    private static bool HasRuntimePlannerWork()
    {
        return _returnHomeByShipId.Count > 0
            || _pendingPlanningDeliveries.Count > 0
            || _blockedPlanningRetries.Count > 0
            || _requestPlanThrottle.Count > 0
            || _routePlanningLocks.Count > 0
            || _committedStock.Count > 0
            || _cycleCreatedAt.Count > 0
            || _cyclePlanningFailures.Count > 0;
    }

    public static void OnDayChange(double days)
    {
        using (TimeScope("OnDayChange"))
        {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return;
        if (!Data.LogisticsNetwork.HasPlannerRules() && !HasRuntimePlannerWork())
        {
            LogVerbose("DAY skip-idle: no logistics requests, auto-sell providers, or runtime planner state");
            return;
        }

        // Committed stock is only meaningful within a single daily tick to prevent
        // same-tick double-spending. Clear it at the start of each tick so prior-tick
        // reservations (which stock has already consumed via launched missions) don't
        // artificially reduce available surplus.
        _committedStock.Clear();

        // Daily planning order matters:
        // 1. Build stock/logistics snapshot and indexes.
        // 2. Reconcile active cycles and stale trajectory/mission artifacts.
        // 3. Try to return owned ships before new outbound planning.
        // 4. Run market automation before provider surplus is consumed.
        // 5. Evaluate requests with stock, in-flight cargo, and pending plans accounted for.
        PlannerSnapshot snapshot;
        snapshot = BuildPlannerSnapshot(player);

        CountActiveLogisticsCycles(player, snapshot.Cycles, out var scActive, out var lvActive, out var committedShipIds);
        snapshot.ScActive = scActive;
        snapshot.LvActive = lvActive;
        snapshot.CommittedShipIds = committedShipIds;
        RebuildActiveLaunchVehicleUseIndex(player, snapshot);

        CleanupStaleUnlaunchedLogisticsMissions(player, snapshot);

        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        if (_nextCompletedTrajectoryScan == default || now >= _nextCompletedTrajectoryScan)
        {
            CleanupCompletedLogisticsMissionTrajectories(player, snapshot);
            _nextCompletedTrajectoryScan = now.AddDays(CompletedTrajectoryScanDays);
        }
        if (_nextOrphanTrajectoryScan == default || now >= _nextOrphanTrajectoryScan)
        {
            CleanupOrphanLogisticsTrajectories(player, snapshot);
            _nextOrphanTrajectoryScan = now.AddDays(OrphanTrajectoryScanDays);
        }

        TryReturnIdleLogisticsShips(player, snapshot);

        ProcessAutoSellProviders(player, snapshot);
        ProcessExportToOrbit(player, snapshot);

        HashSet<ResourceDefinition> networkResources;
        networkResources = Data.LogisticsNetwork.GetNetworkResourcesSet(player, snapshot.Objects);

        foreach (var requesterOI in snapshot.Objects)
        {
            var reqData = Data.LogisticsNetwork.Get(requesterOI);
            if (reqData == null) continue;

            List<Data.LogisticsRequest> fulfilledOneShotRequests = null;
            foreach (var req in reqData.requests
                .OrderByDescending(r => ClampPriority(r?.priority ?? 0))
                .ThenBy(r => r?.ResourceDefinition?.ID ?? r?.resourceDef?.id ?? string.Empty)
                .ToList())
            {
                var rd = req.ResourceDefinition;
                if (!Data.LogisticsResourceFilter.IsSupported(rd))
                    continue;

                if (req.status == Data.LogisticsRequestStatus.Satisfied
                    || req.status == Data.LogisticsRequestStatus.Failed)
                {
                    // One-shot requests that have fully dispatched should never reopen
                    // based on destination stock dropping — they track dispatched, not received.
                    if (rd != null && !(req.oneShot && req.dispatchedAmount >= RequestTarget(req)))
                    {
                        var currentCount = requesterOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
                        if (currentCount < RequestMinimum(req))
                        {
                            if (VerboseLoggingEnabled)
                                LogVerbose($"REOPEN: {rd.ID} on {requesterOI?.ObjectName} stock={currentCount:0.#} minimum={RequestMinimum(req):0.#} target={RequestTarget(req):0.#}");
                            req.status = Data.LogisticsRequestStatus.Pending;
                            ClearRelayState(req);
                        }
                    }
                    if (req.status == Data.LogisticsRequestStatus.Satisfied
                        || req.status == Data.LogisticsRequestStatus.Failed)
                    {
                        var blockedSatisfiedReturnNote = rd != null
                            ? GetReturnBlockedStatusNote(requesterOI, rd, player, snapshot)
                            : null;
                        if (!string.IsNullOrEmpty(blockedSatisfiedReturnNote))
                        {
                            req.status = Data.LogisticsRequestStatus.InProgress;
                            req.statusNote = blockedSatisfiedReturnNote;
                            LogVerbose($"REQ keep-satisfied-return-blocked: target={requesterOI?.ObjectName} rd={rd?.ID} note={blockedSatisfiedReturnNote}");
                            continue;
                        }
                        if (rd != null)
                            CleanupLogisticsCyclesForRequest(requesterOI, rd, player, $"request-{req.status.ToString().ToLowerInvariant()}", snapshot);
                        if (req.oneShot)
                        {
                            fulfilledOneShotRequests ??= new List<Data.LogisticsRequest>();
                            fulfilledOneShotRequests.Add(req);
                            if (VerboseLoggingEnabled)
                                LogVerbose($"ONE-SHOT complete: removing {rd?.ID ?? req.resourceDef?.id} request on {requesterOI?.ObjectName}");
                        }
                        req.statusNote = null;
                        continue;
                    }
                }

                if (req.status == Data.LogisticsRequestStatus.Pending)
                    req.statusNote = (rd != null && networkResources.Contains(rd)) ? null : LogisticsStrings.NoProviderInNetwork();
                else
                    req.statusNote = null;
                if (rd == null) continue;

                if (req.relayFinalTargetObjectId <= 0)
                    req.relayFinalTargetObjectId = requesterOI?.id ?? -1;

                var alreadyThere = requesterOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
                var requestTarget = RequestTarget(req);
                var requestMinimum = RequestMinimum(req);
                var blockedReturnNote = GetReturnBlockedStatusNote(requesterOI, rd, player, snapshot);
                if (req.useMinimumAmount)
                {
                    if (IsRequestTargetCovered(req, alreadyThere) && req.reorderActive)
                    {
                        req.reorderActive = false;
                        LogVerbose($"REQ reorder-close-stock: target={requesterOI?.ObjectName} rd={rd.ID} stock={alreadyThere:0.#} fillTarget={requestTarget:0.#}");
                    }
                    else if (alreadyThere < requestMinimum && !req.reorderActive)
                    {
                        req.reorderActive = true;
                        LogVerbose($"REQ reorder-open: target={requesterOI?.ObjectName} rd={rd.ID} stock={alreadyThere:0.#} minimum={requestMinimum:0.#} fillTarget={requestTarget:0.#}");
                    }
                }
                LogVerbose($"REQ eval: target={requesterOI?.ObjectName} rd={rd.ID} fillTarget={requestTarget:0.#} minimum={requestMinimum:0.#} stock={alreadyThere:0.#} dispatched={req.dispatchedAmount:0.#} status={req.status}");
                bool oneShotDispatched = req.oneShot && req.dispatchedAmount >= requestTarget;
                if (req.oneShot ? oneShotDispatched : IsRequestTargetCovered(req, alreadyThere))
                {
                    if (!string.IsNullOrEmpty(blockedReturnNote))
                    {
                        req.status = Data.LogisticsRequestStatus.InProgress;
                        req.statusNote = blockedReturnNote;
                        LogVerbose($"REQ hold-fulfilled-return-blocked: target={requesterOI?.ObjectName} rd={rd.ID} note={blockedReturnNote}");
                        continue;
                    }
                    if (req.status != Data.LogisticsRequestStatus.Satisfied && VerboseLoggingEnabled)
                        LogVerbose($"SATISFIED: {rd.ID} on {requesterOI?.ObjectName} stock={alreadyThere:0.#} target={requestTarget:0.#} dispatched={req.dispatchedAmount:0.#}");
                    req.status = Data.LogisticsRequestStatus.Satisfied;
                    CleanupLogisticsCyclesForRequest(requesterOI, rd, player, "request-fulfilled", snapshot);
                    if (req.oneShot)
                    {
                        fulfilledOneShotRequests ??= new List<Data.LogisticsRequest>();
                        fulfilledOneShotRequests.Add(req);
                        if (VerboseLoggingEnabled)
                            LogVerbose($"ONE-SHOT fulfilled: removing {rd.ID} request on {requesterOI?.ObjectName} dispatched={req.dispatchedAmount:0.#}");
                    }
                    continue;
                }

                if (HandleRelayProgress(req, requesterOI, rd, requestTarget, alreadyThere, player, snapshot))
                    continue;

                bool hasActiveDelivery = HasActiveCycleDelivering(requesterOI, rd, player, snapshot);
                if (hasActiveDelivery)
                {
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    if (IsTransientPlanningStatus(req.statusNote))
                        req.statusNote = null;
                    LogVerbose($"REQ active-cycle-present: target={requesterOI?.ObjectName} rd={rd.ID}; checking whether additional cargo is still needed");
                }

                if (!hasActiveDelivery && !string.IsNullOrEmpty(blockedReturnNote))
                {
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    req.statusNote = blockedReturnNote;
                    LogVerbose($"REQ return-blocked-note-present: target={requesterOI?.ObjectName} rd={rd.ID} note={blockedReturnNote}; continuing outbound planning");
                }

                var inFlight = GetInFlightDeliveryAmount(requesterOI, rd, player, snapshot);
                if (inFlight > 0)
                    ClearPendingPlanningDelivery(requesterOI, rd);
                if (req.useMinimumAmount && !req.reorderActive)
                {
                    if (!string.IsNullOrEmpty(blockedReturnNote))
                    {
                        req.status = Data.LogisticsRequestStatus.InProgress;
                        req.statusNote = blockedReturnNote;
                    }
                    else
                    {
                        req.status = Data.LogisticsRequestStatus.Satisfied;
                        req.statusNote = null;
                    }
                    LogVerbose($"REQ reorder-idle: target={requesterOI?.ObjectName} rd={rd.ID} stock={alreadyThere:0.#} minimum={requestMinimum:0.#} fillTarget={requestTarget:0.#} inFlight={inFlight:0.#}");
                    continue;
                }
                if (!req.oneShot && IsRequestTargetCovered(req, alreadyThere, inFlight))
                {
                    req.reorderActive = false;
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    req.statusNote = !string.IsNullOrEmpty(blockedReturnNote) ? blockedReturnNote : null;
                    LogVerbose($"REQ reorder-close-inflight: target={requesterOI?.ObjectName} rd={rd.ID} stock={alreadyThere:0.#} inFlight={inFlight:0.#} fillTarget={requestTarget:0.#}");
                    continue;
                }
                var bought = ProcessAutoBuyRequest(req, requesterOI, rd, requestTarget, alreadyThere, inFlight, player, snapshot);
                if (bought > 0)
                {
                    alreadyThere = requesterOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
                    LogVerbose($"AUTO-BUY stock-refresh: target={requesterOI?.ObjectName} rd={rd.ID} bought={bought:0.#} stock={alreadyThere:0.#} target={requestTarget:0.#}");
                    bool oneShotDispatchedAB = req.oneShot && req.dispatchedAmount >= requestTarget;
                    if (req.oneShot ? oneShotDispatchedAB : IsRequestTargetCovered(req, alreadyThere))
                    {
                        if (!string.IsNullOrEmpty(blockedReturnNote))
                        {
                            req.status = Data.LogisticsRequestStatus.InProgress;
                            req.statusNote = blockedReturnNote;
                            continue;
                        }
                        if (req.status != Data.LogisticsRequestStatus.Satisfied && VerboseLoggingEnabled)
                            LogVerbose($"SATISFIED: {rd.ID} on {requesterOI?.ObjectName} stock={alreadyThere:0.#} target={requestTarget:0.#} dispatched={req.dispatchedAmount:0.#}");
                        req.status = Data.LogisticsRequestStatus.Satisfied;
                        CleanupLogisticsCyclesForRequest(requesterOI, rd, player, "request-fulfilled", snapshot);
                        if (req.oneShot)
                        {
                            fulfilledOneShotRequests ??= new List<Data.LogisticsRequest>();
                            fulfilledOneShotRequests.Add(req);
                            if (VerboseLoggingEnabled)
                                LogVerbose($"ONE-SHOT fulfilled: removing {rd.ID} request on {requesterOI?.ObjectName} dispatched={req.dispatchedAmount:0.#}");
                        }
                        continue;
                    }
                    if (!req.oneShot && IsRequestTargetCovered(req, alreadyThere, inFlight))
                    {
                        req.reorderActive = false;
                        req.status = Data.LogisticsRequestStatus.InProgress;
                        req.statusNote = !string.IsNullOrEmpty(blockedReturnNote) ? blockedReturnNote : null;
                        LogVerbose($"REQ reorder-close-autobuy: target={requesterOI?.ObjectName} rd={rd.ID} stock={alreadyThere:0.#} inFlight={inFlight:0.#} fillTarget={requestTarget:0.#}");
                        continue;
                    }
                }

                if (HasPendingPlanningDelivery(requesterOI, rd))
                {
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    LogVerbose($"REQ wait-pending-plan: target={requesterOI?.ObjectName} rd={rd.ID}");
                    continue;
                }

                if (HasBlockedPlanningRetryCooldown(requesterOI, rd, out var cooldownStatus))
                {
                    req.status = hasActiveDelivery || !string.IsNullOrEmpty(blockedReturnNote)
                        ? Data.LogisticsRequestStatus.InProgress
                        : Data.LogisticsRequestStatus.Pending;
                    req.statusNote = !string.IsNullOrEmpty(blockedReturnNote)
                        ? $"{blockedReturnNote}; {cooldownStatus}"
                        : cooldownStatus;
                    continue;
                }

                // For one-shot requests, use dispatched amount (what we've already sent) instead
                // of destination stock. This prevents infinite re-dispatching when the resource
                // is consumed at the destination.
                double remaining = req.oneShot
                    ? requestTarget - req.dispatchedAmount
                    : requestTarget - alreadyThere - inFlight;
                LogVerbose($"REQ remaining: target={requesterOI?.ObjectName} rd={rd.ID} fillTarget={requestTarget:0.#} minimum={requestMinimum:0.#} stock={alreadyThere:0.#} inFlight={inFlight:0.#} dispatched={req.dispatchedAmount:0.#} remaining={remaining:0.#}");
                if (remaining <= 0)
                {
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    if (!string.IsNullOrEmpty(blockedReturnNote))
                        req.statusNote = blockedReturnNote;
                    LogVerbose($"WAIT IN-FLIGHT: {rd.ID} on {requesterOI?.ObjectName} alreadyThere={alreadyThere:0.#} inFlight={inFlight:0.#} fillTarget={requestTarget:0.#}");
                    continue;
                }

                var planningSignature = BuildRequestPlanSignature(requesterOI, rd, requestTarget,
                    alreadyThere, inFlight, hasActiveDelivery, blockedReturnNote, snapshot);
                if (ShouldDeferRequestPlanning(requesterOI, rd, planningSignature, out var throttleStatus))
                {
                    req.status = hasActiveDelivery || !string.IsNullOrEmpty(blockedReturnNote)
                        ? Data.LogisticsRequestStatus.InProgress
                        : Data.LogisticsRequestStatus.Pending;
                    req.statusNote = !string.IsNullOrEmpty(blockedReturnNote)
                        ? $"{blockedReturnNote}; {throttleStatus}"
                        : throttleStatus;
                    continue;
                }

                req.status = hasActiveDelivery
                    ? Data.LogisticsRequestStatus.InProgress
                    : Data.LogisticsRequestStatus.Pending;
                var pendingReason = TryCreateDeliveries(req, requesterOI, rd, remaining, player, snapshot);
                if (string.IsNullOrEmpty(pendingReason))
                    ClearRequestPlanningThrottle(requesterOI, rd);
                else
                    MarkRequestPlanningEvaluated(requesterOI, rd, planningSignature);
                if ((req.status == Data.LogisticsRequestStatus.Pending || hasActiveDelivery) && !string.IsNullOrEmpty(pendingReason))
                    req.statusNote = !string.IsNullOrEmpty(blockedReturnNote)
                        ? $"{blockedReturnNote}; {pendingReason}"
                        : pendingReason;
                else if (!string.IsNullOrEmpty(blockedReturnNote) && req.status == Data.LogisticsRequestStatus.InProgress)
                    req.statusNote = blockedReturnNote;
            }

            if (fulfilledOneShotRequests != null)
            {
                foreach (var removeReq in fulfilledOneShotRequests)
                    reqData.requests.Remove(removeReq);
            }
        }
        }
    }

    private static void ProcessAutoSellProviders(Company player, PlannerSnapshot snapshot)
    {
        if (player == null) return;
        // Auto-Sell runs before exports so logistics does not ship cargo that the provider
        // rule already sold into local market demand.
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        var monthKey = $"{now.Year:D4}-{now.Month:D2}";
        foreach (var oi in snapshot?.Objects ?? Data.LogisticsNetwork.GetAllObjects())
        {
            var data = Data.LogisticsNetwork.Get(oi);
            var oid = oi?.GetObjectInfoData(player);
            if (data?.providers == null || oid == null) continue;

            foreach (var provider in data.providers)
            {
                var rd = provider.ResourceDefinition;
                if (!provider.isActive || !provider.autoSell || !Data.LogisticsResourceFilter.IsSupported(rd))
                    continue;

                if (!string.Equals(provider.autoSellMonthKey, monthKey, StringComparison.Ordinal))
                {
                    provider.autoSellMonthKey = monthKey;
                    provider.autoSellSoldThisMonth = 0;
                }

                var stock = oid.CheckResources(rd);
                var committed = GetCommittedStock(oi, rd);
                var surplus = Math.Max(0, stock - provider.minimumKeep - committed);
                if (provider.autoSellMode == Data.AutoSellMode.PerMonth)
                {
                    var remainingMonthly = Math.Max(0, provider.autoSellMaxPerMonth - provider.autoSellSoldThisMonth);
                    surplus = Math.Min(surplus, remainingMonthly);
                }
                if (surplus <= 0)
                    continue;

                var sold = FulfillMarketOffers(player, oi, rd, surplus, buySell: true,
                    minPrice: provider.autoSellMinPrice, maxPrice: double.MaxValue,
                    buyCheapestFirst: false, snapshot: snapshot);
                if (sold > 0)
                {
                    provider.autoSellSoldThisMonth += sold;
                    LogVerbose($"AUTO-SELL fulfilled: body={oi?.ObjectName} rd={rd.ID} sold={sold:0.#} minPrice={provider.autoSellMinPrice:0.##} mode={provider.autoSellMode}");
                }
            }
        }
    }

    private static void ProcessExportToOrbit(Company player, PlannerSnapshot snapshot)
    {
        if (player == null) return;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null) return;
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;

        foreach (var oi in snapshot?.Objects ?? Data.LogisticsNetwork.GetAllObjects())
        {
            if (oi == null || !oi.NeedVehicleToLaunch()) continue;
            if (oi.objectTypes == global::Data.EObjectTypes.Orbit) continue;

            var data = Data.LogisticsNetwork.Get(oi);
            if (data?.providers == null) continue;

            var orbitOI = oi.LowOrbitCustom?.GetObjectInfo();
            if (orbitOI == null) continue;

            foreach (var provider in data.providers)
            {
                if (!provider.isActive || !provider.exportToOrbit) continue;
                var rd = provider.ResourceDefinition;
                if (!Data.LogisticsResourceFilter.IsSupported(rd)) continue;

                var exportRequest = new Data.LogisticsRequest
                {
                    ResourceDefinition = rd,
                    requestedAmount = double.MaxValue,
                    status = Data.LogisticsRequestStatus.InProgress
                };

                var surplus = GetProviderAvailableAfterMinimum(oi, rd, player);
                if (surplus <= 0) continue;

                if (provider.exportOrbitMaxStock > 0)
                {
                    var orbitStock = orbitOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
                    var orbitInbound = GetInFlightDeliveryAmount(orbitOI, rd, player, snapshot);
                    if (orbitStock + orbitInbound >= provider.exportOrbitMaxStock)
                    {
                        LogVerbose($"EXPORT-ORBIT cap-reached: body={oi.ObjectName} orbit={orbitOI.ObjectName} rd={rd.ID} stock={orbitStock:0.#} inFlight={orbitInbound:0.#} cap={provider.exportOrbitMaxStock:0.#}");
                        continue;
                    }
                }

                if (HasRoutePlanningLock(oi, orbitOI, rd, player, out _))
                    continue;

                var scActive = snapshot?.ScActive ?? new Dictionary<string, int>();
                var lvActive = snapshot?.LvActive ?? new Dictionary<string, int>();
                if (!TryFindSurfaceLaunch(oi, orbitOI, player, scActive, lvActive,
                    requireContainerOnly: true, requireRegularSC: false,
                    out var lvType, out var carrier, out var reason,
                    out var supportDetail, out var tierAdjust, snapshot))
                {
                    LogVerbose($"EXPORT-ORBIT skip: body={oi.ObjectName} rd={rd.ID} surplus={surplus:0.#} reason={reason}");
                    continue;
                }

                var launchSupport = GetAvailableLaunchSupport(oi, player, snapshot);
                var matchingOption = launchSupport.FirstOrDefault(opt =>
                    opt?.Type != null && SameLaunchVehicleType(opt.Type, lvType));
                var carrierCapacity = GetSurfaceToOrbitPayloadCapacity(oi, player, carrier, matchingOption, lvType);
                if (carrierCapacity <= 0) continue;

                var providerMinimumShipment = GetProviderMinimumShipment(oi, rd);
                var minFillAmount = providerMinimumShipment > 0
                    ? providerMinimumShipment
                    : 0;
                if (surplus < minFillAmount)
                {
                    LogVerbose($"EXPORT-ORBIT wait-fill: body={oi.ObjectName} rd={rd.ID} surplus={surplus:0.#} minFill={minFillAmount:0.#} capacity={carrierCapacity:0.#} category={matchingOption?.Category ?? "standard"} tierAdj={tierAdjust}");
                    continue;
                }

                var amount = Math.Min(surplus, carrierCapacity);
                if (carrier?.spacecraftType?.LowOrbitContainer == true)
                    carrier = GetCyclicalOrbitalContainer(player);
                if (carrier == null)
                    continue;

                if (!SetupCycleMission(exportRequest, carrier, rd, amount, orbitOI, oi, lvType,
                        out _, out _, clampToOutstanding: false))
                {
                    LogVerbose($"EXPORT-ORBIT setup-failed: body={oi.ObjectName} orbit={orbitOI.ObjectName} rd={rd.ID} amount={amount:0.#}");
                    continue;
                }

                RecordDispatchInSnapshot(snapshot, carrier, lvType);
                LogVerbose($"EXPORT-ORBIT dispatch: body={oi.ObjectName} orbit={orbitOI.ObjectName} rd={rd.ID} amount={amount:0.#} surplus={surplus:0.#} capacity={carrierCapacity:0.#} lv={lvType?.Name ?? "none"} support={supportDetail ?? "none"} category={matchingOption?.Category ?? "standard"}");
            }
        }
    }

    private static double GetSurfaceToOrbitPayloadCapacity(ObjectInfo source, Company player, Spacecraft carrier,
        LaunchSupportOption support, LaunchVehicleType fallbackLvType)
    {
        var carrierCapacity = carrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (carrierCapacity <= 0)
            return 0;

        // The low-orbit container reports a huge pseudo-capacity. For LV/LOC lifts, the
        // real bottleneck is the selected launch support's payload on this body.
        if (carrier?.spacecraftType?.LowOrbitContainer != true)
            return carrierCapacity;

        var lvType = support?.Type ?? fallbackLvType;
        if (lvType == null || source == null || player == null)
            return carrierCapacity;

        var payload = lvType.MaxPayloadOnThisObject(source, player);
        if (double.IsNaN(payload) || payload <= 0)
            return 0;

        var carrierMass = carrier.GetMass();
        return Math.Max(0, Math.Min(carrierCapacity, payload - carrierMass));
    }

    private static bool IsLogisticsOrbitExportMission(CycleMissionsData cmd)
    {
        return cmd?.customNameFromPlanMission != null
            && cmd.customNameFromPlanMission.StartsWith("[LOGI-ORBIT]", StringComparison.Ordinal);
    }

    private static double ProcessAutoBuyRequest(Data.LogisticsRequest req, ObjectInfo requesterOI,
        ResourceDefinition rd, double requestTarget, double alreadyThere, double inFlight, Company player,
        PlannerSnapshot snapshot = null)
    {
        // Auto-Buy fills only the remaining shortage to target. If local market purchases
        // cover the request, normal route planning is skipped for that daily pass.
        if (req == null || !req.autoBuy || requesterOI == null || rd == null || player == null)
            return 0;
        if (req.autoBuyMaxPrice <= 0)
            return 0;

        var shortage = Math.Max(0, requestTarget - alreadyThere - inFlight);
        if (shortage <= 0)
            return 0;

        var bought = FulfillMarketOffers(player, requesterOI, rd, shortage, buySell: false,
            minPrice: 0, maxPrice: req.autoBuyMaxPrice, buyCheapestFirst: true, snapshot: snapshot);
        if (bought > 0)
            LogVerbose($"AUTO-BUY fulfilled: body={requesterOI?.ObjectName} rd={rd.ID} bought={bought:0.#} maxPrice={req.autoBuyMaxPrice:0.##}");
        return bought;
    }

    private static IEnumerable<Offer> GetMarketOffers(ObjectInfo oi, ResourceDefinition rd, bool buySell,
        PlannerSnapshot snapshot = null)
    {
        var key = MarketOfferKey(oi, rd, buySell);
        if (key != null && snapshot?.MarketOffersByObjectResourceSide != null)
        {
            return snapshot.MarketOffersByObjectResourceSide.TryGetValue(key, out var indexedOffers)
                ? indexedOffers
                : Enumerable.Empty<Offer>();
        }

        return MonoBehaviourSingleton<MarketOfferManager>.Instance?.Offerts ?? Enumerable.Empty<Offer>();
    }

    private static double FulfillMarketOffers(Company player, ObjectInfo oi, ResourceDefinition rd,
        double desiredAmount, bool buySell, double minPrice, double maxPrice, bool buyCheapestFirst,
        PlannerSnapshot snapshot = null)
    {
        // Fulfill through stock offers rather than mutating resources/money directly; this
        // preserves stock accounting and any market analytics/hooks attached to FullFill.
        var offers = GetMarketOffers(oi, rd, buySell, snapshot);
        if (player == null || oi == null || rd == null || offers == null || desiredAmount <= 0)
            return 0;

        var query = offers.Where(offer => offer != null
            && !offer.OfferDone
            && offer.WhereOffer == oi
            && offer.Rd == rd
            && offer.BuySell == buySell
            && offer.CountLeft > 0
            && offer.PricePerUnit >= minPrice
            && offer.PricePerUnit <= maxPrice);
        query = buyCheapestFirst
            ? query.OrderBy(offer => offer.PricePerUnit)
            : query.OrderByDescending(offer => offer.PricePerUnit);

        double fulfilled = 0;
        foreach (var offer in query.ToList())
        {
            var remaining = desiredAmount - fulfilled;
            if (remaining <= 0)
                break;

            var amount = Math.Min(remaining, offer.CountLeft);
            if (!buySell)
            {
                var affordable = offer.PricePerUnit > 0
                    ? Math.Floor(player.MoneyController.CurrentMoney / offer.PricePerUnit)
                    : 0;
                amount = Math.Min(amount, affordable);
            }
            if (amount <= 0)
                break;

            if (offer.CanFullFill(player, (float)amount, out _) && offer.FullFill(player, amount))
                fulfilled += amount;
        }
        return fulfilled;
    }

    private static bool HandleRelayProgress(Data.LogisticsRequest req, ObjectInfo requesterOI,
        ResourceDefinition rd, double requestTarget, double alreadyThere, Company player, PlannerSnapshot snapshot = null)
    {
        if (req == null || requesterOI == null || rd == null || player == null)
            return false;
        if (req.relayStage == Data.RelayStage.None)
            return false;

        var sourceOI = ResolveObject(req.relaySourceObjectId);
        var orbitOI = ResolveObject(req.relayOrbitObjectId);
        var finalTargetOI = ResolveObject(req.relayFinalTargetObjectId) ?? requesterOI;
        if (sourceOI == null || orbitOI == null || finalTargetOI == null)
        {
            ClearRelayState(req);
            return false;
        }
        var relayProviderRule = ResolveRelayProviderRule(sourceOI, rd, req.networkId);

        if (req.relayStage == Data.RelayStage.WaitingForSourceOrbitStock)
        {
            if (HasActiveCycleDelivering(orbitOI, rd, player, snapshot) || HasPendingPlanningDelivery(orbitOI, rd))
            {
                req.status = Data.LogisticsRequestStatus.InProgress;
                req.statusNote = LogisticsStrings.StagingTo(orbitOI);
                return true;
            }

            var orbitStock = orbitOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
            if (orbitStock > 0)
            {
                req.relayStage = Data.RelayStage.WaitingForFinalLeg;
                req.status = Data.LogisticsRequestStatus.InProgress;
                req.statusNote = LogisticsStrings.StagedAt(orbitOI);
                if (VerboseLoggingEnabled)
                    LogVerbose($"RELAY staged-stock-ready: rd={rd.ID} source={sourceOI.ObjectName} orbit={orbitOI.ObjectName} target={finalTargetOI.ObjectName} stock={orbitStock:0.#}");
                return true;
            }

            ClearRelayState(req);
            return false;
        }

        var hasActiveFinalDelivery = HasActiveCycleDelivering(finalTargetOI, rd, player, snapshot);
        if (HasPendingPlanningDelivery(finalTargetOI, rd))
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = LogisticsStrings.ShippingFrom(orbitOI);
            return true;
        }
        if (hasActiveFinalDelivery)
            LogVerbose($"RELAY final-leg-active: target={finalTargetOI.ObjectName} rd={rd.ID}; checking whether additional staged cargo is still needed");

        var committedFromOrbit = GetCommittedStock(orbitOI, rd);
        var rawStagedStock = orbitOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
        var stagedStock = rawStagedStock - committedFromOrbit;

        if (committedFromOrbit > 0 && stagedStock <= 0)
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = $"Waiting for prior shipment from {orbitOI.ObjectName}";
            LogVerbose($"RELAY serialized-wait: rd={rd.ID} orbit={orbitOI.ObjectName} target={finalTargetOI.ObjectName} rawStaged={rawStagedStock:0.#} committed={committedFromOrbit:0.#}");
            return true;
        }

        if (stagedStock <= 0)
        {
            ClearRelayState(req);
            return false;
        }

        var inFlight = GetInFlightDeliveryAmount(finalTargetOI, rd, player, snapshot);
        var remaining = req.oneShot
            ? RequestTarget(req) - req.dispatchedAmount
            : requestTarget - alreadyThere - inFlight;
        if (remaining <= 0)
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = LogisticsStrings.ShippingFrom(orbitOI);
            return true;
        }

        var usefulFinalLoad = GetUsefulRelayFinalLoad(orbitOI, finalTargetOI, rd, remaining, player, snapshot, relayProviderRule);
        if (committedFromOrbit > 0 && stagedStock < usefulFinalLoad)
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = $"Waiting for prior shipment from {orbitOI.ObjectName}";
            LogVerbose($"RELAY serialized-wait: rd={rd.ID} orbit={orbitOI.ObjectName} target={finalTargetOI.ObjectName} staged={stagedStock:0.#} committed={committedFromOrbit:0.#} usefulLoad={usefulFinalLoad:0.#}");
            return true;
        }
        if (usefulFinalLoad > 0 && stagedStock < usefulFinalLoad && stagedStock < remaining)
        {
            if (VerboseLoggingEnabled)
                LogVerbose($"RELAY restage-needed: rd={rd.ID} orbit={orbitOI.ObjectName} target={finalTargetOI.ObjectName} staged={stagedStock:0.#} usefulLoad={usefulFinalLoad:0.#} remaining={remaining:0.#}");
            ClearRelayState(req);
            return false;
        }

        if (TryCreateRelayFinalDelivery(req, finalTargetOI, orbitOI, rd, Math.Min(remaining, stagedStock), player, snapshot, relayProviderRule))
            return true;

        req.status = Data.LogisticsRequestStatus.InProgress;
        req.statusNote = LogisticsStrings.WaitingForSpacecraftAt(orbitOI);
        return true;
    }

    private static Data.LogisticsProvider ResolveRelayProviderRule(ObjectInfo source, ResourceDefinition rd, int requestNetworkId)
    {
        var data = Data.LogisticsNetwork.Get(source);
        if (data == null || rd == null)
            return null;

        return GetMatchingProviderRules(data, rd, requestNetworkId)
            .OrderByDescending(p => p.assignedSpacecraftIds?.Count ?? 0)
            .ThenBy(p => p.useSharedSpacecraftPool ? 1 : 0)
            .FirstOrDefault();
    }

    private static double GetUsefulRelayFinalLoad(ObjectInfo sourceOrbit, ObjectInfo target, ResourceDefinition rd, double remaining, Company player, PlannerSnapshot snapshot = null, Data.LogisticsProvider providerRule = null)
    {
        if (sourceOrbit == null || rd == null || player == null || remaining <= 0)
            return 0;

        var scActive = snapshot?.ScActive ?? new Dictionary<string, int>();
        var carrier = FindBestIdleSpacecraft(sourceOrbit, player, scActive, requireNonContainer: true, out _, snapshot, target, providerRule);
        var capacity = carrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (capacity <= 0)
            return 0;

        return Math.Min(remaining, capacity);
    }

    private static bool HasActiveCycleDelivering(ObjectInfo requester, ResourceDefinition rd, Company player, PlannerSnapshot snapshot = null)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null) return false;

        foreach (var cmd in (snapshot?.Cycles ?? cm.GetAllCycleMission(player)).ToList())
        {
            if (!IsLogisticsMission(cmd)) continue;
            if (cmd.B != requester) continue;
            if (cmd.CheckComplete()) continue;
            if (cmd.cargoAllStart?.Tab == null) continue;

            foreach (var tabRes in cmd.cargoAllStart.Tab)
            {
                if (tabRes == rd)
                {
                    if (IsCycleWaitingOrPlanned(cmd, cm))
                        return true;

                    LogWarning($"CLEANUP stale LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} rd={rd.ID} reason=not waiting and no planned flight");
                    _cyclePlanningFailures.Remove(cmd);
                    RemoveLogisticsCycle(cm, cmd);
                    break;
                }
            }
        }

        if (HasActiveLogisticsMissionDelivering(requester, rd, player, snapshot))
        {
            ClearPendingPlanningDelivery(requester, rd);
            return true;
        }

        return false;
    }

    private static bool HasActiveLogisticsMissionDelivering(ObjectInfo requester, ResourceDefinition rd, Company player, PlannerSnapshot snapshot = null)
    {
        var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;
        var missions = snapshot?.Missions ?? mm?.ListMissionInfo;
        if (missions == null || requester == null || rd == null || player == null)
            return false;

        foreach (var mi in missions)
        {
            if (mi == null || mi.complete || mi.cancel) continue;
            if (mi.company != player) continue;
            if (mi.target != requester) continue;
            if (mi.cargoAll == null) continue;
            if (string.IsNullOrEmpty(mi.missionName) || !mi.missionName.StartsWith("[LOGI]", StringComparison.Ordinal))
                continue;

            var cargoAmount = CargoAmountFor(mi.cargoAll.listCargo, rd)
                + CargoAmountFor(mi.cargoAll.listCargoToOrbit, rd);
            if (cargoAmount <= 0) continue;

            LogVerbose($"REQ active-mission-present: target={requester.ObjectName} rd={rd.ID} mission={mi.id} name=\"{mi.missionName}\" launch={mi.DateLaunch:yyyy-MM-dd} amount={cargoAmount:0.#}");
            return true;
        }

        return false;
    }

    private static double RequestTarget(Data.LogisticsRequest req)
    {
        return Math.Max(0, req?.requestedAmount ?? 0);
    }

    private static double RequestMinimum(Data.LogisticsRequest req)
    {
        if (req == null) return 0;
        if (!req.useMinimumAmount)
            return RequestTarget(req);
        return Math.Max(0, Math.Min(req.minimumAmount, RequestTarget(req)));
    }

    private static double RequestTargetTolerance(Data.LogisticsRequest req)
    {
        if (req == null || !req.useMinimumAmount)
            return 0;

        return Math.Max(0, (RequestTarget(req) - RequestMinimum(req)) * 0.1);
    }

    private static bool IsRequestTargetCovered(Data.LogisticsRequest req, double stock, double inFlight = 0)
    {
        var target = RequestTarget(req);
        return stock + inFlight >= target - RequestTargetTolerance(req);
    }

    private static void CleanupLogisticsCyclesForRequest(ObjectInfo requester, ResourceDefinition rd, Company player, string reason, PlannerSnapshot snapshot = null)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null || requester == null || rd == null || player == null) return;

        ClearPendingPlanningDelivery(requester, rd);
        var reqData = Data.LogisticsNetwork.Get(requester);
        if (reqData != null)
        {
            foreach (var req in reqData.requests.Where(r => r.ResourceDefinition == rd))
                ClearRelayState(req);
        }

        foreach (var cmd in (snapshot?.Cycles ?? cm.GetAllCycleMission(player)).ToList())
        {
            if (!IsLogisticsDeliveryMission(cmd)) continue;
            if (cmd.B != requester) continue;
            if (!CargoContainsResource(cmd.cargoAllStart, rd) && !CargoContainsResource(cmd.cargoAllEnd, rd)) continue;
            ClearReturnStatesForCycle(cmd, requester, rd, player, reason);
            if (ShouldPreserveLandedDeliveryCycle(cmd, requester, rd, player))
            {
                LogVerbose($"CLEANUP preserve-landed LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} rd={rd.ID} reason={reason}");
                continue;
            }

            _cycleCreatedAt.Remove(cmd);
            _cyclePlanningFailures.Remove(cmd);
            LogWarning($"CLEANUP fulfilled LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} rd={rd.ID} reason={reason}");
            RemoveLogisticsCycle(cm, cmd);
        }
    }

    private static void ClearReturnStatesForCycle(CycleMissionsData cmd, ObjectInfo requester,
        ResourceDefinition rd, Company player, string reason)
    {
        if (cmd?.ListSC == null || requester == null || rd == null || player == null)
            return;

        foreach (var sci in cmd.ListSC)
        {
            if (sci is not Spacecraft sc || sc.GetCompany() != player)
                continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state == null)
                continue;
            if (state.Destination != requester || state.Resource != rd)
                continue;

            ResetReturnPlanState(state);
            _returnHomeByShipId.Remove(sc.ID);
            if (VerboseLoggingEnabled)
                LogVerbose($"RETURNHOME clear-owned: ship={sc.GetSpacecraftName()} id={sc.ID} destination={requester.ObjectName} rd={rd.ID} reason={reason}");
        }
    }

    private static bool IsLogisticsDeliveryMission(CycleMissionsData cmd)
    {
        return cmd?.customNameFromPlanMission != null
            && cmd.customNameFromPlanMission.StartsWith("[LOGI]", StringComparison.Ordinal);
    }

    private static bool IsLogisticsReturnMission(CycleMissionsData cmd)
    {
        return cmd?.customNameFromPlanMission != null
            && cmd.customNameFromPlanMission.StartsWith("[LOGI-RETURN]", StringComparison.Ordinal);
    }

    public static string BuildLogisticsMissionName(ObjectInfo from, ObjectInfo to, ResourceDefinition rd, bool isReturn = false, ResourceDefinition backhaulRd = null)
    {
        var prefix = isReturn ? "[LOGI-RETURN]" : "[LOGI]";
        var icon = rd?.IconString;
        var iconPart = string.IsNullOrWhiteSpace(icon) ? string.Empty : $" {icon}";
        var backhaulIcon = backhaulRd?.IconString;
        var backhaulPart = isReturn && !string.IsNullOrWhiteSpace(backhaulIcon) ? $"{backhaulIcon}" : string.Empty;
        return $"{prefix}{iconPart}{backhaulPart} {from?.ObjectName ?? "UNKNOWN"} -> {to?.ObjectName ?? "UNKNOWN"}";
    }

    private static string PendingDeliveryKey(ObjectInfo requester, ResourceDefinition rd)
    {
        return $"{requester?.id ?? -1}:{rd?.ID ?? "null"}";
    }

    private static string BlockedRetryKey(ObjectInfo requester, ResourceDefinition rd)
    {
        return PendingDeliveryKey(requester, rd);
    }

    private static string BuildRequestPlanSignature(ObjectInfo requester, ResourceDefinition rd,
        double requestTarget, double alreadyThere, double inFlight, bool hasActiveDelivery,
        string blockedReturnNote, PlannerSnapshot snapshot)
    {
        // Used for blocked/no-op throttling. Mission and cycle counts are included so stock
        // state changes wake the request even if stock/in-flight amounts have not moved yet.
        var cycleCount = snapshot?.Cycles?.Count ?? -1;
        var missionCount = snapshot?.Missions?.Count ?? -1;
        return $"{requester?.id ?? -1}:{rd?.ID ?? "null"}:" +
               $"target={Math.Round(requestTarget, 1)}:" +
               $"stock={Math.Round(alreadyThere, 1)}:" +
               $"inflight={Math.Round(inFlight, 1)}:" +
               $"active={hasActiveDelivery}:" +
               $"blocked={!string.IsNullOrEmpty(blockedReturnNote)}:" +
               $"cycles={cycleCount}:missions={missionCount}";
    }

    private static bool ShouldDeferRequestPlanning(ObjectInfo requester, ResourceDefinition rd,
        string signature, out string statusNote)
    {
        // This does not throttle successful dispatches. It only suppresses repeated full
        // route scans when the exact same blocked state was already evaluated recently.
        statusNote = null;
        var key = PendingDeliveryKey(requester, rd);
        if (!_requestPlanThrottle.TryGetValue(key, out var state) || state == null)
            return false;

        if (!string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            _requestPlanThrottle.Remove(key);
            return false;
        }

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        if (currentTime >= state.NextEvaluation)
        {
            _requestPlanThrottle.Remove(key);
            return false;
        }

        var days = Math.Max(0.0, (state.NextEvaluation - currentTime).TotalDays);
        statusNote = $"Waiting to re-check logistics options ({days:0.#}d)";
        LogVerbose($"REQ throttle-skip: target={requester?.ObjectName} rd={rd?.ID} next={state.NextEvaluation:yyyy-MM-dd} days={days:0.#}");
        return true;
    }

    private static void MarkRequestPlanningEvaluated(ObjectInfo requester, ResourceDefinition rd, string signature)
    {
        if (requester == null || rd == null || string.IsNullOrEmpty(signature)) return;

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        _requestPlanThrottle[PendingDeliveryKey(requester, rd)] = new RequestPlanThrottleState
        {
            Signature = signature,
            NextEvaluation = currentTime.AddDays(RequestPlanThrottleDays)
        };
    }

    private static void ClearRequestPlanningThrottle(ObjectInfo requester, ResourceDefinition rd)
    {
        _requestPlanThrottle.Remove(PendingDeliveryKey(requester, rd));
    }

    private static bool IsTransientPlanningStatus(string statusNote)
    {
        return !string.IsNullOrEmpty(statusNote)
            && (statusNote.StartsWith("Planning mission", StringComparison.Ordinal)
                || statusNote.StartsWith("Waiting to re-check logistics options", StringComparison.Ordinal));
    }

    private static int ClampPriority(int priority)
    {
        return Math.Max(-1, Math.Min(2, priority));
    }

    private static int GetProviderPriority(ObjectInfo providerOI, ResourceDefinition rd)
    {
        var data = Data.LogisticsNetwork.Get(providerOI);
        if (data?.providers == null || rd == null)
        {
            if (TryGetExportedOrbitProviderParent(providerOI, rd, out var parentProvider))
                return GetProviderPriority(parentProvider, rd);
            return 0;
        }

        var priority = data.providers
            .Where(p => p != null && p.isActive && p.ResourceDefinition == rd)
            .Select(p => ClampPriority(p.priority))
            .DefaultIfEmpty(0)
            .Max();
        if (priority == 0 && TryGetExportedOrbitProviderParent(providerOI, rd, out var exportedParentProvider))
            priority = GetProviderPriority(exportedParentProvider, rd);
        return priority;
    }

    private static int ApplyProviderPriorityToTier(int routeTier, ObjectInfo providerOI, ResourceDefinition rd)
    {
        return routeTier - (GetProviderPriority(providerOI, rd) * ProviderPriorityScoreStep);
    }

    private static string DescribePriorityScore(ObjectInfo providerOI, ResourceDefinition rd)
    {
        var priority = GetProviderPriority(providerOI, rd);
        return priority == 0 ? string.Empty : $";providerPriority={priority};priorityBoost={priority * ProviderPriorityScoreStep}";
    }

    private static bool IsMinimumShipmentStatus(string statusNote)
    {
        return !string.IsNullOrEmpty(statusNote)
            && statusNote.StartsWith("Waiting for minimum ", StringComparison.Ordinal);
    }

    private static void CloseReorderLatchIfTargetCovered(Data.LogisticsRequest req, ObjectInfo requester,
        ResourceDefinition rd, Company player, PlannerSnapshot snapshot)
    {
        // For min/target requests, a planned shipment that would fill to target should close
        // the reorder latch immediately instead of recursively chasing consumption in flight.
        if (req == null || requester == null || rd == null || player == null) return;
        if (!req.useMinimumAmount || !req.reorderActive) return;

        var stock = requester.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
        var inFlight = GetInFlightDeliveryAmount(requester, rd, player, snapshot);
        if (!IsRequestTargetCovered(req, stock, inFlight))
            return;

        req.reorderActive = false;
        LogVerbose($"REQ reorder-close-dispatch: target={requester.ObjectName} rd={rd.ID} stock={stock:0.#} inFlight={inFlight:0.#} fillTarget={RequestTarget(req):0.#} tolerance={RequestTargetTolerance(req):0.#}");
    }

    private static string RoutePlanningLockKey(ObjectInfo source, ObjectInfo target, ResourceDefinition rd, Company player)
    {
        return $"{player?.name ?? "null"}:{source?.id ?? -1}->{target?.id ?? -1}:{rd?.ID ?? "null"}";
    }

    private static bool HasRoutePlanningLock(ObjectInfo source, ObjectInfo target, ResourceDefinition rd,
        Company player, out string statusNote)
    {
        // Route locks cover the short async window after we hand a cycle to stock but before
        // the callback creates a MissionInfo. They are route/resource scoped, not global.
        statusNote = null;
        var key = RoutePlanningLockKey(source, target, rd, player);
        if (!_routePlanningLocks.TryGetValue(key, out var createdAt))
            return false;

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        var ageDays = (currentTime - createdAt).TotalDays;
        if (ageDays < EffectiveCyclePlanningGraceDays)
        {
            statusNote = $"Planning mission for {source?.ObjectName ?? "UNKNOWN"} -> {target?.ObjectName ?? "UNKNOWN"}";
            LogVerbose($"PLAN route-lock-wait: key={key} age={ageDays:0.#}d rd={rd?.ID}");
            return true;
        }

        _routePlanningLocks.Remove(key);
        LogWarning($"PLAN route-lock-stale: key={key} age={ageDays:0.#}d expired after {EffectiveCyclePlanningGraceDays:0.#}d");
        return false;
    }

    private static bool TryAcquireRoutePlanningLock(ObjectInfo source, ObjectInfo target, ResourceDefinition rd,
        Company player, out string routeLockKey)
    {
        routeLockKey = RoutePlanningLockKey(source, target, rd, player);
        if (HasRoutePlanningLock(source, target, rd, player, out _))
            return false;

        _routePlanningLocks[routeLockKey] =
            MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        LogVerbose($"PLAN route-lock-acquire: key={routeLockKey} route={source?.ObjectName}->{target?.ObjectName} rd={rd?.ID}");
        return true;
    }

    private static void ReleaseRoutePlanningLock(string routeLockKey, string reason)
    {
        if (string.IsNullOrWhiteSpace(routeLockKey))
            return;

        if (_routePlanningLocks.Remove(routeLockKey))
            LogVerbose($"PLAN route-lock-release: key={routeLockKey} reason={reason}");
    }

    private static string CommittedStockKey(ObjectInfo source, ResourceDefinition rd)
    {
        return $"{source?.id ?? -1}:{rd?.ID ?? "null"}";
    }

    private static void ResetCommittedStockIfStale()
    {
        // No-op: committed stock is now cleared at the start of each OnDayChange tick.
        // Kept as a method to avoid breaking callers; inlined checks would be dead code.
    }

    private static void CommitStock(ObjectInfo source, ResourceDefinition rd, double amount)
    {
        if (source == null || rd == null || amount <= 0) return;
        // Commit only within a tiny wall-clock window. This prevents same-tick double
        // spending while avoiding stale reservations if stock planning fails later.
        ResetCommittedStockIfStale();
        var key = CommittedStockKey(source, rd);
        _committedStock.TryGetValue(key, out var existing);
        _committedStock[key] = existing + amount;
        _committedStockWallClock = DateTime.UtcNow;
        if (VerboseLoggingEnabled)
            LogVerbose($"STOCK committed: source={source.ObjectName} rd={rd.ID} amount={amount:0.#} totalThisWindow={existing + amount:0.#}");
    }

    private static void DecommitStock(ObjectInfo source, ResourceDefinition rd, double amount)
    {
        if (source == null || rd == null || amount <= 0) return;
        ResetCommittedStockIfStale();
        var key = CommittedStockKey(source, rd);
        if (!_committedStock.TryGetValue(key, out var existing) || existing <= 0) return;
        var newVal = Math.Max(0, existing - amount);
        if (newVal > 0)
            _committedStock[key] = newVal;
        else
            _committedStock.Remove(key);
        if (VerboseLoggingEnabled)
            LogVerbose($"STOCK decommitted: source={source.ObjectName} rd={rd.ID} amount={amount:0.#} was={existing:0.#} now={newVal:0.#}");
    }

    private static void DecommitCycleStock(CycleMissionsData cmd)
    {
        if (cmd?.A == null) return;
        var ends = cmd.EndsResourceCountMaxA;
        if (ends?.listData == null) return;
        foreach (var part in ends.listData)
        {
            if (part?.rd != null && part.count > 0)
                DecommitStock(cmd.A, part.rd, part.count);
        }
    }

    private static double GetCommittedStock(ObjectInfo source, ResourceDefinition rd)
    {
        if (source == null || rd == null) return 0;
        ResetCommittedStockIfStale();
        var key = CommittedStockKey(source, rd);
        _committedStock.TryGetValue(key, out var val);
        return val;
    }

    private static string FormatCooldownStatus(BlockedRetryState state)
    {
        if (state == null) return null;
        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        var days = Math.Max(0, (state.RetryAfter - currentTime).TotalDays);
        var reason = string.IsNullOrWhiteSpace(state.Reason) ? "last attempt was blocked" : state.Reason;
        return $"Retrying in {days:0.#} days: {reason}";
    }

    private static bool HasBlockedPlanningRetryCooldown(ObjectInfo requester, ResourceDefinition rd, out string statusNote)
    {
        // Longer-lived cooldown for truly blocked requests, such as missing LV/fuel/ship.
        // This keeps the daily planner from recreating the same invalid stock cycle forever.
        statusNote = null;
        var key = BlockedRetryKey(requester, rd);
        if (!_blockedPlanningRetries.TryGetValue(key, out var state) || state == null)
            return false;

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        if (currentTime < state.RetryAfter)
        {
            statusNote = FormatCooldownStatus(state);
            LogVerbose($"DISPATCH cooldown: target={requester?.ObjectName} rd={rd?.ID} retryAfter={state.RetryAfter:yyyy-MM-dd} reason={state.Reason}");
            return true;
        }

        _blockedPlanningRetries.Remove(key);
        LogVerbose($"DISPATCH cooldown-expired: target={requester?.ObjectName} rd={rd?.ID}");
        return false;
    }

    private static void MarkBlockedPlanningRetryCooldown(ObjectInfo requester, ResourceDefinition rd, string reason)
    {
        if (requester == null || rd == null)
            return;

        var cooldownDays = Math.Max(0, BlockedMissionRetryCooldownDays);
        if (cooldownDays <= 0)
            return;

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        var retryAfter = currentTime.AddDays(cooldownDays);
        var key = BlockedRetryKey(requester, rd);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "dispatch blocked" : reason;
        if (_blockedPlanningRetries.TryGetValue(key, out var existing)
            && existing != null
            && existing.RetryAfter >= retryAfter
            && existing.Reason == normalizedReason)
        {
            return;
        }

        _blockedPlanningRetries[key] = new BlockedRetryState
        {
            RetryAfter = retryAfter,
            Reason = normalizedReason
        };
        LogWarning($"DISPATCH cooldown-set: target={requester.ObjectName} rd={rd.ID} days={cooldownDays:0.#} reason={normalizedReason}");
    }

    private static void ClearBlockedPlanningRetryCooldown(ObjectInfo requester, ResourceDefinition rd)
    {
        _blockedPlanningRetries.Remove(BlockedRetryKey(requester, rd));
    }

    private static bool ShouldPreserveLandedDeliveryCycle(CycleMissionsData cmd, ObjectInfo requester,
        ResourceDefinition rd, Company player)
    {
        if (cmd?.ListSC == null || requester == null || rd == null || player == null)
            return false;

        foreach (var sci in cmd.ListSC)
        {
            if (sci is not Spacecraft sc || sc.GetCompany() != player)
                continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state == null)
                continue;
            if (state.Destination != requester || state.Resource != rd)
                continue;
            if (sc.CurrentPhase != Spacecraft.EPhase.None)
                continue;
            if (sc.CurrentlyOnThisObject != requester)
                continue;
            return true;
        }

        return false;
    }

    private static bool HasPendingPlanningDelivery(ObjectInfo requester, ResourceDefinition rd)
    {
        // Pending markers bridge "cycle added" to "stock mission visible". They expire into
        // a blocked retry so a lost stock callback cannot freeze the request permanently.
        var key = PendingDeliveryKey(requester, rd);
        if (!_pendingPlanningDeliveries.TryGetValue(key, out var createdAt))
            return false;

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        if ((currentTime - createdAt).TotalDays < EffectiveCyclePlanningGraceDays)
            return true;

        _pendingPlanningDeliveries.Remove(key);
        var reason = $"pending plan stale after {EffectiveCyclePlanningGraceDays:0.#} days";
        LogWarning($"PENDING stale: target={requester?.ObjectName} rd={rd?.ID} expired after {EffectiveCyclePlanningGraceDays:0.#} days");
        MarkBlockedPlanningRetryCooldown(requester, rd, reason);
        return false;
    }

    private static void MarkPendingPlanningDelivery(ObjectInfo requester, ResourceDefinition rd)
    {
        if (requester == null || rd == null) return;
        // Once a new plan is pending, clear older blocked/throttle state for this request.
        ClearBlockedPlanningRetryCooldown(requester, rd);
        var key = PendingDeliveryKey(requester, rd);
        _requestPlanThrottle.Remove(key);
        _pendingPlanningDeliveries[key] =
            MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
    }

    private static void ClearPendingPlanningDelivery(ObjectInfo requester, ResourceDefinition rd)
    {
        if (requester == null || rd == null) return;
        var key = PendingDeliveryKey(requester, rd);
        _pendingPlanningDeliveries.Remove(key);
        _requestPlanThrottle.Remove(key);
    }

    private static bool IsCyclePastPlanningGrace(CycleMissionsData cmd)
    {
        if (cmd == null || !_cycleCreatedAt.TryGetValue(cmd, out var createdAt))
            return false;

        var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        return (currentTime - createdAt).TotalDays >= EffectiveCyclePlanningGraceDays;
    }

    private static bool HasCycleActuallyLaunched(Spacecraft sc, CycleMissionsData cmd, CycleMissionManager cm)
    {
        if (sc == null || cmd == null)
            return false;
        if (sc.CurrentPhase != Spacecraft.EPhase.None)
            return true;
        if (cmd.wasSetPMParameterForCodeJobSystem)
            return true;

        var ctrl = sc.gameObject.GetComponent<SpaceCraftCyclicalMissionController>();
        return ctrl != null && ctrl.CycleMissionPlanFlyWas;
    }

    private static double GetReturnRetryCooldownDays(ReturnHomeState state)
    {
        if (state != null && state.ConsecutiveReturnCycleFailures > ReturnCycleEscalationFailureThreshold)
            return ReturnCycleEscalatedCooldownDays;
        return ReturnCycleBlockedCooldownDays;
    }

    private static void SetReturnRetryCooldown(ReturnHomeState state, Spacecraft sc, ObjectInfo current, ObjectInfo home, string reason)
    {
        if (state == null)
            return;

        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        state.ConsecutiveReturnCycleFailures++;
        var cooldownDays = GetReturnRetryCooldownDays(state);
        state.ReturnRetryAfter = now.AddDays(cooldownDays);
        state.ReturnRetryWallClockAfterUtc = DateTime.UtcNow.Add(ReturnCycleWallClockThrottle);
        state.LastBlockedReason = reason;
        state.LastBlockedStatusNote = LogisticsStrings.ReturnRetryCooldown(cooldownDays);
        state.LastBlockedDate = now.Date;
        LogWarning($"RETURNHOME cooldown-set: ship={sc?.GetSpacecraftName() ?? "null"} id={sc?.ID ?? -1} current={current?.ObjectName ?? "null"} home={home?.ObjectName ?? "null"} days={cooldownDays:0.#} failures={state.ConsecutiveReturnCycleFailures} reason={reason}");
    }

    private static void MarkReturnAttemptCooldown(ReturnHomeState state, Spacecraft sc, ObjectInfo current, ObjectInfo home, string reason)
    {
        if (state == null)
            return;

        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        state.ReturnRetryAfter = now.AddDays(ReturnCycleBlockedCooldownDays);
        state.ReturnRetryWallClockAfterUtc = DateTime.UtcNow.Add(ReturnCycleWallClockThrottle);
        state.LastBlockedStatusNote = LogisticsStrings.AwaitingReturnFrom(current);
        LogVerbose($"RETURNHOME attempt-cooldown: ship={sc?.GetSpacecraftName() ?? "null"} id={sc?.ID ?? -1} current={current?.ObjectName ?? "null"} home={home?.ObjectName ?? "null"} days={ReturnCycleBlockedCooldownDays:0.#} reason={reason}");
    }

    private static bool IsReturnRetryCoolingDown(ReturnHomeState state, out string statusNote)
    {
        statusNote = null;
        if (state == null)
            return false;

        var nowGame = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        var nowReal = DateTime.UtcNow;
        var gameRemaining = Math.Max(0, (state.ReturnRetryAfter - nowGame).TotalDays);
        var realRemaining = Math.Max(0, (state.ReturnRetryWallClockAfterUtc - nowReal).TotalSeconds);
        if (gameRemaining <= 0 && realRemaining <= 0)
            return false;

        statusNote = gameRemaining > 0
            ? LogisticsStrings.ReturnRetryCooldown(gameRemaining)
            : $"Return launch blocked; retrying shortly ({realRemaining:0.#}s)";
        return true;
    }

    private static bool IsCycleWaitingOrPlanned(CycleMissionsData cmd, CycleMissionManager cm)
    {
        if (cmd == null || cm == null) return false;
        var withinGrace = false;
        if (_cycleCreatedAt.TryGetValue(cmd, out var createdAt))
        {
            var currentTime = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
            if ((currentTime - createdAt).TotalDays < EffectiveCyclePlanningGraceDays)
                withinGrace = true;
            else
                _cycleCreatedAt.Remove(cmd);
        }

        var hasEverFlown = false;
        var sawListedSpacecraft = false;
        if (cmd.ListSC != null)
        {
            foreach (var sci in cmd.ListSC)
            {
                if (sci is not Spacecraft sc)
                    continue;

                sawListedSpacecraft = true;
                if (cm.GetCycleMission(sc) != cmd)
                    continue;

                var ctrl = sc.gameObject.GetComponent<SpaceCraftCyclicalMissionController>();
                if (ctrl != null && ctrl.CycleMissionPlanFlyWas)
                {
                    _cycleCreatedAt.Remove(cmd);
                    foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                        ClearPendingPlanningDelivery(cmd.B, tabRes);
                    return true;
                }

                if (_returnHomeByShipId.TryGetValue(sc.ID, out var returnState)
                    && returnState != null
                    && !returnState.HasLeftHome)
                {
                    return true;
                }
            }
        }

        if (sawListedSpacecraft)
        {
            if (withinGrace)
                return true;

            if (cmd.wasSetPMParameterForCodeJobSystem)
            {
                _cyclePlanningFailures.TryGetValue(cmd, out var listedFailures);
                _cyclePlanningFailures[cmd] = listedFailures + 1;
                if (listedFailures + 1 >= MaxCyclePlanningFailures)
                {
                    LogWarning($"CLEANUP stuck-planning LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} name={cmd.customNameFromPlanMission} failures={listedFailures + 1} (job system active but listed ship never flew)");
                    _cyclePlanningFailures.Remove(cmd);
                    _cycleCreatedAt.Remove(cmd);
                    return false;
                }
                return true;
            }

            return false;
        }

        // Fallback for malformed/old cycles with no usable ListSC entry. Normal logistics
        // cycles should not reach this path, because scanning all spacecraft is expensive.
        var now = Time.unscaledTime;
        if (_cachedSpacecraft == null || now - _cachedSpacecraftTime > 0.5f)
        {
            _cachedSpacecraft = UnityEngine.Object.FindObjectsOfType<Spacecraft>();
            _cachedSpacecraftTime = now;
        }
        foreach (var sc in _cachedSpacecraft)
        {
            if (sc == null) continue;
            if (cm.GetCycleMission(sc) != cmd) continue;

            var ctrl = sc.gameObject.GetComponent<SpaceCraftCyclicalMissionController>();
            if (ctrl != null && ctrl.CycleMissionPlanFlyWas)
            {
                hasEverFlown = true;
                _cycleCreatedAt.Remove(cmd);
                foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                    ClearPendingPlanningDelivery(cmd.B, tabRes);
                return true;
            }

            if (_returnHomeByShipId.TryGetValue(sc.ID, out var returnState)
                && returnState != null
                && !returnState.HasLeftHome)
            {
                return true;
            }
        }

        if (withinGrace)
            return true;

        if (cmd.wasSetPMParameterForCodeJobSystem && !hasEverFlown)
        {
            _cyclePlanningFailures.TryGetValue(cmd, out var failures);
            _cyclePlanningFailures[cmd] = failures + 1;
            if (failures + 1 >= MaxCyclePlanningFailures)
            {
                LogWarning($"CLEANUP stuck-planning LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} name={cmd.customNameFromPlanMission} failures={failures + 1} (job system active but ship never flew)");
                _cyclePlanningFailures.Remove(cmd);
                _cycleCreatedAt.Remove(cmd);
                return false;
            }
            return true;
        }

        return false;
    }

    private static double GetInFlightDeliveryAmount(ObjectInfo requester, ResourceDefinition rd, Company player, PlannerSnapshot snapshot = null)
    {
        var indexedKey = TargetResourceKey(requester, rd);
        if (indexedKey != null && snapshot?.InFlightCargoByTargetAndResource != null)
        {
            return snapshot.InFlightCargoByTargetAndResource.TryGetValue(indexedKey, out var indexedAmount)
                ? indexedAmount
                : 0;
        }

        var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;
        var missions = snapshot?.Missions ?? mm?.ListMissionInfo;
        if (missions == null || requester == null || rd == null || player == null)
            return 0;

        double result = 0;
        foreach (var mi in missions)
        {
            if (mi == null || mi.complete || mi.cancel) continue;
            if (mi.company != player) continue;
            if (mi.target != requester) continue;
            if (mi.cargoAll == null) continue;

            result += CargoAmountFor(mi.cargoAll.listCargo, rd);
            result += CargoAmountFor(mi.cargoAll.listCargoToOrbit, rd);
        }

        return result;
    }

    private static double CargoAmountFor(IEnumerable<Cargo> cargoList, ResourceDefinition rd)
    {
        if (cargoList == null || rd == null) return 0;
        return cargoList
            .Where(c => c != null
                && c.resourceTypeType == EResourceTypeType.resorces
                && c.resourceType == rd)
            .Sum(c => c.cargoMass);
    }

    public static void GetActiveCycleCounts(Company player,
        out Dictionary<string, int> scActive, out Dictionary<string, int> lvActive)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        var cycles = cm?.GetAllCycleMission(player);
        CountActiveLogisticsCycles(player, cycles, out scActive, out lvActive, out _);
    }

    private static void CountActiveLogisticsCycles(Company player,
        IEnumerable<CycleMissionsData> cycles,
        out Dictionary<string, int> scActive, out Dictionary<string, int> lvActive,
        out HashSet<int> committedShipIds)
    {
        scActive = new Dictionary<string, int>();
        lvActive = new Dictionary<string, int>();
        committedShipIds = new HashSet<int>();
        if (cycles == null) return;

        foreach (var cmd in cycles)
        {
            if (cmd == null || cmd.CheckComplete()) continue;
            if (!IsLogisticsMission(cmd)) continue;
            if (cmd.ListSC == null) continue;

            foreach (var sci in cmd.ListSC)
            {
                var sc = sci as Spacecraft;
                if (sc == null || sc.spacecraftType == null) continue;
                var tn = Data.LogisticsNetwork.TypeKey(sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC");
                if (!scActive.ContainsKey(tn)) scActive[tn] = 0;
                scActive[tn]++;
                committedShipIds.Add(sc.ID);
            }

            if (cmd.LvTypeA != null)
            {
                var tn = Data.LogisticsNetwork.TypeKey(cmd.LvTypeA.ID, cmd.LvTypeA.Name ?? "LV");
                if (!lvActive.ContainsKey(tn)) lvActive[tn] = 0;
                lvActive[tn]++;
            }
            if (cmd.LvTypeB != null)
            {
                var tn = Data.LogisticsNetwork.TypeKey(cmd.LvTypeB.ID, cmd.LvTypeB.Name ?? "LV");
                if (!lvActive.ContainsKey(tn)) lvActive[tn] = 0;
                lvActive[tn]++;
            }
        }
    }

    private static void RecordDispatchInSnapshot(PlannerSnapshot snapshot, Spacecraft sc, LaunchVehicleType lvType)
    {
        if (snapshot == null) return;
        if (sc?.spacecraftType != null)
        {
            var tn = Data.LogisticsNetwork.TypeKey(sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC");
            if (!snapshot.ScActive.ContainsKey(tn)) snapshot.ScActive[tn] = 0;
            snapshot.ScActive[tn]++;
            snapshot.CommittedShipIds.Add(sc.ID);
        }
        if (lvType != null)
        {
            var tn = Data.LogisticsNetwork.TypeKey(lvType.ID, lvType.Name ?? "LV");
            if (!snapshot.LvActive.ContainsKey(tn)) snapshot.LvActive[tn] = 0;
            snapshot.LvActive[tn]++;
            var origin = sc?.CurrentlyOnThisObject;
            if (origin != null)
                IncrementActiveLaunchVehicleUse(snapshot, origin, lvType);
        }
    }

    private static void RebuildActiveLaunchVehicleUseIndex(Company player, PlannerSnapshot snapshot)
    {
        if (player == null || snapshot?.Cycles == null)
            return;

        snapshot.ActiveLvUsesByOriginAndType.Clear();
        foreach (var cmd in snapshot.Cycles)
        {
            if (cmd == null || cmd.CheckComplete()) continue;
            if (!IsLogisticsMission(cmd)) continue;

            if (cmd.A != null && cmd.LvTypeA != null)
                IncrementActiveLaunchVehicleUse(snapshot, cmd.A, cmd.LvTypeA);
            if (cmd.B != null && cmd.LvTypeB != null)
                IncrementActiveLaunchVehicleUse(snapshot, cmd.B, cmd.LvTypeB);
        }
    }

    private static void IncrementActiveLaunchVehicleUse(PlannerSnapshot snapshot, ObjectInfo origin, LaunchVehicleType lvType)
    {
        var key = ActiveLaunchVehicleUseKey(origin, lvType);
        if (snapshot == null || key == null)
            return;
        if (!snapshot.ActiveLvUsesByOriginAndType.ContainsKey(key))
            snapshot.ActiveLvUsesByOriginAndType[key] = 0;
        snapshot.ActiveLvUsesByOriginAndType[key]++;
    }

    private static string ActiveLaunchVehicleUseKey(ObjectInfo origin, LaunchVehicleType lvType)
    {
        if (origin == null || lvType == null)
            return null;
        return $"{origin.id}|{Data.LogisticsNetwork.TypeKey(lvType.ID, lvType.Name ?? "LV")}";
    }

    private static bool IsLogisticsMission(CycleMissionsData cmd)
    {
        return cmd?.customNameFromPlanMission != null
            && cmd.customNameFromPlanMission.StartsWith("[LOGI", StringComparison.Ordinal);
    }

    public static void RegisterLogisticsCycleName(CycleMissionsData cmd)
    {
        if (!IsLogisticsMission(cmd)) return;

        // Stock callbacks sometimes lose direct access to the CycleMissionsData by the time
        // a MissionInfo is created. Cache both ship and route lookups as fallbacks for naming.
        var name = cmd.customNameFromPlanMission;
        if (cmd.ListSC != null)
        {
            foreach (var sci in cmd.ListSC)
            {
                if (sci is Spacecraft sc && sc.ID >= 0)
                    _cycleNameByShipId[sc.ID] = name;
            }
        }

        var routeKey = MakeCycleRouteKey(cmd.A, cmd.B, cmd.Company);
        if (routeKey != null)
            _cycleNameByRouteKey[routeKey] = name;
    }

    public static void UnregisterLogisticsCycleName(CycleMissionsData cmd)
    {
        if (!IsLogisticsMission(cmd)) return;

        if (cmd.ListSC != null)
        {
            foreach (var sci in cmd.ListSC)
            {
                if (sci is Spacecraft sc && sc.ID >= 0)
                    _cycleNameByShipId.Remove(sc.ID);
            }
        }

        var routeKey = MakeCycleRouteKey(cmd.A, cmd.B, cmd.Company);
        if (routeKey != null)
            _cycleNameByRouteKey.Remove(routeKey);
    }

    public static void RemoveLogisticsCycle(CycleMissionManager cm, CycleMissionsData cmd)
    {
        if (cm == null || cmd == null) return;
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(cmd);
        if (!string.IsNullOrEmpty(dispatchId) && SolarSdk.Fleet.HasSyntheticCarrier(dispatchId))
            SolarSdk.Fleet.ReleaseSyntheticCarrier(dispatchId, SdkReservationOwner);
        SolarSdk.CyclicalMissions.UnregisterCycle(cmd, "remove-logistics-cycle");
        if (cmd.ListSC != null)
        {
            foreach (var sci in cmd.ListSC)
            {
                if (sci is Spacecraft sc && sc.ID >= 0 && SolarSdk.Fleet.IsReserved(sc.ID))
                    SolarSdk.Fleet.ReleaseSpacecraft(sc.ID, SdkReservationOwner);
            }
        }
        UnregisterLogisticsCycleName(cmd);
        cm.RemoveCycleMission(cmd);
    }

    private static string RegisterSdkCycle(CycleMissionsData cmd, Spacecraft primaryShip, string context)
    {
        if (cmd == null)
            return null;

        var dispatchId = SolarSdk.CyclicalMissions.CreateDispatchId(SdkOwnerTag);
        var routeSummary = $"{cmd.A?.ObjectName ?? "null"}->{cmd.B?.ObjectName ?? "null"}";
        SolarSdk.CyclicalMissions.RegisterPlannedCycle(dispatchId, SdkOwnerTag, cmd, primaryShip, routeSummary);
        if (primaryShip?.ID >= 0)
            SolarSdk.Fleet.ReserveSpacecraft(primaryShip.ID, SdkReservationOwner, context, dispatchId, cmd.A?.id ?? -1, cmd.B?.id ?? -1);
        else if (primaryShip != null)
            SolarSdk.Fleet.TrackSyntheticCarrier(dispatchId, SdkReservationOwner, primaryShip, context, cmd.A?.id ?? -1, cmd.B?.id ?? -1);
        LogVerbose($"SDK-DISPATCH registered id={dispatchId} context={context} route={routeSummary} ship={primaryShip?.GetSpacecraftName() ?? "null"}#{primaryShip?.ID ?? -1}");
        return dispatchId;
    }

    private static bool ValidateSdkDispatchBoundary(string context, Company company, ObjectInfo source, ObjectInfo target,
        Spacecraft carrier, CargoAll cargoAll, bool allowSyntheticCarrier, out string failure)
    {
        failure = null;
        var draft = SolarSdk.Missions.CreateDraft(SdkReservationOwner);
        draft.Company = company;
        draft.Start = source;
        draft.Target = target;
        draft.Spacecraft = carrier;
        draft.CargoAll = cargoAll;
        draft.MissionName = $"[LOGI] {source?.ObjectName ?? "null"} -> {target?.ObjectName ?? "null"}";
        draft.AllowSyntheticCarrier = allowSyntheticCarrier;
        draft.ForCyclicalMission = true;

        var validation = SolarSdk.Missions.Validate(draft, new SdkMissionValidationOptions
        {
            RunStockValidation = false
        });

        var issues = validation.Issues
            .Where(i => i.Kind != SdkMissionFailureKind.None)
            .Select(i => $"{i.Kind}:{i.Message}")
            .ToList();

        var capacity = carrier?.spacecraftType?.GetCargoCapacity(company) ?? 0.0;
        if (cargoAll != null && capacity > 0.0 && cargoAll.CargoCurrent > capacity + 0.001)
            issues.Add($"CargoOverLimit:manifest {cargoAll.CargoCurrent:0.#} exceeds carrier capacity {capacity:0.#}");

        if (issues.Count == 0)
        {
            LogVerbose($"SDK-VALIDATION ok: context={context} route={source?.ObjectName ?? "null"}->{target?.ObjectName ?? "null"} carrier={carrier?.GetSpacecraftName() ?? "null"}#{carrier?.ID ?? -1} manifest={FormatCargo(cargoAll)}");
            return true;
        }

        failure = string.Join("; ", issues);
        LogWarning($"SDK-VALIDATION blocked: context={context} route={source?.ObjectName ?? "null"}->{target?.ObjectName ?? "null"} carrier={carrier?.GetSpacecraftName() ?? "null"}#{carrier?.ID ?? -1} issues={failure} manifest={FormatCargo(cargoAll)}");
        SolarSdk.Diagnostics.WriteSnapshotOnce("logistics-dispatch-validation", $"{context}:{source?.id ?? -1}->{target?.id ?? -1}:{carrier?.ID ?? -1}");
        return false;
    }

    private static string MakeCycleRouteKey(ObjectInfo a, ObjectInfo b, Company company)
    {
        if (a == null || b == null || company == null) return null;
        var first = Math.Min(a.id, b.id);
        var second = Math.Max(a.id, b.id);
        return $"{company.ID}|{first}|{second}";
    }

    private static string DescribeSpacecraft(Spacecraft sc)
    {
        if (sc == null) return "null";
        return $"{sc.GetSpacecraftName() ?? sc.spacecraftName ?? sc.spacecraftType?.NameRocketType ?? "SC"}#{sc.ID}";
    }

    private static bool IsSameSpacecraftIdentity(Spacecraft a, Spacecraft b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        return a.ID >= 0 && b.ID >= 0 && a.ID == b.ID;
    }

    private static bool IsReservedForLogisticsReturn(Spacecraft sc)
    {
        if (sc == null || sc.ID < 0) return false;
        if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state == null)
            return false;

        // Once logistics assigns a ship to an outbound delivery, keep it owned until the
        // return-home state is explicitly cleared. Stock can briefly detach failed cycles
        // while the ship is still visible at home; treating that ship as available here
        // causes duplicate outbound/return cycles.
        return true;
    }

    private static bool IsSpacecraftAlreadyCommitted(Spacecraft sc, Company player, out string reason,
        bool includeReturnReservation = true, HashSet<int> committedShipIds = null)
    {
        reason = null;
        if (sc == null)
        {
            reason = "ship is null";
            return true;
        }

        if (sc.spacecraftType == null)
        {
            reason = $"{DescribeSpacecraft(sc)} has no spacecraft type";
            return true;
        }

        if (player != null && sc.GetCompany() != player)
        {
            reason = $"{DescribeSpacecraft(sc)} is not owned by player";
            return true;
        }

        if (sc.CurrentPhase != Spacecraft.EPhase.None)
        {
            reason = $"{DescribeSpacecraft(sc)} phase={sc.CurrentPhase}";
            return true;
        }

        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        var attached = cm?.GetCycleMission(sc);
        if (attached != null && !attached.CheckComplete())
        {
            reason = $"{DescribeSpacecraft(sc)} already has cycle {attached.customNameFromPlanMission ?? "unnamed"}";
            return true;
        }

        var controllerCycle = sc.CraftCyclicalMissionController?.CycleMissionsData;
        if (controllerCycle != null && !controllerCycle.CheckComplete())
        {
            reason = $"{DescribeSpacecraft(sc)} controller already has cycle {controllerCycle.customNameFromPlanMission ?? "unnamed"}";
            return true;
        }

        if (includeReturnReservation && IsReservedForLogisticsReturn(sc))
        {
            reason = $"{DescribeSpacecraft(sc)} is reserved for logistics return";
            return true;
        }

        // Use pre-built committed set when available (O(1) lookup),
        // fall back to full cycle scan otherwise.
        if (committedShipIds != null)
        {
            if (sc.ID >= 0 && committedShipIds.Contains(sc.ID))
            {
                reason = $"{DescribeSpacecraft(sc)} identity in committed-ship set";
                return true;
            }
        }
        else if (cm != null && player != null)
        {
            foreach (var cmd in cm.GetAllCycleMission(player))
            {
                if (cmd == null || cmd.CheckComplete() || cmd.ListSC == null)
                    continue;

                foreach (var sci in cmd.ListSC)
                {
                    if (sci is not Spacecraft other || !IsSameSpacecraftIdentity(sc, other))
                        continue;

                    reason = $"{DescribeSpacecraft(sc)} identity already appears in active cycle {cmd.customNameFromPlanMission ?? "unnamed"} as {DescribeSpacecraft(other)}";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSpacecraftAvailableForLogistics(Spacecraft sc, Company player, HashSet<int> committedShipIds = null)
    {
        return !IsSpacecraftAlreadyCommitted(sc, player, out _, committedShipIds: committedShipIds);
    }

    private static bool ValidateSpacecraftForCycleCreation(Spacecraft sc, Company player, string context)
    {
        if (!IsSpacecraftAlreadyCommitted(sc, player, out var reason))
            return true;

        LogWarning($"SKIP cycle: spacecraft already in use context={context} reason={reason}");
        return false;
    }

    private static bool ValidateSpacecraftForReturnCycleCreation(Spacecraft sc, Company player, string context)
    {
        if (!IsSpacecraftAlreadyCommitted(sc, player, out var reason, includeReturnReservation: false))
            return true;

        LogWarning($"SKIP cycle: spacecraft already in use context={context} reason={reason}");
        return false;
    }

    public static bool IsLogisticsPlan(PMMissionParameter pmp)
    {
        return !string.IsNullOrEmpty(FindLogisticsCycleName(pmp));
    }

    public static string FindLogisticsCycleName(PMMissionParameter pmp)
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (pmp == null || player == null || cm == null)
            return null;

        if (pmp.FlyCompany != null && pmp.FlyCompany != player)
            return null;

        if (pmp.SC is Spacecraft pmpSc)
        {
            if (pmpSc.ID >= 0 && _cycleNameByShipId.TryGetValue(pmpSc.ID, out var cachedShipName))
                return cachedShipName;

            var scCmd = cm.GetCycleMission(pmpSc);
            if (scCmd != null && IsLogisticsMission(scCmd) && !string.IsNullOrEmpty(scCmd.customNameFromPlanMission))
            {
                RegisterLogisticsCycleName(scCmd);
                return scCmd.customNameFromPlanMission;
            }
        }

        if (pmp.Start != null && pmp.Target != null)
        {
            var routeKey = MakeCycleRouteKey(pmp.Start, pmp.Target, player);
            if (routeKey != null && _cycleNameByRouteKey.TryGetValue(routeKey, out var cachedRouteName))
                return cachedRouteName;

            var allCycles = cm.GetAllCycleMission(player);
            foreach (var cmd in allCycles)
            {
                if (!IsLogisticsMission(cmd)) continue;
                if (string.IsNullOrEmpty(cmd.customNameFromPlanMission)) continue;

                var sameDirection = cmd.A == pmp.Start && cmd.B == pmp.Target;
                var reverseDirection = cmd.B == pmp.Start && cmd.A == pmp.Target;
                if (sameDirection || reverseDirection)
                {
                    RegisterLogisticsCycleName(cmd);
                    return cmd.customNameFromPlanMission;
                }
            }
        }

        return null;
    }

    public static string FindLogisticsCycleName(ISpacecraftInfo spacecraftInfo, IEnumerable<ISpacecraftInfo> spacecraftInfos)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null)
            return null;

        if (spacecraftInfo is Spacecraft sc)
        {
            var cmd = cm.GetCycleMission(sc);
            if (cmd != null && IsLogisticsMission(cmd) && !string.IsNullOrEmpty(cmd.customNameFromPlanMission))
            {
                RegisterLogisticsCycleName(cmd);
                return cmd.customNameFromPlanMission;
            }
        }

        if (spacecraftInfos == null)
            return null;

        foreach (var sci in spacecraftInfos)
        {
            if (sci is not Spacecraft listShip)
                continue;

            var cmd = cm.GetCycleMission(listShip);
            if (cmd != null && IsLogisticsMission(cmd) && !string.IsNullOrEmpty(cmd.customNameFromPlanMission))
            {
                RegisterLogisticsCycleName(cmd);
                return cmd.customNameFromPlanMission;
            }
        }

        return null;
    }

    public static string FindLogisticsCycleName(ObjectInfo start, ObjectInfo target, Company company,
        IEnumerable<ISpacecraftInfo> spacecraftInfos, CargoAll cargoAll)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (start == null || target == null || company == null || cm == null) return null;

        var routeKey = MakeCycleRouteKey(start, target, company);
        if (routeKey != null && _cycleNameByRouteKey.TryGetValue(routeKey, out var cachedRouteName))
            return cachedRouteName;

        var spacecraftSet = new HashSet<ISpacecraftInfo>();
        if (spacecraftInfos != null)
        {
            foreach (var sci in spacecraftInfos)
            {
                if (sci == null) continue;
                spacecraftSet.Add(sci);
                if (sci is Spacecraft sc && sc.ID >= 0 && _cycleNameByShipId.TryGetValue(sc.ID, out var cachedShipName))
                    return cachedShipName;
            }
        }

        foreach (var cmd in cm.GetAllCycleMission(company))
        {
            if (!IsLogisticsMission(cmd)) continue;
            if (string.IsNullOrEmpty(cmd.customNameFromPlanMission)) continue;

            if (spacecraftSet.Count > 0 && cmd.ListSC != null)
            {
                foreach (var sci in cmd.ListSC)
                {
                    if (sci != null && spacecraftSet.Contains(sci))
                    {
                        RegisterLogisticsCycleName(cmd);
                        return cmd.customNameFromPlanMission;
                    }
                }
            }

            var sameDirection = cmd.A == start && cmd.B == target;
            var reverseDirection = cmd.B == start && cmd.A == target;
            if (!sameDirection && !reverseDirection) continue;

            if (cargoAll == null || CargoOverlaps(cmd.cargoAllStart, cargoAll) || CargoOverlaps(cmd.cargoAllEnd, cargoAll))
            {
                RegisterLogisticsCycleName(cmd);
                return cmd.customNameFromPlanMission;
            }
        }

        return null;
    }

    private static bool CargoOverlaps(InfoCargoCyclicalMission cycleCargo, CargoAll missionCargo)
    {
        if (cycleCargo?.Tab == null || missionCargo == null) return false;
        foreach (var rd in cycleCargo.Tab)
        {
            if (rd == null) continue;
            if (CargoContainsResource(missionCargo, rd))
                return true;
        }
        return false;
    }

    public static void CapLogisticsCargoForPlannerLimits(PMMissionParameter pmp)
    {
        using (TimeScope($"CapLogisticsCargoForPlannerLimits {pmp?.Start?.ObjectName ?? "null"}->{pmp?.Target?.ObjectName ?? "null"}"))
        {
        if (!IsLogisticsPlan(pmp) || pmp.CargoAll == null) return;
        if (CanSkipPlannerCapCheckForSimpleLocLaunch(pmp))
            return;

        var result = pmp.CheckCanPlanMission().planMissionResult;
        if (ApplySmallReservePropellant(pmp))
            result = pmp.CheckCanPlanMission().planMissionResult;

        if (VerboseLoggingEnabled)
        {
            var cargoStart = pmp.CargoAll.CargoCurrent;
            var capacity = (pmp.SC?.GetTypeSpaceCraft()?.GetCargoCapacity(pmp.FlyCompany) ?? 0) * Math.Max(1, pmp.SCCount);
            LogVerbose($"LOGI-CAP before: {pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} result={result} cargo={cargoStart:0.#}/{capacity:0.#} propellant={pmp.CargoAll?.cargoFuel?.cargoMassPotencjal:0.#} sc={pmp.SC?.GetSpacecraftName()} scType={pmp.SC?.GetTypeSpaceCraft()?.NameRocketType} lv={pmp.LV?.GetLaunchVehicleType()?.Name} manifest={FormatCargo(pmp.CargoAll)}");
        }
        if (result == PMMissionParameter.EPlanMissionResult.AllOk) return;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV) && pmp.LV == null)
        {
            LogWarning($"PLAN invalid: {pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} needs an LV but none was assigned; leaving cargo unchanged");
            return;
        }

        var limitingFailure =
            result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongThrust)
            || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongMaxCapacityFuelOk)
            || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV)
            || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongResourcesCargoLoadLimit);

        if (!limitingFailure) return;

        var cargoItems = GetResourceCargoItems(pmp.CargoAll);
        if (cargoItems.Count == 0) return;

        var original = cargoItems.Select(c => c.cargoMass).ToArray();
        var originalTotal = original.Sum();
        if (originalTotal <= 0) return;

        double bestScale = -1;
        double low = 0;
        double high = 1;
        var bestResult = result;

        for (var i = 0; i < 6; i++)
        {
            var scale = (low + high) / 2;
            ApplyCargoScale(cargoItems, original, scale);

            var check = pmp.CheckCanPlanMission().planMissionResult;
            if (check == PMMissionParameter.EPlanMissionResult.AllOk)
            {
                bestScale = scale;
                bestResult = check;
                low = scale;
            }
            else
            {
                high = scale;
            }
        }

        if (bestScale >= 0)
        {
            ApplyCargoScale(cargoItems, original, bestScale);
            if (VerboseLoggingEnabled)
            {
                var cappedTotal = cargoItems.Sum(c => c.cargoMass);
                var capacity = (pmp.SC?.GetTypeSpaceCraft()?.GetCargoCapacity(pmp.FlyCompany) ?? 0) * Math.Max(1, pmp.SCCount);
                LogVerbose($"LOGI-CAP scaled: {pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} cargo={originalTotal:0.#}->{cappedTotal:0.#}/{capacity:0.#} scale={bestScale:0.###} dueTo={result} after={bestResult} manifest={FormatCargo(pmp.CargoAll)}");
            }
        }
        else
        {
            ApplyCargoScale(cargoItems, original, 0);
            var failureReason = TranslatePlanMissionResult(result) ?? $"Mission blocked ({result})";
            LogWarning($"CAP planner cargo: no valid cargo amount found for {pmp.Start?.ObjectName} -> {pmp.Target?.ObjectName}; original={originalTotal:0.#}, result={result} - aborting cycle");
            AbortLogisticsCycle(pmp, failureReason);
        }
        }
    }

    private static bool CanSkipPlannerCapCheckForSimpleLocLaunch(PMMissionParameter pmp)
    {
        if (pmp == null || pmp.CargoAll == null || pmp.SC == null || pmp.Start == null || pmp.Target == null || pmp.FlyCompany == null)
            return false;

        var scType = pmp.SC.GetTypeSpaceCraft();
        if (scType?.LowOrbitContainer != true)
            return false;
        if (pmp.LV == null)
            return false;
        if (!pmp.Start.NeedVehicleToLaunch() || !IsOrbitOf(pmp.Target, pmp.Start))
            return false;

        var capacity = scType.GetCargoCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        if (pmp.CargoAll.CargoCurrent > capacity + 0.001)
            return false;

        try
        {
            if (!pmp.LV.CheckMaximumPayload(pmp.CargoAll, pmp.SC))
                return false;
        }
        catch
        {
            return false;
        }

        LogVerbose($"LOGI-CAP skip-simple-loc: {pmp.Start.ObjectName}->{pmp.Target.ObjectName} cargo={pmp.CargoAll.CargoCurrent:0.#}/{capacity:0.#} lv={pmp.LV.GetLaunchVehicleType()?.Name ?? "none"}");
        return true;
    }

    private static void AbortLogisticsCycle(PMMissionParameter pmp, string failureReason = null)
    {
        var player = pmp?.FlyCompany ?? MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (pmp == null || player == null || cm == null) return;

        foreach (var cmd in cm.GetAllCycleMission(player).ToList())
        {
            if (!IsLogisticsMission(cmd)) continue;
            var sameDirection = cmd.A == pmp.Start && cmd.B == pmp.Target;
            var reverseDirection = cmd.B == pmp.Start && cmd.A == pmp.Target;
            if (!sameDirection && !reverseDirection) continue;

            var reason = failureReason ?? "Ship cannot carry any payload on this route";

            if (cmd.ListSC != null)
            {
                foreach (var sci in cmd.ListSC)
                {
                    if (sci is Spacecraft sc && sc.GetCompany() == player)
                    {
                        if (_returnHomeByShipId.TryGetValue(sc.ID, out var state) && state != null)
                        {
                            state.LastBlockedReason = reason;
                            state.LastBlockedStatusNote = reason;
                        }
                        else
                        {
                            _returnHomeByShipId.Remove(sc.ID);
                        }
                    }
                }
            }

            foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                ClearPendingPlanningDelivery(cmd.B, tabRes);

            DecommitCycleStock(cmd);
            _cycleCreatedAt.Remove(cmd);
            _cyclePlanningFailures.Remove(cmd);
            if (cmd.B != null)
            {
                foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                    MarkBlockedPlanningRetryCooldown(cmd.B, tabRes, reason);

                SetCyclePlanFailureNote(cmd.B, cmd.cargoAllStart, reason);
            }
            LogWarning($"ABORT LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} name={cmd.customNameFromPlanMission} reason={reason}");
            RemoveLogisticsCycle(cm, cmd);
            return;
        }
    }

    private static bool ApplySmallReservePropellant(PMMissionParameter pmp)
    {
        if (!ReturnFuelEnabled() || pmp?.CargoAll?.cargoFuel == null || pmp.SC == null || pmp.FlyCompany == null)
            return false;

        var fuelType = pmp.FuelNeedToStart;
        var scType = pmp.SC.GetTypeSpaceCraft();
        if (fuelType == null || scType == null || scType.SolarSC)
            return false;

        var minFuel = pmp.MINFuelCost > 0 ? pmp.MINFuelCost : pmp.AllFuelNeed;
        if (minFuel <= 0)
            return false;

        var tankCapacity = scType.GetFuelCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        var targetPropellant = Math.Min(tankCapacity, Math.Ceiling(minFuel * ReservePropellantMultiplier));
        if (targetPropellant <= 0)
            return false;

        var currentTarget = pmp.CargoAll.cargoFuel.cargoMassPotencjal;
        if (currentTarget >= targetPropellant)
            return false;

        SolarSdk.MissionLoadout.ConfigureReservePropellant(pmp, fuelType, targetPropellant);

        LogVerbose($"RETURNFUEL reserve-propellant: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} targetPropellant={targetPropellant:0.#} tank={tankCapacity:0.#} normalCargo={pmp.CargoAll.CargoCurrent:0.#} reduceFuelToMinimum={pmp.ReduceFuelToMinimum}");
        return true;
    }

    private static List<Cargo> GetResourceCargoItems(CargoAll cargoAll)
    {
        return SolarSdk.MissionLoadout.GetRegularResourceCargoItems(cargoAll);
    }

    private static bool IsResourceCargo(Cargo cargo)
    {
        return cargo != null
            && cargo.resourceTypeType == EResourceTypeType.resorces
            && cargo.resourceType != null;
    }

    private static void ApplyCargoScale(List<Cargo> cargoItems, double[] original, double scale)
    {
        for (var i = 0; i < cargoItems.Count; i++)
            cargoItems[i].cargoMass = Math.Floor(original[i] * scale);
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        if (counts == null || counts.Count == 0) return "none";
        return string.Join(",", counts.Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    private static bool ReturnFuelEnabled()
    {
        return LogisticsModSdk.Plugin.ReturnFuelEnabled?.Value ?? true;
    }

    private static double ReturnFuelSafetyMultiplier()
    {
        var value = LogisticsModSdk.Plugin.ReturnFuelSafetyMultiplier?.Value ?? 1.5;
        return Math.Max(1, value);
    }

    private static bool ReserveCargoFirst()
    {
        return LogisticsModSdk.Plugin.ReturnFuelReserveCargoFirst?.Value ?? true;
    }

    private static double GetFuelStock(ObjectInfo oi, Company player, ResourceDefinition fuelType)
    {
        if (oi == null || player == null || fuelType == null) return 0;
        return oi.GetObjectInfoData(player)?.CheckResources(fuelType) ?? 0;
    }

    /// <summary>
    /// Returns the fuel stock accessible to a spacecraft at a destination body.
    /// Orbit-only ships at a surface body access fuel from the body's low orbit,
    /// not the surface. Falls back to surface stock if orbit has insufficient fuel
    /// and the ship can self-launch from the surface.
    /// </summary>
    private static double GetAccessibleFuelStock(ObjectInfo destination, Spacecraft sc, Company player, ResourceDefinition fuelType)
    {
        if (destination == null || player == null || fuelType == null)
            return 0;

        // If destination is already an orbit, fuel stock is straightforward
        if (destination.objectTypes == global::Data.EObjectTypes.Orbit)
            return GetFuelStock(destination, player, fuelType);

        // For surface destinations, check if the ship can actually land there
        var canLand = !RequiresLaunchVehicleForSpacecraft(destination, sc, player, 0);

        var orbitOI = destination.LowOrbitCustom?.GetObjectInfo();
        var orbitStock = orbitOI != null ? GetFuelStock(orbitOI, player, fuelType) : 0;
        var surfaceStock = canLand ? GetFuelStock(destination, player, fuelType) : 0;

        // Orbit-only ships can only access orbit fuel
        if (!canLand)
            return orbitStock;

        // Ships that can land: check orbit first, add surface if accessible
        return orbitStock + surfaceStock;
    }

    private static double GetProviderAvailableAfterMinimum(ObjectInfo providerOI, ResourceDefinition rd, Company player)
    {
        if (providerOI == null || rd == null || player == null) return 0;
        var data = Data.LogisticsNetwork.Get(providerOI);
        var oid = providerOI.GetObjectInfoData(player);
        if (oid == null) return 0;
        if ((data == null || data.providers == null || !data.providers.Any(p => p.isActive && p.ResourceDefinition == rd))
            && TryGetExportedOrbitProviderParent(providerOI, rd, out _))
        {
            var exportedResult = Math.Max(0, oid.CheckResources(rd) - GetCommittedStock(providerOI, rd));
            LogSurplusDiag(providerOI, rd, player, "exported-orbit", 0, exportedResult);
            return exportedResult;
        }
        if (data == null) return 0;

        var available = oid.CheckResources(rd);
        var minKeep = data.providers
            .Where(p => p.isActive && p.ResourceDefinition == rd)
            .Sum(p => p.minimumKeep);
        var committed = GetCommittedStock(providerOI, rd);
        var result = Math.Max(0, available - minKeep - committed);
        LogSurplusDiag(providerOI, rd, player, "normal", minKeep, result);
        return result;
    }

    // DIAGNOSTIC: logs the components of the surplus calculation so we can see which
    // term is wrong when a provider reports low/zero exportable stock despite a large
    // visible stockpile. Throttled per provider+resource to avoid flooding the log.
    private static readonly Dictionary<string, DateTime> _surplusDiagLast = new Dictionary<string, DateTime>();

    private static void LogSurplusDiag(ObjectInfo providerOI, ResourceDefinition rd, Company player,
        string path, double minKeep, double result)
    {
        if (!VerboseLoggingEnabled || providerOI == null || rd == null || player == null) return;
        var key = $"{providerOI.id}:{rd.ID}";
        var now = DateTime.UtcNow;
        if (_surplusDiagLast.TryGetValue(key, out var last) && (now - last).TotalSeconds < 1.5)
            return;
        _surplusDiagLast[key] = now;

        var oid = providerOI.GetObjectInfoData(player);
        double rawValue = -1;
        var rows = oid?.ListRowResourcesData;
        if (rows != null)
        {
            foreach (var row in rows)
            {
                if (row != null && row.ResourcesType == rd)
                {
                    rawValue = row.Value;
                    break;
                }
            }
        }
        var checkRes = oid != null ? oid.CheckResources(rd) : 0;
        var committed = GetCommittedStock(providerOI, rd);
        var data = Data.LogisticsNetwork.Get(providerOI);
        var provCount = data?.providers?.Count(p => p != null && p.isActive && p.ResourceDefinition == rd) ?? 0;
        var reservedDelta = rawValue >= 0 ? rawValue - checkRes : 0;
        LogVerbose($"SURPLUS-DIAG: provider={providerOI.ObjectName} rd={rd.ID} path={path} " +
            $"rawValue={rawValue:0.#} checkResources={checkRes:0.#} reservedDelta={reservedDelta:0.#} " +
            $"minKeep={minKeep:0.#} committed={committed:0.#} activeProviderRules={provCount} availableAfterMin={result:0.#}");
    }

    private static double GetProviderMinimumShipment(ObjectInfo providerOI, ResourceDefinition rd)
    {
        if (providerOI == null || rd == null) return 0;
        var data = Data.LogisticsNetwork.Get(providerOI);
        if ((data == null || data.providers == null || !data.providers.Any(p => p.isActive && p.ResourceDefinition == rd))
            && TryGetExportedOrbitProviderParent(providerOI, rd, out var parentProvider))
        {
            return GetProviderMinimumShipment(parentProvider, rd);
        }

        return data?.providers?
            .Where(p => p != null && p.isActive && p.ResourceDefinition == rd)
            .Select(p => Math.Max(0, p.minimumShipmentAmount))
            .DefaultIfEmpty(0)
            .Max() ?? 0;
    }

    private static bool MeetsProviderMinimumShipment(ObjectInfo providerOI, ResourceDefinition rd, double amount, out string reason)
    {
        reason = null;
        var minimumShipment = GetProviderMinimumShipment(providerOI, rd);
        if (minimumShipment <= 0 || amount >= minimumShipment)
            return true;

        reason = $"Waiting for minimum {rd?.Name ?? rd?.ID ?? "resource"} SEND shipment at {providerOI?.ObjectName ?? "unknown"}: {amount:0.#}/{minimumShipment:0.#}";
        return false;
    }

    private static bool AllowsSensibleOvership(Data.LogisticsRequest req)
    {
        return req != null && req.useMinimumAmount && req.reorderActive;
    }

    private static double GetCandidateAmount(Data.LogisticsRequest req, ObjectInfo providerOI, ResourceDefinition rd,
        double remaining, double available, double capacity, Spacecraft sc = null, ObjectInfo quotaLocation = null,
        Data.LogisticsProvider providerRule = null)
    {
        if (remaining <= 0 || available <= 0 || capacity <= 0) return 0;

        var desired = remaining;
        if (AllowsSensibleOvership(req))
        {
            desired = Math.Max(desired, GetProviderMinimumShipment(providerOI, rd));
            if (sc != null)
                desired = Math.Max(desired, GetMinimumShipmentForSpacecraft(quotaLocation ?? providerOI, sc, providerRule));
        }

        return Math.Min(Math.Min(available, capacity), desired);
    }

    private static bool TryGetExportedOrbitProviderParent(ObjectInfo orbitOI, ResourceDefinition rd, out ObjectInfo parentBody)
    {
        parentBody = null;
        if (orbitOI == null || rd == null || orbitOI.objectTypes != global::Data.EObjectTypes.Orbit)
            return false;

        parentBody = orbitOI.parentObjectInfo;
        var parentData = parentBody != null ? Data.LogisticsNetwork.Get(parentBody) : null;
        if (parentData?.providers == null)
        {
            parentBody = null;
            return false;
        }

        var hasExportProvider = parentData.providers.Any(p =>
            p != null && p.isActive && p.exportToOrbit && p.ResourceDefinition == rd);
        if (!hasExportProvider)
            parentBody = null;
        return hasExportProvider;
    }

    private static bool NetworkHasProviderForFuel(ResourceDefinition fuelType, Company player)
    {
        if (fuelType == null || player == null) return false;
        foreach (var oi in Data.LogisticsNetwork.GetAllObjects())
        {
            var data = Data.LogisticsNetwork.Get(oi);
            if (data == null) continue;
            if (!data.providers.Any(p => p.isActive && p.ResourceDefinition == fuelType)) continue;
            if (GetProviderAvailableAfterMinimum(oi, fuelType, player) > 0)
                return true;
        }
        return false;
    }

    private static double EstimatePrePlanReturnFuel(Spacecraft sc, Company player)
    {
        var type = sc?.spacecraftType;
        if (type == null || player == null || type.SolarSC) return 0;
        return Math.Ceiling(type.GetFuelCapacity(player) * PrePlanReturnFuelFractionOfTank);
    }

    private static Cargo FindResourceCargo(CargoAll cargoAll, ResourceDefinition rd)
    {
        return SolarSdk.MissionLoadout.FindRegularResourceCargo(cargoAll, rd);
    }

    private static void AddOrIncreaseResourceCargo(CargoAll cargoAll, ResourceDefinition rd, double amount)
    {
        SolarSdk.MissionLoadout.AddOrIncreaseResourceCargo(cargoAll, rd, amount);
    }

    private static double CargoAmountFor(CargoAll cargoAll, ResourceDefinition rd)
    {
        return SolarSdk.MissionLoadout.GetRegularResourceMass(cargoAll, rd);
    }

    private static bool CargoContainsResource(CargoAll cargoAll, ResourceDefinition rd)
    {
        return SolarSdk.MissionLoadout.ContainsRegularResource(cargoAll, rd);
    }

    private static bool CargoContainsResource(InfoCargoCyclicalMission cargoInfo, ResourceDefinition rd)
    {
        return cargoInfo?.Tab != null && cargoInfo.Tab.Any(tabRd => tabRd == rd);
    }

    private static double ReduceNonFuelCargo(CargoAll cargoAll, ResourceDefinition fuelType, double amountToRemove)
    {
        return SolarSdk.MissionLoadout.ReduceNonFuelResourceCargo(cargoAll, fuelType, amountToRemove);
    }

    private static bool BuildCargoManifestWithReturnFuel(Data.LogisticsRequest req, ResourceDefinition rd,
        double amount, ObjectInfo requesterOI, ObjectInfo providerOI, Spacecraft sc, Company player,
        double capacity, LaunchVehicleType lvType, out CargoAll cargoAll, out double normalCargo, out double reserveFuelCargo,
        out ResourceDefinition blockedFuelType, out double blockedFuelShortfall, out bool waitingForFuelProbe,
        Data.LogisticsProvider providerRule = null)
    {
        cargoAll = CargoAll.CreateCargoEmpty();
        normalCargo = Math.Min(amount, capacity);
        reserveFuelCargo = 0;
        blockedFuelType = null;
        blockedFuelShortfall = 0;
        waitingForFuelProbe = false;

        if (rd == null || normalCargo <= 0 || capacity <= 0)
            return false;

        AddOrIncreaseResourceCargo(cargoAll, rd, normalCargo);
        if (!ShouldReserveReturnFuel(providerOI, requesterOI, sc, player, providerRule))
        {
            normalCargo = CargoAmountFor(cargoAll, rd);
            LogVerbose($"RETURNFUEL reserve-skipped: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType ?? "null"} lv={lvType?.Name ?? "none"} reason=no-return-fuel-required manifest={FormatCargo(cargoAll)}");
            return cargoAll.CargoCurrent > 0;
        }

        if (!TryEstimateReturnFuelRequirement(providerOI, requesterOI, sc, player, cargoAll, lvType,
                out var waitingForProbe,
                out var fuelType, out var requiredReserve, out var destinationStock))
        {
            waitingForFuelProbe = waitingForProbe;
            if (waitingForFuelProbe)
            {
                LogVerbose($"RETURNFUEL estimate-pending: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType ?? "null"} rd={rd.ID} cargo={normalCargo:0.#} lv={lvType?.Name ?? "none"} manifest={FormatCargo(cargoAll)}");
                return false;
            }
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL estimate-skipped: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType ?? "null"} rd={rd.ID} cargo={normalCargo:0.#} lv={lvType?.Name ?? "none"} manifest={FormatCargo(cargoAll)}");
            normalCargo = CargoAmountFor(cargoAll, rd);
            return cargoAll.CargoCurrent > 0;
        }

        var existingFuelCargo = CargoAmountFor(cargoAll, fuelType);
        var shortfall = Math.Max(0, requiredReserve - destinationStock - existingFuelCargo);
        if (shortfall <= 0)
        {
            normalCargo = CargoAmountFor(cargoAll, rd);
            LogVerbose($"RETURNFUEL trust-domestic-stockpile: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} existingFuelCargo={existingFuelCargo:0.#} manifest={FormatCargo(cargoAll)}");
            return cargoAll.CargoCurrent > 0;
        }

        var providerFuelAvailable = GetProviderAvailableAfterMinimum(providerOI, fuelType, player);
        var maxFuelCargo = capacity * MaxReturnFuelCargoDisplacementFraction;
        var maxAdditionalFuelCargo = Math.Max(0, maxFuelCargo - existingFuelCargo);
        var fuelToAdd = Math.Min(shortfall, Math.Min(providerFuelAvailable, maxAdditionalFuelCargo));
        LogVerbose($"RETURNFUEL manifest-calc: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} existingFuelCargo={existingFuelCargo:0.#} shortfall={shortfall:0.#} providerFuel={providerFuelAvailable:0.#} capacity={capacity:0.#} maxFuelCargo={maxFuelCargo:0.#} plannedFuelAdd={fuelToAdd:0.#} before={FormatCargo(cargoAll)}");
        double reduced = 0;

        var freeCapacity = Math.Max(0, capacity - cargoAll.CargoCurrent);
        if (fuelToAdd > freeCapacity)
        {
            var displacementNeeded = fuelToAdd - freeCapacity;
            reduced = ReduceNonFuelCargo(cargoAll, fuelType, displacementNeeded);
        }

        freeCapacity = Math.Max(0, capacity - cargoAll.CargoCurrent);
        fuelToAdd = Math.Min(fuelToAdd, freeCapacity);
        if (fuelToAdd > 0)
        {
            AddOrIncreaseResourceCargo(cargoAll, fuelType, fuelToAdd);
            reserveFuelCargo = fuelToAdd;
        }

        existingFuelCargo = CargoAmountFor(cargoAll, fuelType);
        var remainingShortfall = Math.Max(0, requiredReserve - destinationStock - existingFuelCargo);
        if (remainingShortfall > 0)
        {
            blockedFuelType = fuelType;
            blockedFuelShortfall = remainingShortfall;
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-shortfall: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.spacecraftType?.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} providerFuel={providerFuelAvailable:0.#} fuelAdded={reserveFuelCargo:0.#} shortfall={remainingShortfall:0.#} manifest={FormatCargo(cargoAll)}");
            return false;
        }

        normalCargo = CargoAmountFor(cargoAll, rd);
        if (normalCargo <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL no-request-cargo-left: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} fuel={fuelType.ID} fuelAdded={reserveFuelCargo:0.#} reducedCargo={reduced:0.#} manifest={FormatCargo(cargoAll)}");
            return false;
        }
        LogVerbose($"RETURNFUEL ship-reserve-manifest: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} fuelAdded={reserveFuelCargo:0.#} reducedCargo={reduced:0.#} normalCargo={normalCargo:0.#} manifest={FormatCargo(cargoAll)}");
        return cargoAll.CargoCurrent > 0;
    }

    private static bool ShouldReserveReturnFuel(ObjectInfo providerOI, ObjectInfo requesterOI, Spacecraft sc, Company player, Data.LogisticsProvider providerRule = null)
    {
        var scType = sc?.GetTypeSpaceCraft();
        if (!ReturnFuelEnabled() || providerOI == null || requesterOI == null || sc == null || player == null || scType == null)
            return false;

        if (!UseFuelProbeForSpacecraft(providerOI, sc, providerRule))
            return false;

        if (scType.SolarSC || scType.LowOrbitContainer || scType.MagneticCatapult)
            return false;

        if (scType.GetFuelCapacity(player) <= 0)
            return false;

        return true;
    }

    private static bool UseFuelProbeForSpacecraft(ObjectInfo quotaLocation, Spacecraft sc, Data.LogisticsProvider providerRule = null)
    {
        if (quotaLocation == null || sc?.spacecraftType == null)
            return true;

        var assignedProvider = providerRule != null && Data.LogisticsNetwork.IsSpacecraftAssignedToProvider(sc.ID, providerRule)
            ? providerRule
            : Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID);
        var assignedSetting = Data.LogisticsNetwork.GetProviderSpacecraftSetting(assignedProvider, sc);
        if (assignedSetting != null)
            return assignedSetting.useFuelProbe;

        var data = Data.LogisticsNetwork.Get(quotaLocation);
        var quota = data?.spacecraftQuota?
            .FirstOrDefault(q => Data.LogisticsNetwork.QuotaMatches(q, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"));
        return quota?.useFuelProbe ?? true;
    }

    private static bool TryEstimateReturnFuelRequirement(ObjectInfo providerOI, ObjectInfo requesterOI,
        Spacecraft sc, Company player, CargoAll cargoAll, LaunchVehicleType lvType,
        out bool waitingForProbe,
        out ResourceDefinition fuelType, out double requiredReserve, out double destinationStock)
    {
        waitingForProbe = false;
        fuelType = null;
        requiredReserve = 0;
        destinationStock = 0;
        if (!ReturnFuelEnabled())
        {
            LogVerbose($"RETURNFUEL probe-skip: disabled route={providerOI?.ObjectName}->{requesterOI?.ObjectName}");
            return false;
        }

        if (providerOI == null || requesterOI == null || sc == null || player == null || cargoAll == null)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL probe-skip: missing-input provider={providerOI?.ObjectName ?? "null"} requester={requesterOI?.ObjectName ?? "null"} ship={sc?.GetSpacecraftName() ?? "null"} player={player?.name ?? "null"} cargo={(cargoAll == null ? "null" : FormatCargo(cargoAll))}");
            return false;
        }

        var scType = sc.GetTypeSpaceCraft();
        if (scType == null || scType.SolarSC)
        {
            LogVerbose($"RETURNFUEL probe-skip: unsupported-ship route={providerOI.ObjectName}->{requesterOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType?.NameRocketType ?? "null"} solar={scType?.SolarSC.ToString() ?? "null"}");
            return false;
        }

        var probeKey = BuildReturnFuelProbeKey(providerOI, requesterOI, sc, player, lvType);
        if (!_returnFuelProbeCache.TryGetValue(probeKey, out var probe) || (!probe.Pending && !probe.Complete))
        {
            StartAsyncReturnFuelProbe(probeKey, providerOI, requesterOI, sc, player, lvType);
            waitingForProbe = true;
            return false;
        }

        if (probe.Pending)
        {
            waitingForProbe = true;
            return false;
        }

        if (probe.FuelType == null)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL probe-no-fueltype-cached: returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} lv={lvType?.Name ?? "none"} result={probe.Result} failure={probe.FailureReason ?? "none"}");
            return false;
        }

        fuelType = probe.FuelType;
        requiredReserve = probe.RequiredReserve;
        destinationStock = GetAccessibleFuelStock(requesterOI, sc, player, fuelType);
        LogVerbose($"RETURNFUEL probe-cache-hit: outbound={providerOI.ObjectName}->{requesterOI.ObjectName} return={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} lv={lvType?.Name ?? "none"} result={probe.Result} fuel={fuelType.ID} allFuel={probe.AllFuelNeed:0.#} minFuel={probe.MinFuelCost:0.#} fuelNeed={probe.FuelNeed:0.#} leftOver={probe.LeftOverFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} tank={scType.GetFuelCapacity(player):0.#} cargo={FormatCargo(cargoAll)}");
        if (requiredReserve <= 0)
        {
            var fallbackReserve = Math.Ceiling(scType.GetCargoCapacity(player) * MaxReturnFuelCargoDisplacementFraction);
            requiredReserve = fallbackReserve;
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL probe-zero-reserve-fallback: returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} lv={lvType?.Name ?? "none"} result={probe.Result} fuel={fuelType.ID} allFuel={probe.AllFuelNeed:0.#} minFuel={probe.MinFuelCost:0.#} fallbackReserve={fallbackReserve:0.#} destStock={destinationStock:0.#}");
        }
        return requiredReserve > 0;
    }

    private static string BuildReturnFuelProbeKey(ObjectInfo providerOI, ObjectInfo requesterOI,
        Spacecraft sc, Company player, LaunchVehicleType lvType)
    {
        var transfer = GetTransferTypeForSpacecraft(providerOI, sc);
        var scType = sc?.spacecraftType ?? sc?.GetTypeSpaceCraft();
        var fuelCapacity = scType == null || player == null ? 0 : scType.GetFuelCapacity(player);
        var cargoCapacity = scType == null || player == null ? 0 : scType.GetCargoCapacity(player);
        return string.Join("|",
            player?.ID ?? "company",
            providerOI?.id.ToString() ?? "provider",
            requesterOI?.id.ToString() ?? "requester",
            scType?.ID ?? scType?.NameRocketType ?? "sc",
            $"tank={Math.Round(fuelCapacity, 1)}",
            $"cargo={Math.Round(cargoCapacity, 1)}",
            lvType?.ID ?? lvType?.Name ?? "no-lv",
            transfer.ToString(),
            $"margin={ReturnFuelSafetyMultiplier():0.###}");
    }

    private static void StoreReturnFuelProbe(string key, ReturnFuelProbeState probe)
    {
        if (string.IsNullOrEmpty(key) || probe == null)
            return;

        if (!_returnFuelProbeCache.ContainsKey(key))
            _returnFuelProbeCacheOrder.Enqueue(key);
        _returnFuelProbeCache[key] = probe;

        var attempts = 0;
        while (_returnFuelProbeCache.Count > MaxReturnFuelProbeCacheEntries
            && _returnFuelProbeCacheOrder.Count > 0
            && attempts++ < MaxReturnFuelProbeCacheEntries * 2)
        {
            var evict = _returnFuelProbeCacheOrder.Dequeue();
            if (!_returnFuelProbeCache.TryGetValue(evict, out var existing))
                continue;
            if (existing.Pending)
            {
                _returnFuelProbeCacheOrder.Enqueue(evict);
                continue;
            }

            _returnFuelProbeCache.Remove(evict);
            LogVerbose($"RETURNFUEL probe-cache-evict: key={evict}");
        }
    }

    private static void StartAsyncReturnFuelProbe(string key, ObjectInfo providerOI, ObjectInfo requesterOI,
        Spacecraft sc, Company player, LaunchVehicleType lvType)
    {
        using (TimeScope($"StartAsyncReturnFuelProbe {requesterOI?.ObjectName ?? "null"}->{providerOI?.ObjectName ?? "null"}"))
        {
        if (string.IsNullOrEmpty(key) || providerOI == null || requesterOI == null || sc == null || player == null)
            return;
        if (_returnFuelProbeCache.TryGetValue(key, out var existing) && existing.Pending)
            return;

        var scType = sc.GetTypeSpaceCraft();
        var fuelType = scType?.GetFuelType();
        var probe = new ReturnFuelProbeState
        {
            Pending = true,
            Complete = false,
            RequestedAt = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now,
            FuelType = fuelType
        };
        StoreReturnFuelProbe(key, probe);

        var probeCargo = CargoAll.CreateCargoEmpty();
        var probeSpacecraft = new PlannerSpacecraftInfo(sc, requesterOI);
        var pmp = new PMMissionParameter();
        pmp.SetCompany(player);
        pmp.SetTabDestination(requesterOI, providerOI);
        pmp.SetTabCargo(probeCargo);
        pmp.SetTabSC(probeSpacecraft);
        pmp.SetTabLV(new List<ILaunchVehicleInfo>(), 0);
        pmp.ForCyclicalMission = true;
        pmp.ReduceFuelToMinimum = false;
        pmp.TryFixWrongThrust = true;
        pmp.TrajectoryColor = Color.blue;
        pmp.SetMissionOrigin(MissionInfo.EMissionCreator.Other);
        var transfer = GetTransferTypeForSpacecraft(providerOI, sc);
        // Moon-case routes (planet ↔ moon) use a slider, not a porkchop plot.
        // Setting ClickFastestButton on a moon case causes ButtonFastestClickButton
        // to run its porkchop grid search against an empty/invalid grid, corrupting
        // the trajectory and producing garbage fuel estimates. The probe's
        // PlannerSpacecraftInfo is invisible to both our prefix guards and stock's
        // moon-case early-exit in ButtonFastestClickButton (because
        // TransferTypeMoonCase defaults to Optimal, not Fastest).
        var isMoonCase = IsMoonCaseRoute(requesterOI, providerOI);
        pmp.TryFastAsPossible = transfer == ETransferType.Fastest && !isMoonCase;
        pmp.ClickFastestButton = transfer == ETransferType.Fastest && !isMoonCase;
        if (isMoonCase)
            pmp.TransferTypeMoonCase = ETransferType.Optimal;
        ApplyCachedPrecalculateData(pmp);

        if (VerboseLoggingEnabled)
            LogVerbose($"RETURNFUEL async-probe-start: key={key} returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType?.NameRocketType ?? "null"} probePos={probeSpacecraft.GetActualPosition()?.ObjectName ?? "null"} transfer={transfer} moonCase={isMoonCase} fuel={fuelType?.ID ?? "null"}");
        MonoBehaviourSingleton<GameManager>.Instance.SetPMParameterForCodeJobSystem(pmp, () =>
        {
            using (TimeScope($"ReturnFuelProbeCallback {requesterOI?.ObjectName ?? "null"}->{providerOI?.ObjectName ?? "null"}"))
            {
            var result = pmp.CheckCanPlanMission().planMissionResult;
            var callbackFuelType = pmp.FuelNeedToStart ?? fuelType;
            var planFuelNeed = Math.Max(pmp.AllFuelNeed, pmp.MINFuelCost);
            var tankCapacity = scType?.GetFuelCapacity(player) ?? 0;
            var estimatedReturnFuel = Math.Min(Math.Max(0, planFuelNeed), tankCapacity * Math.Max(1, pmp.SCCount));
            var requiredReserve = Math.Ceiling(estimatedReturnFuel * ReturnFuelSafetyMultiplier());

            probe.Pending = false;
            probe.Complete = true;
            probe.CompletedAt = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
            probe.FuelType = callbackFuelType;
            probe.FuelNeed = pmp.FuelNeed;
            probe.MinFuelCost = pmp.MINFuelCost;
            probe.AllFuelNeed = pmp.AllFuelNeed;
            probe.LeftOverFuel = pmp.LeftOverFuel;
            probe.RequiredReserve = requiredReserve;
            probe.Result = result;
            probe.FailureReason = result == PMMissionParameter.EPlanMissionResult.AllOk ? null : result.ToString();
            CachePrecalculateData(pmp, "return-fuel-probe");

            if (VerboseLoggingEnabled)
                LogVerbose($"RETURNFUEL async-probe-result: key={key} returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} result={result} fuel={callbackFuelType?.ID ?? "null"} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} fuelNeed={pmp.FuelNeed:0.#} leftOver={pmp.LeftOverFuel:0.#} reserve={requiredReserve:0.#} depart={pmp.DepartureTimeDate:yyyy-MM-dd} arrive={pmp.Arrival:yyyy-MM-dd}");
            }
        });
        }
    }

    private static void EnsureReturnFuelReserveFromPlan(PMMissionParameter pmp)
    {
        // This hook runs after stock has calculated the plan. If stock says the return leg
        // needs fuel at the destination, reserve cargo capacity now by displacing payload.
        if (!ReturnFuelEnabled() || pmp?.CargoAll == null || pmp.SC == null || pmp.FlyCompany == null || pmp.Target == null)
            return;

        var fuelType = pmp.FuelNeedToStart;
        var scType = pmp.SC.GetTypeSpaceCraft();
        if (fuelType == null || scType == null || scType.SolarSC)
            return;

        var planFuelNeed = pmp.MINFuelCost > 0 ? Math.Min(pmp.AllFuelNeed, pmp.MINFuelCost) : pmp.AllFuelNeed;
        var estimatedReturnFuel = Math.Min(Math.Max(0, planFuelNeed), scType.GetFuelCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount));
        var requiredReserve = Math.Ceiling(estimatedReturnFuel * ReturnFuelSafetyMultiplier());
        var destinationStock = pmp.SC is Spacecraft probeShip
            ? GetAccessibleFuelStock(pmp.Target, probeShip, pmp.FlyCompany, fuelType)
            : GetFuelStock(pmp.Target, pmp.FlyCompany, fuelType);
        var existingFuelCargo = CargoAmountFor(pmp.CargoAll, fuelType);
        var shortfall = Math.Max(0, requiredReserve - destinationStock - existingFuelCargo);

        if (shortfall <= 0)
        {
            LogVerbose($"RETURNFUEL trust-domestic-stockpile-plan: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} estimated={estimatedReturnFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} existingFuelCargo={existingFuelCargo:0.#} manifest={FormatCargo(pmp.CargoAll)}");
            return;
        }

        var capacity = scType.GetCargoCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        var providerFuelAvailable = GetProviderAvailableAfterMinimum(pmp.Start, fuelType, pmp.FlyCompany);
        var fuelToAdd = Math.Min(shortfall, providerFuelAvailable);
        if (fuelToAdd <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-shortfall: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} estimated={estimatedReturnFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} shortfall={shortfall:0.#} providerFuel={providerFuelAvailable:0.#}");
            return;
        }

        var maxFuelCargo = capacity * MaxReturnFuelCargoDisplacementFraction;
        var maxAdditionalFuelCargo = Math.Max(0, maxFuelCargo - existingFuelCargo);
        fuelToAdd = Math.Min(fuelToAdd, maxAdditionalFuelCargo);
        if (fuelToAdd <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-cap-reached: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} maxFuelCargo={maxFuelCargo:0.#} existingFuelCargo={existingFuelCargo:0.#} manifest={FormatCargo(pmp.CargoAll)}");
            return;
        }

        var freeCapacity = Math.Max(0, capacity - pmp.CargoAll.CargoCurrent);
        double reduced = 0;
        if (fuelToAdd > freeCapacity)
        {
            var displacementNeeded = fuelToAdd - freeCapacity;
            reduced = ReduceNonFuelCargo(pmp.CargoAll, fuelType, displacementNeeded);
        }

        freeCapacity = Math.Max(0, capacity - pmp.CargoAll.CargoCurrent);
        fuelToAdd = Math.Min(fuelToAdd, freeCapacity);
        if (fuelToAdd <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-defer-no-room: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} capacity={capacity:0.#} reducedCargo={reduced:0.#} manifest={FormatCargo(pmp.CargoAll)}");
            return;
        }

        AddOrIncreaseResourceCargo(pmp.CargoAll, fuelType, fuelToAdd);
        LogVerbose($"RETURNFUEL ship-reserve-plan: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} estimated={estimatedReturnFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} shortfall={shortfall:0.#} maxFuelCargo={maxFuelCargo:0.#} fuelAdded={fuelToAdd:0.#} reducedCargo={reduced:0.#} manifest={FormatCargo(pmp.CargoAll)}");
    }

    private static string FormatCargo(CargoAll cargoAll)
    {
        return SolarSdk.MissionLoadout.FormatCargo(cargoAll);
    }

    private static void MarkShipForReturn(Spacecraft sc, ObjectInfo home, ObjectInfo destination, ResourceDefinition rd)
    {
        if (sc == null || home == null || sc.ID < 0) return;
        // Ownership starts at outbound cycle creation, not at launch. That prevents a ship
        // sitting in stock's planned state from being selected for another export.
        _returnHomeByShipId[sc.ID] = new ReturnHomeState
        {
            Home = home,
            Destination = destination,
            Resource = rd,
            HasLeftHome = false
        };
        if (VerboseLoggingEnabled)
            LogVerbose($"RETURNHOME mark: ship={sc.GetSpacecraftName()} id={sc.ID} home={home.ObjectName} destination={destination?.ObjectName ?? "null"} rd={rd?.ID ?? "null"}");
    }

    private static void TryReturnIdleLogisticsShips(Company player, PlannerSnapshot snapshot = null)
    {
        if (player == null || _returnHomeByShipId.Count == 0) return;

        // Return logic is intentionally separate from request fulfillment. Even a satisfied
        // request may need status text and safety cooldowns while its ships are stranded.
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null) return;

        var trackedShips = GetTrackedReturnShips(player, snapshot);
        foreach (var sc in trackedShips)
        {
            if (sc == null || sc.spacecraftType == null) continue;
            if (sc.GetCompany() != player) continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state)) continue;
            var home = state.Home;
            if (home == null)
            {
                _returnHomeByShipId.Remove(sc.ID);
                continue;
            }
            if (sc.CurrentPhase != Spacecraft.EPhase.None) continue;
            var current = sc.CurrentlyOnThisObject;
            if (current == null) continue;

            var currentPlanKey = $"{sc.ID}:{current.id}:{home.id}";
            if (state.ResolvedPlanKey != null && !state.ResolvedPlanKey.StartsWith(currentPlanKey, StringComparison.Ordinal))
                ResetReturnPlanState(state);
            if (state.PendingPlanKey != null && !state.PendingPlanKey.StartsWith(currentPlanKey, StringComparison.Ordinal))
                ResetReturnPlanState(state);

            if (current == home)
            {
                var attachedCycleAtHome = cm.GetCycleMission(sc);
                if (IsLogisticsReturnMission(attachedCycleAtHome))
                {
                    _cycleCreatedAt.Remove(attachedCycleAtHome);
                    _cyclePlanningFailures.Remove(attachedCycleAtHome);
                    RemoveLogisticsCycle(cm, attachedCycleAtHome);
                    if (VerboseLoggingEnabled)
                        LogVerbose($"RETURNHOME remove-complete-cycle: ship={sc.GetSpacecraftName()} id={sc.ID} cycle={attachedCycleAtHome.customNameFromPlanMission}");
                }

                if (state.HasLeftHome)
                {
                    ResetReturnPlanState(state);
                    ResetReturnFailureState(state);
                    _returnHomeByShipId.Remove(sc.ID);
                    if (VerboseLoggingEnabled)
                        LogVerbose($"RETURNHOME arrived: ship={sc.GetSpacecraftName()} id={sc.ID} home={home.ObjectName}");
                }
                continue;
            }

            var attachedCycle = cm.GetCycleMission(sc);
            if (attachedCycle != null)
            {
                if (IsLogisticsReturnMission(attachedCycle))
                {
                    if (IsCyclePastPlanningGrace(attachedCycle)
                        && !HasCycleActuallyLaunched(sc, attachedCycle, cm))
                    {
                        _cycleCreatedAt.Remove(attachedCycle);
                        _cyclePlanningFailures.Remove(attachedCycle);
                        RemoveLogisticsCycle(cm, attachedCycle);
                        SetReturnRetryCooldown(state, sc, current, home, $"return cycle did not launch within {EffectiveCyclePlanningGraceDays:0.#} days");
                        LogWarning($"RETURNHOME break-unlaunched-cycle: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} cooldownDays={ReturnCycleBlockedCooldownDays:0.#} cycle={attachedCycle.customNameFromPlanMission}");
                    }
                    else
                    {
                        LogVerbose($"RETURNHOME wait-attached-return-cycle: ship={sc.GetSpacecraftName()} id={sc.ID} cycle={attachedCycle.customNameFromPlanMission}");
                    }
                    continue;
                }

                if (IsLogisticsDeliveryMission(attachedCycle))
                {
                    LogVerbose($"RETURNHOME wait-delivery-detach: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} cycle={attachedCycle.customNameFromPlanMission}");
                }
                else
                {
                    LogVerbose($"RETURNHOME wait-attached-cycle: ship={sc.GetSpacecraftName()} id={sc.ID} cycle={attachedCycle.customNameFromPlanMission}");
                }
                continue;
            }

            if (IsReturnRetryCoolingDown(state, out var returnCooldownNote))
            {
                state.LastBlockedStatusNote = returnCooldownNote;
                LogVerbose($"RETURNHOME cooldown: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} note={returnCooldownNote}");
                continue;
            }
            state.ReturnRetryAfter = DateTime.MinValue;
            state.ReturnRetryWallClockAfterUtc = DateTime.MinValue;

            state.HasLeftHome = true;
            if (TrySetupReturnCycle(sc, current, home, player, state, snapshot))
                continue;
        }
    }

    private static string GetReturnBlockedStatusNote(ObjectInfo requester, ResourceDefinition rd, Company player, PlannerSnapshot snapshot = null)
    {
        // Summarize multiple owned ships for the UI: some may already be returning while
        // others are landed/orbital but blocked by fuel, LV, or stock planning cooldowns.
        if (requester == null || rd == null || player == null || _returnHomeByShipId.Count == 0)
            return null;

        var ships = GetShipsAtLocation(requester, player, snapshot);
        var returning = new List<string>();
        var blockedByReason = new Dictionary<string, List<string>>();

        foreach (var sc in ships)
        {
            if (sc == null || sc.spacecraftType == null) continue;
            if (sc.GetCompany() != player) continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state)) continue;
            if (state?.Destination != requester || state.Resource != rd) continue;
            if (sc.CurrentlyOnThisObject != requester) continue;

            var shipName = sc.GetSpacecraftName();
            var note = state.LastBlockedStatusNote;
            if (string.IsNullOrWhiteSpace(note))
                note = LogisticsStrings.AwaitingReturnFrom(sc.CurrentlyOnThisObject);

            if (note == LogisticsStrings.AwaitingReturnFrom(sc.CurrentlyOnThisObject))
            {
                returning.Add(shipName);
            }
            else
            {
                if (!blockedByReason.TryGetValue(note, out var list))
                {
                    list = new List<string>();
                    blockedByReason[note] = list;
                }
                list.Add(shipName);
            }
        }

        if (returning.Count == 0 && blockedByReason.Count == 0)
            return null;

        var parts = new List<string>();
        if (returning.Count > 0)
            parts.Add(FormatReturnShipGroup(returning.Count, "returning", returning));
        foreach (var kv in blockedByReason.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key))
            parts.Add(FormatReturnShipGroup(kv.Value.Count, $"blocked: {kv.Key}", kv.Value));
        return string.Join("; ", parts);
    }

    private static IEnumerable<Spacecraft> GetTrackedReturnShips(Company player, PlannerSnapshot snapshot = null)
    {
        if (player == null || _returnHomeByShipId.Count == 0)
            return Enumerable.Empty<Spacecraft>();

        if (snapshot?.ShipsById != null)
        {
            var result = new List<Spacecraft>();
            foreach (var shipId in _returnHomeByShipId.Keys.ToList())
            {
                if (snapshot.ShipsById.TryGetValue(shipId, out var sc) && sc != null)
                    result.Add(sc);
            }
            return result;
        }

        var ships = snapshot?.Ships
            ?? MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();
        return ships.Where(sc => sc != null
            && sc.GetCompany() == player
            && _returnHomeByShipId.ContainsKey(sc.ID));
    }

    public static int GetAwayLogisticsSpacecraftCountForQuota(ObjectInfo quotaHome, LogisticsModSdk.Data.ShipQuotaEntry quota)
    {
        if (quotaHome == null || quota == null || _returnHomeByShipId.Count == 0)
            return 0;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null)
            return 0;

        var count = 0;
        foreach (var sc in GetTrackedReturnShips(player))
        {
            if (sc == null || sc.spacecraftType == null || sc.ID < 0)
                continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state == null)
                continue;
            if (!SameObjectInfo(state.Home, quotaHome))
                continue;
            if (!Data.LogisticsNetwork.QuotaMatches(quota, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"))
                continue;

            // Planned-but-not-launched ships are still visible at the quota home. This count is
            // specifically for assigned ships the player cannot currently see at that node.
            if (!SameObjectInfo(sc.CurrentlyOnThisObject, quotaHome) || sc.CurrentPhase != Spacecraft.EPhase.None)
                count++;
        }

        return count;
    }

    public static int GetReturnReservedSpacecraftCountAt(ObjectInfo currentLocation, string typeId, string fallbackName)
    {
        if (currentLocation == null || _returnHomeByShipId.Count == 0)
            return 0;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null)
            return 0;

        var count = 0;
        foreach (var sc in GetTrackedReturnShips(player))
        {
            if (sc == null || sc.spacecraftType == null || sc.ID < 0)
                continue;
            if (!SameObjectInfo(sc.CurrentlyOnThisObject, currentLocation))
                continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state?.Home == null)
                continue;
            if (SameObjectInfo(state.Home, currentLocation))
                continue;
            if (!SameQuotaKey(typeId, sc.spacecraftType.ID)
                && !SameQuotaKey(typeId, sc.spacecraftType.NameRocketType ?? fallbackName)
                && !SameQuotaKey(fallbackName, sc.spacecraftType.ID)
                && !SameQuotaKey(fallbackName, sc.spacecraftType.NameRocketType ?? fallbackName))
                continue;

            count++;
        }

        return count;
    }

    private static bool SameQuotaKey(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameObjectInfo(ObjectInfo a, ObjectInfo b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.id == b.id;
    }

    private static string FormatReturnShipGroup(int count, string label, List<string> details)
    {
        if (details == null || details.Count == 0)
            return $"{count} ship{(count == 1 ? "" : "s")} {label}";
        if (count == 1)
            return $"{details[0]} {label}";

        var shown = string.Join(", ", details.Take(3));
        var suffix = details.Count > 3 ? $", +{details.Count - 3} more" : "";
        return $"{count} ships {label}: {shown}{suffix}";
    }

    private static void ResetReturnPlanState(ReturnHomeState state)
    {
        if (state == null) return;
        state.PendingPlanKey = null;
        state.PendingPlanParameter = null;
        state.PendingPlanResult = null;
        state.ResolvedPlanKey = null;
        state.HasResolvedPlanResult = false;
        state.ResolvedFuelType = null;
        state.ResolvedFuelNeed = 0;
        state.ResolvedPlanDate = DateTime.MinValue;
    }

    private static void ResetReturnFailureState(ReturnHomeState state)
    {
        if (state == null) return;
        state.ConsecutiveReturnCycleFailures = 0;
        state.ReturnRetryAfter = DateTime.MinValue;
        state.ReturnRetryWallClockAfterUtc = DateTime.MinValue;
    }

    private static bool TrySetupReturnCycle(Spacecraft sc, ObjectInfo current, ObjectInfo home, Company player, ReturnHomeState state, PlannerSnapshot snapshot = null, bool allowBackhaul = true, double backhaulAmountLimit = double.PositiveInfinity)
    {
        // Let stock planning validate the return route, but throttle failed attempts. A
        // failed stock cycle can make the ship temporarily disappear from planet view, so
        // repeated creation/destruction needs to be treated as unsafe.
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (sc == null || current == null || home == null || player == null || cm == null) return false;
        if (!ValidateSpacecraftForReturnCycleCreation(sc, player, "return-home-create"))
            return false;
        if (IsReturnRetryCoolingDown(state, out var returnCooldownNote))
        {
            state.LastBlockedStatusNote = returnCooldownNote;
            LogVerbose($"RETURNHOME skip-create-cooldown: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} note={returnCooldownNote}");
            return false;
        }

        LaunchVehicleType returnLvType = null;
        LaunchVehicle returnLv = null;
        var scType = sc.spacecraftType;
        var currentIsOrbit = current.objectTypes == global::Data.EObjectTypes.Orbit;
        var needsLaunchVehicle = !currentIsOrbit && RequiresLaunchVehicleForSpacecraft(current, sc, player, 0);
        if (needsLaunchVehicle)
        {
            var launchSupport = GetAvailableLaunchSupport(current, player, snapshot);
            returnLv = launchSupport
                .Select(option => option.Vehicle)
                .FirstOrDefault(lv => lv != null
                    && lv.launchVehicleType != null
                    && lv.GetCompany() == player
                    && (!lv.launchTime.HasValue || lv.launchVehicleType.reusability > 0f));
            if (returnLv == null)
            {
                var details = string.Join("; ", launchSupport
                    .Where(option => option?.Vehicle != null)
                    .Take(6)
                    .Select(option =>
                    {
                        var lv = option.Vehicle;
                        var typeName = lv.launchVehicleType?.Name ?? "null";
                        var owner = lv.GetCompany()?.Definition?.ID ?? lv.company?.Definition?.ID ?? "null";
                        var atBody = lv.objectInfo?.ObjectName ?? "null";
                        var launched = lv.launchTime.HasValue ? "launched" : "ground";
                        var reusable = lv.launchVehicleType != null ? lv.launchVehicleType.reusability.ToString("0.##") : "null";
                        return $"{typeName}/owner={owner}/at={atBody}/{launched}/reuse={reusable}/support={option.Label}";
                    }));
                LogReturnBlockedOnce(
                    state,
                    $"ship={sc.GetSpacecraftName()} current={current.ObjectName} home={home.ObjectName} reason=current body requires LV and none is ready lvCount={launchSupport.Count} lv=[{details}]",
                    LogisticsStrings.WaitingForLaunchVehicleAt(current));
                return false;
            }
            returnLvType = returnLv.launchVehicleType;
        }
        else
        {
            LogVerbose($"RETURNHOME no-LV-needed: ship={sc.GetSpacecraftName()} current={current.ObjectName} home={home.ObjectName} main={player.mainObjectInfo?.ObjectName} needMoonLV={scType?.needLaunchVehicleToGoToMoon}");
        }

        var transferType = GetTransferTypeForSpacecraft(home, sc);
        // Moon-case override: return routes between planet and moon have no porkchop.
        if (transferType == ETransferType.Fastest
            && IsMoonCaseRoute(current, home))
        {
            transferType = ETransferType.Optimal;
            LogVerbose($"MOONCASE return-transfer-override: route={current.ObjectName}->{home.ObjectName} forced=Optimal (moon-case has no porkchop)");
        }
        if (allowBackhaul && transferType == ETransferType.Fastest && double.IsPositiveInfinity(backhaulAmountLimit))
        {
            var rawCapacity = scType?.GetCargoCapacity(player) ?? 0;
            var reserveFuelRoom = UseFuelProbeForSpacecraft(home, sc)
                ? EstimatePrePlanReturnFuel(sc, player)
                : 0;
            backhaulAmountLimit = Math.Max(0, Math.Floor((rawCapacity - reserveFuelRoom) * FastestBackhaulCargoFraction));
            LogVerbose($"RETURNHOME fast-backhaul-cap: ship={sc.GetSpacecraftName()} id={sc.ID} rawCapacity={rawCapacity:0.#} reserveFuelRoom={reserveFuelRoom:0.#} cap={backhaulAmountLimit:0.#} current={current.ObjectName} home={home.ObjectName}");
        }
        var backhaulCargo = CargoAll.CreateCargoEmpty();
        ResourceDefinition backhaulRd = null;
        double backhaulAmount = 0;
        ObjectInfo backhaulTarget = null;
        if (allowBackhaul && TryBuildBackhaulManifest(sc, current, home, player, snapshot, out backhaulRd, out backhaulAmount, out backhaulTarget, backhaulAmountLimit))
        {
            AddOrIncreaseResourceCargo(backhaulCargo, backhaulRd, backhaulAmount);
            LogVerbose($"RETURNHOME backhaul: ship={sc.GetSpacecraftName()} id={sc.ID} rd={backhaulRd.ID} amount={backhaulAmount:0.#} limit={(double.IsPositiveInfinity(backhaulAmountLimit) ? "none" : backhaulAmountLimit.ToString("0.#"))} transfer={transferType} current={current.ObjectName} home={home.ObjectName} target={backhaulTarget?.ObjectName ?? "null"}");
        }

        var returnTarget = backhaulRd != null && backhaulAmount > 0 && backhaulTarget != null
            ? backhaulTarget
            : home;
        var scList = new List<ISpacecraftInfo> { sc as ISpacecraftInfo };
        if (!ValidateSdkDispatchBoundary("return-home", player, current, returnTarget, sc, backhaulCargo, allowSyntheticCarrier: false, out var validationFailure))
        {
            state.LastBlockedReason = validationFailure;
            state.LastBlockedStatusNote = validationFailure;
            return false;
        }

        var cycleResult = SolarSdk.CyclicalMissions.CreateAndAddCycle(new SdkCycleDraft
        {
            Source = returnTarget, Target = current, Company = player,
            CargoStart = ECargoStart.FlyWithWhatIsAvailable, CargoEnd = ECargoStart.FlyWithWhatIsAvailable,
            CargoAllStart = CargoAll.CreateCargoEmpty(), CargoAllEnd = backhaulCargo,
            LaunchVehicleTypeA = null, LaunchVehicleTypeB = returnLvType, TransferType = transferType,
            Ends = EEnds.ThisManyTimes,
            EndsObjectThisManyTimes = 1,
            Spacecraft = scList,
            CustomName = BuildLogisticsMissionName(current, returnTarget, state.Resource, isReturn: true, backhaulRd: backhaulRd)
        }, sc, SdkOwnerTag, SdkReservationOwner, "return-home");
        if (!cycleResult.Success)
        {
            state.LastBlockedReason = cycleResult.FailureReason;
            state.LastBlockedStatusNote = cycleResult.FailureReason;
            LogWarning($"RETURNHOME blocked: SDK cycle create failed reason={cycleResult.FailureCode}:{cycleResult.FailureReason}");
            return false;
        }

        var cmd = cycleResult.Cycle;
        _cycleCreatedAt[cmd] = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        ResetReturnPlanState(state);
        MarkReturnAttemptCooldown(state, sc, current, returnTarget, "return cycle handed to stock planner");
        state.LastBlockedReason = null;
        state.LastBlockedStatusNote = LogisticsStrings.AwaitingReturnFrom(current);
        state.LastBlockedDate = DateTime.MinValue;
        RegisterLogisticsCycleName(cmd);

        HandOffCycleToStockPlanner(sc, cmd, "return-home");

        if (cm.GetCycleMission(sc) != cmd
            && sc.CurrentPhase == Spacecraft.EPhase.None
            && sc.CurrentlyOnThisObject == current)
        {
            _cycleCreatedAt.Remove(cmd);
            _cyclePlanningFailures.Remove(cmd);
            if (backhaulRd != null && allowBackhaul)
            {
                ResetReturnPlanState(state);
                state.ReturnRetryAfter = DateTime.MinValue;
                state.ReturnRetryWallClockAfterUtc = DateTime.MinValue;
                state.ConsecutiveReturnCycleFailures = 0;
                var fastNote = transferType == ETransferType.Fastest ? " fast-route-priority=true" : "";
                LogWarning($"RETURNHOME backhaul-retry-empty: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} target={returnTarget.ObjectName} rd={backhaulRd.ID} amount={backhaulAmount:0.#}{fastNote}");
                return TrySetupReturnCycle(sc, current, home, player, state, snapshot, allowBackhaul: false);
            }
            SetReturnRetryCooldown(state, sc, current, returnTarget, "return cycle detached before ship launched");
            LogWarning($"RETURNHOME detached-before-launch: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} target={returnTarget.ObjectName}");
            return false;
        }

        if (backhaulRd != null && backhaulAmount > 0)
        {
            CommitStock(current, backhaulRd, backhaulAmount);
            RegisterBackhaulInFlight(backhaulTarget ?? home, backhaulRd, backhaulAmount, snapshot);
        }

        if (VerboseLoggingEnabled)
        {
            var backhaulNote = backhaulRd != null ? $" backhaul={backhaulRd.ID}:{backhaulAmount:0.#}" : "";
            LogVerbose($"RETURNHOME cycle: ship={sc.GetSpacecraftName()} id={sc.ID} {current.ObjectName}->{returnTarget.ObjectName} home={home.ObjectName} lv={(returnLvType?.Name ?? "none")}{backhaulNote}");
        }
        return true;
    }

    private static bool TryBuildBackhaulManifest(Spacecraft sc, ObjectInfo current, ObjectInfo home,
        Company player, PlannerSnapshot snapshot,
        out ResourceDefinition backhaulRd, out double backhaulAmount, out ObjectInfo backhaulTarget, double amountLimit = double.PositiveInfinity)
    {
        backhaulRd = null;
        backhaulAmount = 0;
        backhaulTarget = null;
        if (sc?.spacecraftType == null || current == null || home == null || player == null)
            return false;

        var scType = sc.spacecraftType;
        var typeKey = Data.LogisticsNetwork.TypeKey(scType.ID, scType.NameRocketType ?? "SC");
        var homeData = Data.LogisticsNetwork.Get(home);
        if (homeData == null)
            return false;

        var quota = homeData.spacecraftQuota?.Find(q =>
            Data.LogisticsNetwork.QuotaMatches(q, scType.ID, scType.NameRocketType ?? "SC"));
        var assignedProvider = Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID);
        var assignedSetting = Data.LogisticsNetwork.GetProviderSpacecraftSetting(assignedProvider, sc);
        var backhaulEnabled = assignedSetting?.backhaul ?? quota?.backhaul ?? false;
        if (!backhaulEnabled)
            return false;

        var rawCapacity = scType.GetCargoCapacity(player);
        var reserveFuelRoom = UseFuelProbeForSpacecraft(home, sc)
            ? EstimatePrePlanReturnFuel(sc, player)
            : 0;
        var capacity = Math.Max(0, rawCapacity - reserveFuelRoom);
        if (capacity <= 0)
        {
            LogVerbose($"BACKHAUL skip-capacity: ship={sc.GetSpacecraftName()} rawCapacity={rawCapacity:0.#} reserveFuelRoom={reserveFuelRoom:0.#} current={current.ObjectName} home={home.ObjectName}");
            return false;
        }

        var sourceSurplusByResource = GetBackhaulSourceSurplusByResource(current, player);
        if (sourceSurplusByResource.Count == 0)
            return false;

        var candidateRequests = new List<(ObjectInfo target, Data.LogisticsRequest req, ResourceDefinition rd, double need, double surplus)>();
        void AddRequestCandidates(ObjectInfo target)
        {
            var targetData = target != null ? Data.LogisticsNetwork.Get(target) : null;
            if (targetData?.requests == null)
                return;

            foreach (var req in targetData.requests)
            {
                if (req == null || !Data.LogisticsResourceFilter.IsSupported(req.ResourceDefinition))
                    continue;
                var rd = req.ResourceDefinition;
                if (!sourceSurplusByResource.TryGetValue(rd, out var surplus) || surplus <= 0)
                    continue;

                var oid = target.GetObjectInfoData(player);
                if (oid == null) continue;
                var stock = oid.CheckResources(rd);
                var targetAmount = req.requestedAmount;
                var inFlight = GetInFlightDeliveryAmount(target, rd, player, snapshot);
                double remaining = targetAmount - stock - inFlight;
                if (remaining <= 0)
                    continue;

                candidateRequests.Add((target, req, rd, remaining, surplus));
            }
        }

        AddRequestCandidates(home);
        if (home.objectTypes == global::Data.EObjectTypes.Orbit && home.parentObjectInfo != null)
            AddRequestCandidates(home.parentObjectInfo);

        if (candidateRequests.Count == 0)
            return false;

        candidateRequests.Sort((a, b) =>
        {
            var priorityCompare = b.req.priority.CompareTo(a.req.priority);
            if (priorityCompare != 0)
                return priorityCompare;

            var aAmount = Math.Min(a.surplus, Math.Min(a.need, capacity));
            var bAmount = Math.Min(b.surplus, Math.Min(b.need, capacity));
            return bAmount.CompareTo(aAmount);
        });

        var best = candidateRequests[0];
        backhaulRd = best.rd;
        backhaulTarget = best.target;
        var cappedCapacity = double.IsPositiveInfinity(amountLimit)
            ? capacity
            : Math.Min(capacity, Math.Max(0, amountLimit));
        backhaulAmount = Math.Min(best.surplus, Math.Min(best.need, cappedCapacity));
        if (backhaulAmount <= 0)
            return false;

        LogVerbose($"BACKHAUL matched: ship={sc.GetSpacecraftName()} rd={backhaulRd.ID} amount={backhaulAmount:0.#} surplus={best.surplus:0.#} need={best.need:0.#} capacity={capacity:0.#} limit={(double.IsPositiveInfinity(amountLimit) ? "none" : amountLimit.ToString("0.#"))} rawCapacity={rawCapacity:0.#} reserveFuelRoom={reserveFuelRoom:0.#} priority={best.req.priority} current={current.ObjectName} home={home.ObjectName} target={best.target?.ObjectName ?? "null"}");
        return true;
    }

    private static Dictionary<ResourceDefinition, double> GetBackhaulSourceSurplusByResource(ObjectInfo current, Company player)
    {
        var result = new Dictionary<ResourceDefinition, double>();
        if (current == null || player == null)
            return result;

        var currentData = Data.LogisticsNetwork.Get(current);
        if (currentData?.providers != null)
        {
            foreach (var provider in currentData.providers)
            {
                var rd = provider.ResourceDefinition;
                if (!provider.isActive || !Data.LogisticsResourceFilter.IsSupported(rd))
                    continue;

                var surplus = GetProviderAvailableAfterMinimum(current, rd, player);
                if (surplus <= 0)
                    continue;

                if (!result.TryGetValue(rd, out var existing) || surplus > existing)
                    result[rd] = surplus;
            }
        }

        var parentBody = current.objectTypes == global::Data.EObjectTypes.Orbit
            ? current.parentObjectInfo
            : null;
        var parentData = parentBody != null ? Data.LogisticsNetwork.Get(parentBody) : null;
        if (parentData?.providers != null)
        {
            var orbitStock = current.GetObjectInfoData(player);
            foreach (var provider in parentData.providers)
            {
                var rd = provider.ResourceDefinition;
                if (!provider.isActive || !provider.exportToOrbit || !Data.LogisticsResourceFilter.IsSupported(rd) || orbitStock == null)
                    continue;

                var surplus = Math.Max(0, orbitStock.CheckResources(rd) - GetCommittedStock(current, rd));
                if (surplus <= 0)
                    continue;

                if (!result.TryGetValue(rd, out var existing) || surplus > existing)
                    result[rd] = surplus;

                LogVerbose($"BACKHAUL orbit-staged-source: orbit={current.ObjectName} parent={parentBody.ObjectName} rd={rd.ID} surplus={surplus:0.#}");
            }
        }

        return result;
    }

    private static void RegisterBackhaulInFlight(ObjectInfo home, ResourceDefinition rd, double amount, PlannerSnapshot snapshot)
    {
        if (home == null || rd == null || amount <= 0)
            return;
        var key = TargetResourceKey(home, rd);
        if (key == null)
            return;
        if (snapshot?.InFlightCargoByTargetAndResource != null)
        {
            snapshot.InFlightCargoByTargetAndResource.TryGetValue(key, out var existing);
            snapshot.InFlightCargoByTargetAndResource[key] = existing + amount;
        }
    }

    private static void LogReturnBlockedOnce(ReturnHomeState state, string reason, string statusNote = null)
    {
        if (state == null)
        {
            LogWarning($"RETURNHOME blocked: {reason}");
            return;
        }

        var currentDate = (MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now).Date;
        if (state.LastBlockedReason == reason && state.LastBlockedDate == currentDate)
            return;

        state.LastBlockedReason = reason;
        state.LastBlockedStatusNote = statusNote;
        state.LastBlockedDate = currentDate;
        LogWarning($"RETURNHOME blocked: {reason}");
    }

    private static bool IsOrbitOf(ObjectInfo orbit, ObjectInfo body)
    {
        if (orbit == null || body == null) return false;
        if (body.LowOrbitCustom != null && body.LowOrbitCustom.GetObjectInfo() == orbit)
            return true;
        return orbit.objectTypes == global::Data.EObjectTypes.Orbit && orbit.parentObjectInfo == body;
    }

    internal static bool IsMoonCaseRoute(ObjectInfo a, ObjectInfo b)
    {
        // Stock CheckEarthMoonCase only works with resolved orbit/NBody positions,
        // not with surface bodies. This helper works at the surface-body level for
        // routes the logistics planner creates (e.g. EARTH -> LUNA).
        //
        // Moon case = any transfer within a local planet-moon system:
        //   planet <-> its moon, moon <-> sibling moon, orbit <-> moon, etc.
        if (a == null || b == null) return false;
        // Resolve surface bodies to their canonical (non-orbit) form
        var bodyA = GetCanonicalBody(a);
        var bodyB = GetCanonicalBody(b);
        if (bodyA == null || bodyB == null) return false;
        if (bodyA == bodyB) return false;
        // Direct parent-child: planet -> its moon or moon -> its planet
        if (bodyA.parentObjectInfo == bodyB || bodyB.parentObjectInfo == bodyA)
            return true;
        // Siblings: two moons of the same parent (planet/dwarf planet only —
        // two planets orbiting the Sun are NOT a moon case)
        if (bodyA.parentObjectInfo != null
            && bodyA.parentObjectInfo == bodyB.parentObjectInfo
            && (bodyA.parentObjectInfo.objectTypes == global::Data.EObjectTypes.Planet
                || bodyA.parentObjectInfo.objectTypes == global::Data.EObjectTypes.DwarfPlanet))
            return true;
        return false;
    }

    private static string TryCreateDeliveries(Data.LogisticsRequest req, ObjectInfo requester,
        ResourceDefinition rd, double remaining, Company player, PlannerSnapshot snapshot = null)
    {
        using (TimeScope($"TryCreateDeliveries {requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}"))
        {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null) return null;

        // Route selection is "enumerate everything, rank, then execute first valid". Do not
        // return on the first provider; that would make source order beat route quality.
        if (HasBlockedPlanningRetryCooldown(requester, rd, out var cooldownStatus))
            return cooldownStatus;

        var scActive = snapshot?.ScActive ?? new Dictionary<string, int>();
        var lvActive = snapshot?.LvActive ?? new Dictionary<string, int>();
        LogVerbose($"DISPATCH begin: target={requester?.ObjectName} rd={rd.ID} remaining={remaining:0.#} activeSC={FormatCounts(scActive)} activeLV={FormatCounts(lvActive)}");
        var bestBlocker = new PlannerBlocker();
        var candidates = BuildRouteCandidates(req, requester, rd, remaining, player, scActive, lvActive, bestBlocker, snapshot);
        if (candidates.Count == 0)
        {
            if (!HasProviderForResource(requester, rd, snapshot, req.networkId))
                LogVerbose($"DISPATCH none: target={requester?.ObjectName} rd={rd.ID} net={req.networkId} reason=no active provider with matching resource/network");
            else
                LogVerbose($"DISPATCH none: target={requester?.ObjectName} rd={rd.ID} reason={bestBlocker.Reason ?? "no usable ship/LV/provider this tick"}");
            MarkBlockedPlanningRetryCooldown(requester, rd, bestBlocker.Reason ?? "no usable ship/LV/provider this tick");
            return bestBlocker.Reason;
        }

        var orderedCandidates = candidates
            .OrderBy(c => c.Tier)
            .ThenBy(c => c.UsesLV ? 1 : 0)
            .ThenBy(c => c.HopCount)
            .ThenByDescending(c => c.Available)
            .ThenBy(c => c.EffectiveSource?.id ?? int.MaxValue)
            .ThenBy(c => c.Provider?.id ?? int.MaxValue)
            .ToList();

        // Candidate ordering is deterministic so repeated daily passes pick the same source
        // when nothing material has changed.
        if (VerboseLoggingEnabled)
            LogBepInEx($"ROUTE request: target={requester?.ObjectName} rd={rd.ID} remaining={remaining:0.#} candidates={orderedCandidates.Count}");
        foreach (var candidate in orderedCandidates)
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate: rd={rd.ID} kind={candidate.Kind} label={candidate.Label} score={candidate.Tier} usesLV={candidate.UsesLV} hops={candidate.HopCount} available={candidate.Available:0.#} amount={candidate.Amount:0.#} detail={candidate.ScoreBreakdown}");
            if (ExecuteRouteCandidate(candidate, req, requester, rd, player, snapshot))
            {
                CloseReorderLatchIfTargetCovered(req, requester, rd, player, snapshot);
                return null;
            }
        }
        if (IsMinimumShipmentStatus(req?.statusNote))
            return req.statusNote;
        var executeReason = bestBlocker.Reason ?? "all candidates failed during execution";
        if (VerboseLoggingEnabled)
            LogBepInEx($"ROUTE no-execute: target={requester?.ObjectName} rd={rd.ID} reason={executeReason}");
        MarkBlockedPlanningRetryCooldown(requester, rd, executeReason);
        return executeReason;
        }
    }

    private static bool HasProviderForResource(ObjectInfo requester, ResourceDefinition rd, PlannerSnapshot snapshot, int requestNetworkId = 0)
    {
        if (rd != null && snapshot?.ProvidersByResource != null)
        {
            if (!snapshot.ProvidersByResource.TryGetValue(rd, out var indexedProviders))
                return false;
            foreach (var oi in indexedProviders)
            {
                if (oi == null || oi == requester) continue;
                var data = Data.LogisticsNetwork.Get(oi);
                if (data != null && Data.LogisticsNetwork.HasMatchingNetworkProvider(data, rd, requestNetworkId, requester, oi))
                    return true;
            }
            return false;
        }

        foreach (var oi in snapshot?.Objects ?? Data.LogisticsNetwork.GetAllObjects())
        {
            if (oi == requester) continue;
            var data = Data.LogisticsNetwork.Get(oi);
            if (data != null && Data.LogisticsNetwork.HasMatchingNetworkProvider(data, rd, requestNetworkId, requester, oi))
                return true;
        }

        return false;
    }

    private static List<RouteCandidate> BuildRouteCandidates(Data.LogisticsRequest req, ObjectInfo requester,
        ResourceDefinition rd, double remaining, Company player,
        Dictionary<string, int> scActive, Dictionary<string, int> lvActive, PlannerBlocker bestBlocker,
        PlannerSnapshot snapshot = null)
    {
        using (TimeScope($"BuildRouteCandidates {requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}"))
        {
        var result = new List<RouteCandidate>();
        // Provider set is already resource-indexed in the snapshot. Each provider may yield
        // zero, one, or multiple route shapes depending on vehicle/LV/staging feasibility.
        var providerObjects = GetProviderObjectsForResource(rd, snapshot);
            foreach (var providerOI in providerObjects)
            {
                if (providerOI == requester) continue;

                var provData = Data.LogisticsNetwork.Get(providerOI);
                if (provData == null && TryGetExportedOrbitProviderParent(providerOI, rd, out var exportedParent))
                    provData = Data.LogisticsNetwork.Get(exportedParent);
                if (provData == null) continue;
                var matchingProviders = GetMatchingProviderRules(provData, rd, req.networkId, requester, providerOI).ToList();
                if (matchingProviders.Count == 0)
                    continue;

            var available = GetProviderAvailableAfterMinimum(providerOI, rd, player);
            LogVerbose($"DISPATCH provider: provider={providerOI?.ObjectName} rd={rd.ID} net={req.networkId} availableAfterMin={available:0.#}");
            if (available <= 0)
            {
                var noSurplusTier = ApplyProviderPriorityToTier(GetRouteTier(providerOI, requester), providerOI, rd);
                var noSurplusDetail = DescribeRouteScore(providerOI, requester, noSurplusTier);
                var diagStock = providerOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
                var diagData = Data.LogisticsNetwork.Get(providerOI);
                var diagMinKeep = diagData?.providers?
                    .Where(p => p.isActive && p.ResourceDefinition == rd)
                    .Sum(p => p.minimumKeep) ?? 0;
                var diagCommitted = GetCommittedStock(providerOI, rd);
                var noSurplusReason = LogisticsStrings.NoSurplusAtWithDetails(rd, providerOI, diagStock, diagMinKeep, diagCommitted);
                if (VerboseLoggingEnabled)
                    LogBepInEx($"ROUTE provider-skip: provider={providerOI?.ObjectName} rd={rd.ID} score={noSurplusTier} detail={noSurplusDetail} reason={noSurplusReason}");
                TrackPlannerBlocker(bestBlocker, noSurplusTier, 6, noSurplusReason);
                continue;
            }

            foreach (var providerRule in matchingProviders)
            {
                AddDirectRouteCandidates(result, req, providerRule, providerOI, requester, rd, remaining, available, player, scActive, lvActive, bestBlocker, snapshot);
                AddStagedRouteCandidate(result, req, providerRule, providerOI, requester, rd, remaining, available, player, scActive, lvActive, bestBlocker, snapshot);
            }
        }
        return result;
        }
    }

    private static IEnumerable<Data.LogisticsProvider> GetMatchingProviderRules(Data.LogisticsObjectData provData, ResourceDefinition rd, int requestNetworkId, ObjectInfo requestBody = null, ObjectInfo providerBody = null)
    {
        if (provData?.providers == null || rd == null)
            return Enumerable.Empty<Data.LogisticsProvider>();

        if (requestBody != null && providerBody != null)
        {
            return provData.providers.Where(p => p != null
                && p.isActive
                && p.ResourceDefinition == rd
                && Data.LogisticsNetwork.NetworksMatchWithLocation(requestNetworkId, p.networkId, requestBody, providerBody));
        }

        return provData.providers.Where(p => p != null
            && p.isActive
            && p.ResourceDefinition == rd
            && Data.LogisticsNetwork.NetworksMatch(requestNetworkId, p.networkId));
    }

    private static IEnumerable<ObjectInfo> GetProviderObjectsForResource(ResourceDefinition rd, PlannerSnapshot snapshot)
    {
        if (rd != null && snapshot?.ProvidersByResource != null)
        {
            return snapshot.ProvidersByResource.TryGetValue(rd, out var indexedProviders)
                ? indexedProviders
                : Enumerable.Empty<ObjectInfo>();
        }

        return snapshot?.Objects ?? Data.LogisticsNetwork.GetAllObjects();
    }

    private static void AddDirectRouteCandidates(List<RouteCandidate> result, Data.LogisticsRequest req, Data.LogisticsProvider providerRule, ObjectInfo providerOI,
        ObjectInfo requester, ResourceDefinition rd, double remaining, double available, Company player,
        Dictionary<string, int> scActive, Dictionary<string, int> lvActive, PlannerBlocker bestBlocker, PlannerSnapshot snapshot = null)
    {
        // Direct candidates include true orbit/orbit spacecraft missions and surface launch
        // missions. Low-gravity surface self-launch is treated as direct spacecraft if stock
        // thrust checks say the ship can leave without an LV.
        if (Math.Min(available, remaining) <= 0) return;
        var baseRouteTier = GetRouteTier(providerOI, requester);
        var routeTier = ApplyProviderPriorityToTier(baseRouteTier, providerOI, rd);
        var routeDetail = VerboseLoggingEnabled ? DescribeRouteScore(providerOI, requester, routeTier) + DescribePriorityScore(providerOI, rd) : null;

        var isSurfaceToOwnOrbit = IsOrbitOf(requester, providerOI);
        if (providerOI.NeedVehicleToLaunch() && !isSurfaceToOwnOrbit)
        {
            var directSurfaceShip = FindBestIdleSpacecraft(providerOI, player, scActive,
                true, out var directSurfaceShipReason, snapshot, requester, providerRule);
            var directSurfaceCapacity = directSurfaceShip?.spacecraftType?.GetCargoCapacity(player) ?? 0;
            var directSurfaceAmount = GetCandidateAmount(req, providerOI, rd, remaining, available, directSurfaceCapacity, directSurfaceShip, providerOI, providerRule);
            if (directSurfaceShip != null && directSurfaceCapacity > 0)
                directSurfaceAmount = Math.Min(directSurfaceAmount, GetSelfLaunchPayloadLimit(providerOI, directSurfaceShip, player));
            if (directSurfaceShip != null
                && directSurfaceAmount > 0
                && !RequiresLaunchVehicleForSpacecraft(providerOI, directSurfaceShip, player, directSurfaceAmount))
            {
                if (!MeetsProviderMinimumShipment(providerOI, rd, directSurfaceAmount, out var providerMinimumReason))
                {
                    TrackPlannerBlocker(bestBlocker, routeTier, 7, providerMinimumReason);
                    LogVerbose($"DISPATCH no-direct-surface-bypass: provider={providerOI.ObjectName} requester={requester.ObjectName} reason={providerMinimumReason}");
                    return;
                }
                    if (!MeetsMinimumShipment(providerOI, directSurfaceShip, directSurfaceAmount, out var minimumReason, providerRule))
                {
                    TrackPlannerBlocker(bestBlocker, routeTier, 7, minimumReason);
                    LogVerbose($"DISPATCH no-direct-surface-bypass: provider={providerOI.ObjectName} requester={requester.ObjectName} reason={minimumReason}");
                    return;
                }
                result.Add(new RouteCandidate
                {
                    Kind = RouteKind.DirectSpacecraft,
                    Provider = providerOI,
                    ProviderRule = providerRule,
                    EffectiveSource = providerOI,
                    Spacecraft = directSurfaceShip,
                    Amount = directSurfaceAmount,
                    Available = available,
                    Tier = routeTier,
                    HopCount = 1,
                    UsesLV = false,
                    Label = $"{providerOI.ObjectName} -> {requester.ObjectName}",
                    ScoreBreakdown = routeDetail + $";surfaceBypassLV=true;selfLaunchLimit={directSurfaceAmount:0.#}"
                });
                return;
            }
            if (directSurfaceShip == null && !string.IsNullOrEmpty(directSurfaceShipReason))
                LogVerbose($"DISPATCH no-direct-surface-bypass: provider={providerOI.ObjectName} requester={requester.ObjectName} reason={directSurfaceShipReason}");
        }

        if (!providerOI.NeedVehicleToLaunch())
        {
            // Try all idle spacecraft at this provider, not just the highest-capacity one.
            // A ship with lower capacity but lower (or zero) minimum shipment may succeed
            // when the largest ship's minimum exceeds the remaining request amount.
            var candidates = FindAllIdleSpacecraft(providerOI, player, scActive, requireNonContainer: false,
                out var spacecraftReason, snapshot, requester, providerRule);
            if (candidates.Count > 0)
            {
                foreach (var sc in candidates)
                {
                    var capacity = sc.spacecraftType?.GetCargoCapacity(player) ?? 0;
                    if (capacity <= 0) continue;
                    var candidateAmount = GetCandidateAmount(req, providerOI, rd, remaining, available, capacity, sc, providerOI, providerRule);
                    if (!MeetsProviderMinimumShipment(providerOI, rd, candidateAmount, out var providerMinimumReason))
                    {
                        if (VerboseLoggingEnabled)
                            LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSpacecraft} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={providerMinimumReason} ship={sc.GetSpacecraftName()}");
                        TrackPlannerBlocker(bestBlocker, routeTier, 7, providerMinimumReason);
                        continue;
                    }
                    if (!MeetsMinimumShipment(providerOI, sc, candidateAmount, out var minimumReason, providerRule))
                    {
                        if (VerboseLoggingEnabled)
                            LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSpacecraft} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={minimumReason} ship={sc.GetSpacecraftName()}");
                        TrackPlannerBlocker(bestBlocker, routeTier, 7, minimumReason);
                        continue;
                    }
                    result.Add(new RouteCandidate
                    {
                        Kind = RouteKind.DirectSpacecraft,
                        Provider = providerOI,
                        ProviderRule = providerRule,
                        EffectiveSource = providerOI,
                        Spacecraft = sc,
                        Amount = candidateAmount,
                        Available = available,
                        Tier = routeTier,
                        HopCount = 1,
                        UsesLV = false,
                        Label = $"{providerOI.ObjectName} -> {requester.ObjectName}",
                        ScoreBreakdown = routeDetail
                    });
                    break;
                }
            }
            else if (!string.IsNullOrEmpty(spacecraftReason))
            {
                if (VerboseLoggingEnabled)
                    LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSpacecraft} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={spacecraftReason}");
                TrackPlannerBlocker(bestBlocker, routeTier, 3, spacecraftReason);
            }
            return;
        }

        if (!TryFindSurfaceLaunch(providerOI, requester, player, scActive, lvActive, isSurfaceToOwnOrbit,
                !isSurfaceToOwnOrbit, out var lvType, out var carrier, out var launchReason, out var launchSupportDetail, out var launchSupportAdjustment, snapshot, providerRule))
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSurfaceLaunch} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={launchReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 2, launchReason);
            return;
        }

        routeTier += launchSupportAdjustment;
        routeDetail = VerboseLoggingEnabled ? DescribeRouteScore(providerOI, requester, routeTier, launchSupportAdjustment) : null;
        if (VerboseLoggingEnabled)
            routeDetail += DescribePriorityScore(providerOI, rd);

        var scCapacity = carrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (scCapacity <= 0)
        {
            var capacityReason = LogisticsStrings.NoCargoCapacityFrom(providerOI);
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSurfaceLaunch} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={capacityReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 4, capacityReason);
            return;
        }

        var surfaceLaunchAmount = GetCandidateAmount(req, providerOI, rd, remaining, available, scCapacity, carrier, providerOI, providerRule);
        if (!MeetsProviderMinimumShipment(providerOI, rd, surfaceLaunchAmount, out var providerSurfaceMinimumReason))
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSurfaceLaunch} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={providerSurfaceMinimumReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 7, providerSurfaceMinimumReason);
            // Don't return — let staged route for this provider still be tried.
            return;
        }
        if (!MeetsMinimumShipment(providerOI, carrier, surfaceLaunchAmount, out var surfaceMinimumReason, providerRule))
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.DirectSurfaceLaunch} label={providerOI.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={surfaceMinimumReason} ship={carrier.GetSpacecraftName()}");
            TrackPlannerBlocker(bestBlocker, routeTier, 7, surfaceMinimumReason);
            // Don't return — staged route for this provider may use a different carrier.
            // (TryFindSurfaceLaunch picks one carrier; staged routes resolve their own.)
            return;
        }

        result.Add(new RouteCandidate
        {
            Kind = RouteKind.DirectSurfaceLaunch,
            Provider = providerOI,
            ProviderRule = providerRule,
            EffectiveSource = providerOI,
            LaunchVehicleType = lvType,
            Spacecraft = carrier,
            Amount = surfaceLaunchAmount,
            Available = available,
            Tier = routeTier,
            HopCount = 1,
            UsesLV = true,
            Label = $"{providerOI.ObjectName} -> {requester.ObjectName}",
            ScoreBreakdown = string.IsNullOrWhiteSpace(launchSupportDetail) ? routeDetail : $"{routeDetail};launchSupport={launchSupportDetail}"
        });
    }

    private static void AddStagedRouteCandidate(List<RouteCandidate> result, Data.LogisticsRequest req, Data.LogisticsProvider providerRule, ObjectInfo providerOI,
        ObjectInfo requester, ResourceDefinition rd, double remaining, double available, Company player,
        Dictionary<string, int> scActive, Dictionary<string, int> lvActive, PlannerBlocker bestBlocker, PlannerSnapshot snapshot = null)
    {
        // V1 staging has exactly one relay: source surface -> source orbit by LOC/LV, then
        // source orbit -> final destination by a regular spacecraft. No graph search here.
        if (!providerOI.NeedVehicleToLaunch()) return;
        if (requester == null || providerOI == null) return;
        if (IsOrbitOf(requester, providerOI)) return;

        var sourceOrbit = providerOI.LowOrbitCustom?.GetObjectInfo();
        if (sourceOrbit == null)
        {
            var noOrbitReason = LogisticsStrings.NoSourceOrbitAt(providerOI);
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> [orbit missing] -> {requester.ObjectName} score=5 detail=no-source-orbit reason={noOrbitReason}");
            TrackPlannerBlocker(bestBlocker, 5, 5, noOrbitReason);
            return;
        }
        var baseRouteTier = GetRouteTier(sourceOrbit, requester);
        var routeTier = ApplyProviderPriorityToTier(baseRouteTier, providerOI, rd);
        var routeDetail = VerboseLoggingEnabled ? DescribeRouteScore(sourceOrbit, requester, routeTier) + DescribePriorityScore(providerOI, rd) : null;

        if (!TryFindSurfaceLaunch(providerOI, sourceOrbit, player, scActive, lvActive, true,
                false, out var stageLvType, out var stageCarrier, out var stageReason, out var stageSupportDetail, out var stageSupportAdjustment, snapshot, providerRule))
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={stageReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 2, stageReason);
            return;
        }

        routeTier += stageSupportAdjustment;
        routeDetail = VerboseLoggingEnabled ? DescribeRouteScore(sourceOrbit, requester, routeTier, stageSupportAdjustment) : null;
        if (VerboseLoggingEnabled)
            routeDetail += DescribePriorityScore(providerOI, rd);

        var finalCarrier = FindBestIdleSpacecraft(sourceOrbit, player, scActive, requireNonContainer: true,
            out var finalCarrierReason, snapshot, requester, providerRule);
        var stageCapacity = stageCarrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        var finalCapacity = finalCarrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (stageCapacity <= 0)
        {
            var stageCapacityReason = LogisticsStrings.NoOrbitalPayloadCapacityFrom(providerOI);
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={stageCapacityReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 4, stageCapacityReason);
            return;
        }
        if (finalCapacity <= 0)
        {
            var finalReason = finalCarrierReason ?? LogisticsStrings.NoSpacecraftAvailableAt(sourceOrbit);
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={finalReason}");
            var missingOptionalStagingSpacecraft = IsMissingOptionalStagingSpacecraftReason(finalReason);
            TrackPlannerBlocker(bestBlocker, missingOptionalStagingSpacecraft ? routeTier + 100 : routeTier,
                missingOptionalStagingSpacecraft ? 9 : 3, finalReason);
            return;
        }

        var amount = GetCandidateAmount(req, providerOI, rd, remaining, available,
            Math.Min(stageCapacity, finalCapacity), finalCarrier, sourceOrbit, providerRule);
        if (amount <= 0) return;
        if (!MeetsProviderMinimumShipment(providerOI, rd, amount, out var providerMinimumReason))
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={providerMinimumReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 7, providerMinimumReason);
            return;
        }
        if (!MeetsMinimumShipment(sourceOrbit, finalCarrier, amount, out var minimumReason, providerRule))
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={minimumReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 7, minimumReason);
            return;
        }

        result.Add(new RouteCandidate
        {
            Kind = RouteKind.StageSourceSurfaceToOrbit,
            Provider = providerOI,
            ProviderRule = providerRule,
            EffectiveSource = sourceOrbit,
            StageOrbit = sourceOrbit,
            StageCarrier = stageCarrier,
            FinalCarrier = finalCarrier,
            LaunchVehicleType = stageLvType,
            Amount = amount,
            Available = available,
            Tier = routeTier,
            HopCount = 2,
            UsesLV = true,
            Label = $"{providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName}",
            ScoreBreakdown = string.IsNullOrWhiteSpace(stageSupportDetail) ? routeDetail : $"{routeDetail};launchSupport={stageSupportDetail}"
        });
    }

    private static bool ExecuteRouteCandidate(RouteCandidate candidate, Data.LogisticsRequest req,
        ObjectInfo requester, ResourceDefinition rd, Company player, PlannerSnapshot snapshot = null)
    {
        using (TimeScope($"ExecuteRouteCandidate {candidate?.Kind.ToString() ?? "null"} {requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}"))
        {
        if (candidate == null || req == null || requester == null || rd == null || player == null)
            return false;

        // Candidate execution is the first point that mutates stock/logistics state. All
        // feasibility checks before this should be side-effect free except diagnostics.
        switch (candidate.Kind)
        {
            case RouteKind.DirectSpacecraft:
                if (SetupDirectCycleMission(req, candidate.Spacecraft, rd, candidate.Amount, requester, candidate.Provider,
                        out var blockedFuelType, out var blockedFuelShortfall, providerRule: candidate.ProviderRule))
                {
                    RecordDispatchInSnapshot(snapshot, candidate.Spacecraft, null);
                    ClearRelayState(req);
                    if (VerboseLoggingEnabled)
                    {
                        LogVerbose($"PROC ranked: {rd.ID} x{candidate.Amount:0.#} {candidate.Label} kind={candidate.Kind}");
                        LogBepInEx($"ROUTE chosen: rd={rd.ID} kind={candidate.Kind} label={candidate.Label} score={candidate.Tier} detail={candidate.ScoreBreakdown}");
                    }
                    return true;
                }
                if (IsWaitingForReturnFuelProbe(req))
                    return true;
                return TryCreateFuelBootstrapDelivery(req, requester, rd, blockedFuelType, blockedFuelShortfall, player);

            case RouteKind.DirectSurfaceLaunch:
                if (IsOrbitOf(requester, candidate.Provider))
                    candidate.Spacecraft = GetCyclicalOrbitalContainer(player);
                if (candidate.Spacecraft == null)
                    return false;
                var directLocToOwnOrbit = IsOrbitOf(requester, candidate.Provider)
                    && candidate.Spacecraft.spacecraftType?.LowOrbitContainer == true;
                if (SetupCycleMission(req, candidate.Spacecraft, rd, candidate.Amount, requester, candidate.Provider,
                        candidate.LaunchVehicleType, out blockedFuelType, out blockedFuelShortfall,
                        pendingTargetOI: directLocToOwnOrbit ? candidate.Provider : null, providerRule: candidate.ProviderRule))
                {
                    RecordDispatchInSnapshot(snapshot, candidate.Spacecraft, candidate.LaunchVehicleType);
                    ClearRelayState(req);
                    if (VerboseLoggingEnabled)
                    {
                        LogVerbose($"PROC ranked: {rd.ID} x{candidate.Amount:0.#} {candidate.Label} kind={candidate.Kind}");
                        LogBepInEx($"ROUTE chosen: rd={rd.ID} kind={candidate.Kind} label={candidate.Label} score={candidate.Tier} detail={candidate.ScoreBreakdown}");
                    }
                    return true;
                }
                if (IsWaitingForReturnFuelProbe(req))
                    return true;
                return TryCreateFuelBootstrapDelivery(req, requester, rd, blockedFuelType, blockedFuelShortfall, player);

            case RouteKind.StageSourceSurfaceToOrbit:
                if (candidate.StageCarrier == null || candidate.StageCarrier.spacecraftType?.LowOrbitContainer == true)
                    candidate.StageCarrier = GetCyclicalOrbitalContainer(player);
                if (candidate.StageCarrier == null)
                    return false;
                SetRelayState(req, Data.RelayStage.WaitingForSourceOrbitStock, candidate.Provider, candidate.StageOrbit, requester);
                if (SetupCycleMission(req, candidate.StageCarrier, rd, candidate.Amount, candidate.StageOrbit, candidate.Provider,
                        candidate.LaunchVehicleType, out blockedFuelType, out blockedFuelShortfall,
                        accountingTargetOI: requester, pendingTargetOI: candidate.StageOrbit, providerRule: candidate.ProviderRule))
                {
                    RecordDispatchInSnapshot(snapshot, candidate.StageCarrier, candidate.LaunchVehicleType);
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    req.statusNote = LogisticsStrings.StagingTo(candidate.StageOrbit);
                    if (VerboseLoggingEnabled)
                    {
                        LogVerbose($"PROC ranked: {rd.ID} x{candidate.Amount:0.#} {candidate.Label} kind={candidate.Kind}");
                        LogBepInEx($"ROUTE chosen: rd={rd.ID} kind={candidate.Kind} label={candidate.Label} score={candidate.Tier} detail={candidate.ScoreBreakdown}");
                    }
                    return true;
                }

                if (IsWaitingForReturnFuelProbe(req))
                    return true;
                ClearRelayState(req);
                return TryCreateFuelBootstrapDelivery(req, requester, rd, blockedFuelType, blockedFuelShortfall, player);
        }

        return false;
        }
    }

    private static bool TryCreateRelayFinalDelivery(Data.LogisticsRequest req, ObjectInfo requester,
        ObjectInfo sourceOrbit, ResourceDefinition rd, double remaining, Company player, PlannerSnapshot snapshot = null,
        Data.LogisticsProvider providerRule = null)
    {
        // Second half of the staged route. This only fires after enough stock is visible at
        // source orbit; the player still sees one GET request rather than two child requests.
        if (HasRoutePlanningLock(sourceOrbit, requester, rd, player, out var lockStatus))
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = lockStatus;
            return true;
        }

        var scActive = snapshot?.ScActive ?? new Dictionary<string, int>();
        var carrier = FindBestIdleSpacecraft(sourceOrbit, player, scActive, requireNonContainer: true, out _, snapshot, requester, providerRule);
        var cap = carrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (carrier == null || cap <= 0)
            return false;

        var stagedAvailable = sourceOrbit.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
        stagedAvailable = Math.Max(0, stagedAvailable - GetCommittedStock(sourceOrbit, rd));
        var amount = GetCandidateAmount(req, sourceOrbit, rd, remaining, stagedAvailable, cap, carrier, sourceOrbit, providerRule);
        if (amount <= 0)
            return false;
        if (!MeetsMinimumShipment(sourceOrbit, carrier, amount, out var minimumReason, providerRule))
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = minimumReason;
            LogVerbose($"RELAY final-leg-wait-minimum: rd={rd.ID} sourceOrbit={sourceOrbit.ObjectName} target={requester.ObjectName} amount={amount:0.#} reason={minimumReason}");
            return true;
        }

        if (!SetupDirectCycleMission(req, carrier, rd, amount, requester, sourceOrbit,
                out var blockedFuelType, out var blockedFuelShortfall,
                lvTypeA: null, accountingTargetOI: requester, pendingTargetOI: requester, providerRule: providerRule))
        {
            if (IsWaitingForReturnFuelProbe(req))
                return true;
            return TryCreateFuelBootstrapDelivery(req, sourceOrbit, rd, blockedFuelType, blockedFuelShortfall, player);
        }

        RecordDispatchInSnapshot(snapshot, carrier, null);
        req.relayStage = Data.RelayStage.WaitingForFinalLeg;
        req.status = Data.LogisticsRequestStatus.InProgress;
        req.statusNote = LogisticsStrings.ShippingFrom(sourceOrbit);
        if (VerboseLoggingEnabled)
            LogVerbose($"RELAY final-leg-dispatch: rd={rd.ID} sourceOrbit={sourceOrbit.ObjectName} target={requester.ObjectName} amount={amount:0.#}");
        return true;
    }

    private static bool IsWaitingForReturnFuelProbe(Data.LogisticsRequest req)
    {
        return req != null
            && req.status == Data.LogisticsRequestStatus.InProgress
            && string.Equals(req.statusNote, "Calculating return fuel reserve", StringComparison.Ordinal);
    }

    private static Spacecraft FindBestIdleSpacecraft(ObjectInfo location, Company player,
        Dictionary<string, int> scActive, bool requireNonContainer, out string reason, PlannerSnapshot snapshot = null,
        ObjectInfo routeTarget = null, Data.LogisticsProvider providerRule = null)
    {
        var candidates = FindAllIdleSpacecraft(location, player, scActive, requireNonContainer, out reason, snapshot, routeTarget, providerRule);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static List<Spacecraft> FindAllIdleSpacecraft(ObjectInfo location, Company player,
        Dictionary<string, int> scActive, bool requireNonContainer, out string reason, PlannerSnapshot snapshot = null,
        ObjectInfo routeTarget = null, Data.LogisticsProvider providerRule = null)
    {
        reason = null;
        var result = new List<Spacecraft>();
        if (location == null || player == null) return result;
        var allShips = GetShipsAtLocation(location, player, snapshot)
            .Where(sc => sc != null && sc.spacecraftType != null
                && (!requireNonContainer || !sc.spacecraftType.LowOrbitContainer))
            .ToList();
        if (allShips.Count == 0)
        {
            reason = LogisticsStrings.NoSpacecraftPresentAt(location);
            return result;
        }

        var committedIds = snapshot?.CommittedShipIds;
        var seen = new HashSet<int>();
        var assignedIds = providerRule?.assignedSpacecraftIds != null
            ? new HashSet<int>(providerRule.assignedSpacecraftIds.Where(id => id >= 0))
            : new HashSet<int>();
        var hasProviderAssignments = assignedIds.Count > 0;

        foreach (var sc in allShips
            .Where(sc => assignedIds.Contains(sc.ID))
            .Where(sc => IsSpacecraftInRangeForRoute(sc, routeTarget, player))
            .Where(sc => IsSpacecraftAvailableForLogistics(sc, player, committedIds))
            .OrderByDescending(sc => sc.spacecraftType.GetCargoCapacity(player)))
        {
            if (sc.ID >= 0 && seen.Add(sc.ID))
                result.Add(sc);
        }

        if (providerRule != null && !providerRule.useSharedSpacecraftPool)
        {
            if (result.Count == 0)
                reason = hasProviderAssignments
                    ? $"Assigned spacecraft unavailable at {location.ObjectName}"
                    : $"No spacecraft assigned to this SEND order at {location.ObjectName}";
            return result;
        }

        // Shared quota availability is based on ships physically at this location minus
        // logistics ownership/active-cycle commitments and provider-specific assignments.
        var data = Data.LogisticsNetwork.Get(location);
        if (data == null)
        {
            if (result.Count == 0)
                reason = LogisticsStrings.NoSpacecraftLogisticsAt(location);
            return result;
        }

        var quotas = data.spacecraftQuota.Where(q => q.count > 0).ToList();
        if (quotas.Count == 0)
        {
            if (result.Count == 0)
                reason = LogisticsStrings.NoSpacecraftQuotaAt(location);
            return result;
        }

        var quotaExhausted = false;
        var matchingPresent = false;
        var rangeLimited = false;
        var idleMatchingPresent = false;

        foreach (var quota in quotas)
        {
            var matchingShips = allShips
                .Where(sc => Data.LogisticsNetwork.QuotaMatches(quota, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"))
                .Where(sc => !Data.LogisticsNetwork.IsSpacecraftAssignedToProvider(sc.ID, providerRule))
                .Where(sc => !Data.LogisticsNetwork.IsSpacecraftAssignedToOtherProvider(sc.ID, providerRule))
                .ToList();
            if (matchingShips.Count == 0)
                continue;

            matchingPresent = true;
            var shipsInRange = matchingShips
                .Where(sc => IsSpacecraftInRangeForRoute(sc, routeTarget, player))
                .ToList();
            if (shipsInRange.Count == 0)
            {
                rangeLimited = true;
                continue;
            }

            var committedAtLocation = matchingShips.Count(sc =>
                IsSpacecraftAlreadyCommitted(sc, player, out _, committedShipIds: committedIds));
            var canUse = quota.count - committedAtLocation;
            if (canUse <= 0)
            {
                quotaExhausted = true;
                continue;
            }

            foreach (var sc in shipsInRange
                .Where(sc => IsSpacecraftAvailableForLogistics(sc, player, committedIds))
                .OrderByDescending(sc => sc.spacecraftType.GetCargoCapacity(player)))
            {
                idleMatchingPresent = true;
                if (sc.ID >= 0 && !seen.Add(sc.ID))
                    continue;
                result.Add(sc);
            }
        }

        if (result.Count == 0)
        {
            if (!matchingPresent)
                reason = LogisticsStrings.NoMatchingSpacecraftAt(location);
            else if (rangeLimited)
                reason = LogisticsStrings.NoSpacecraftInRange(location, routeTarget);
            else if (quotaExhausted)
                reason = LogisticsStrings.AllSpacecraftQuotaInUseAt(location);
            else if (!idleMatchingPresent)
                reason = LogisticsStrings.NoIdleSpacecraftAt(location);
            else
                reason = LogisticsStrings.NoSpacecraftAvailableAt(location);
        }
        return result;
    }

    private static bool IsSpacecraftInRangeForRoute(Spacecraft sc, ObjectInfo routeTarget, Company player)
    {
        var type = sc?.spacecraftType;
        if (type == null || player == null || routeTarget == null || !type.SolarSC)
            return true;

        var solarRange = type.GetSolarRange(player);
        var targetDistance = routeTarget.DistanceToSunInAU;
        var inRange = solarRange + 0.0001f >= targetDistance;
        if (!inRange)
            LogVerbose($"SOLAR range-block: ship={sc.GetSpacecraftName()} type={type.NameRocketType} target={routeTarget.ObjectName} rangeAU={solarRange:0.###} targetAU={targetDistance:0.###}");
        return inRange;
    }

    private static IEnumerable<Spacecraft> GetShipsAtLocation(ObjectInfo location, Company player, PlannerSnapshot snapshot = null)
    {
        if (location == null || player == null)
            return Enumerable.Empty<Spacecraft>();

        if (snapshot?.ShipsByObjectId != null
            && snapshot.ShipsByObjectId.TryGetValue(location.id, out var indexedShips))
        {
            return indexedShips;
        }

        var ships = snapshot?.Ships
            ?? MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();
        return ships.Where(sc => sc != null
            && sc.GetCompany() == player
            && sc.CurrentlyOnThisObject == location);
    }

    private static bool TryFindSurfaceLaunch(ObjectInfo providerOI, ObjectInfo targetOI, Company player,
        Dictionary<string, int> scActive, Dictionary<string, int> lvActive, bool requireContainerOnly, bool requireRegularSC,
        out LaunchVehicleType lvType, out Spacecraft carrier, out string reason, out string supportDetail,
        out int supportTierAdjustment, PlannerSnapshot snapshot = null, Data.LogisticsProvider providerRule = null)
    {
        lvType = null;
        carrier = null;
        reason = null;
        supportDetail = null;
        supportTierAdjustment = 0;
        if (providerOI == null || player == null || !providerOI.NeedVehicleToLaunch())
        {
            reason = providerOI == null ? LogisticsStrings.NoProviderSelected() : LogisticsStrings.NoSurfaceLaunchPathFrom(providerOI);
            return false;
        }

        var provData = Data.LogisticsNetwork.Get(providerOI);
        if (provData == null)
        {
            reason = LogisticsStrings.NoLogisticsDataAt(providerOI);
            return false;
        }

        var lvQuotas = provData.launchVehicleQuota.Where(q => q.count > 0).ToList();
        if (lvQuotas.Count == 0)
        {
            reason = LogisticsStrings.NoLvQuotaAt(providerOI, DescribeAvailableLaunchSupport(providerOI, player, snapshot));
            return false;
        }

        // GetAvailableLaunchSupport folds stock LVs and fake/facility launch vehicles into
        // one comparable list so staging and direct surface launches use identical rules.
        var allReadyLV = GetAvailableLaunchSupport(providerOI, player, snapshot)
            .Where(option => option?.Vehicle != null
                && option.Type != null
                && option.Vehicle.GetCompany() == player
                && option.Vehicle.objectInfo == providerOI
                && option.Vehicle.IsReadyToLaunchReusable())
            .ToList();
        if (allReadyLV.Count == 0)
        {
            reason = LogisticsStrings.NoReadyLvAt(providerOI, DescribeAvailableLaunchSupport(providerOI, player, snapshot));
            return false;
        }

        var matchingReadyLV = allReadyLV
            .Where(option => lvQuotas.Any(q => Data.LogisticsNetwork.QuotaMatches(q, option.Type.ID, option.Type.Name ?? "LV")))
            .ToList();
        if (matchingReadyLV.Count == 0)
        {
            reason = LogisticsStrings.NoMatchingLvQuotaAt(providerOI, DescribeAvailableLaunchSupport(providerOI, player, snapshot));
            return false;
        }

        var quotaExhausted = false;

        var availableLV = matchingReadyLV
            .Where(option =>
            {
                // LV quotas are UI toggles. A positive quota enables all ready matching LVs at this body.
                var allowed = matchingReadyLV.Count(readyOption =>
                    readyOption?.Type != null
                    && lvQuotas.Any(q => Data.LogisticsNetwork.QuotaMatches(q, readyOption.Type.ID, readyOption.Type.Name ?? "LV"))
                    && SameLaunchVehicleType(readyOption.Type, option.Type));
                var active = CountActiveLaunchVehicleUsesAt(providerOI, option.Type, player, snapshot);
                if (active >= allowed)
                    quotaExhausted = true;
                return active < allowed;
            })
            .OrderBy(option => option.TierAdjustment)
            .ThenBy(option => option.IsFacilityBacked ? 0 : 1)
            .ThenBy(option => option.Type?.Name ?? "LV", StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (availableLV.Count == 0)
        {
            reason = quotaExhausted
                ? LogisticsStrings.AllLvQuotaInUseAt(providerOI)
                : LogisticsStrings.NoLvAvailableAt(providerOI);
            return false;
        }

        lvType = availableLV[0].Type;
        supportDetail = availableLV[0].Label;
        supportTierAdjustment = availableLV[0].TierAdjustment;
        if (requireContainerOnly)
        {
            carrier = PeekCyclicalOrbitalContainer(player) ?? GetCyclicalOrbitalContainer(player);
            if (carrier == null)
            {
                reason = LogisticsStrings.NoOrbitalContainerAt(providerOI);
                return false;
            }
            return true;
        }

        carrier = FindBestIdleSpacecraft(providerOI, player, scActive, requireNonContainer: requireRegularSC,
            out var carrierReason, snapshot, targetOI, providerRule);
        if (carrier == null)
            reason = carrierReason ?? LogisticsStrings.NoIdleSpacecraftAt(providerOI);
        return carrier != null;
    }

    private static int CountActiveLaunchVehicleUsesAt(ObjectInfo origin, LaunchVehicleType lvType, Company player, PlannerSnapshot snapshot = null)
    {
        if (origin == null || lvType == null || player == null)
            return 0;

        var key = ActiveLaunchVehicleUseKey(origin, lvType);
        if (key != null && snapshot?.ActiveLvUsesByOriginAndType != null
            && snapshot.ActiveLvUsesByOriginAndType.TryGetValue(key, out var indexedCount))
        {
            return indexedCount;
        }

        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        var cycles = snapshot?.Cycles ?? cm?.GetAllCycleMission(player);
        if (cycles == null)
            return 0;

        var count = 0;
        foreach (var cmd in cycles)
        {
            if (cmd == null || cmd.CheckComplete()) continue;
            if (!IsLogisticsMission(cmd)) continue;
            if (cmd.A == origin && SameLaunchVehicleType(cmd.LvTypeA, lvType))
                count++;
            if (cmd.B == origin && SameLaunchVehicleType(cmd.LvTypeB, lvType))
                count++;
        }
        return count;
    }

    private static bool SameLaunchVehicleType(LaunchVehicleType a, LaunchVehicleType b)
    {
        if (a == null || b == null) return false;
        if (!string.IsNullOrEmpty(a.ID) && !string.IsNullOrEmpty(b.ID))
            return string.Equals(a.ID, b.ID, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<LaunchSupportOption> GetAvailableLaunchSupport(ObjectInfo providerOI, Company player, PlannerSnapshot snapshot = null)
    {
        if (providerOI == null || player == null)
            return new List<LaunchSupportOption>();

        // Cached per snapshot because facility support discovery touches stock object data
        // and may run for many provider/request combinations in the same daily pass.
        if (snapshot != null && providerOI.id > 0)
        {
            if (snapshot.LaunchSupportByObjectId.TryGetValue(providerOI.id, out var cached))
                return cached;

            var computed = BuildAvailableLaunchSupport(providerOI, player);
            snapshot.LaunchSupportByObjectId[providerOI.id] = computed;
            return computed;
        }

        return BuildAvailableLaunchSupport(providerOI, player);
    }

    private static List<LaunchSupportOption> BuildAvailableLaunchSupport(ObjectInfo providerOI, Company player)
    {
        var objectData = providerOI.GetObjectInfoData(player);
        var seen = new HashSet<int>();
        var result = new List<LaunchSupportOption>();

        // Primary: stock GetListLaunchVehicle (includes most standard LVs)
        var rows = providerOI.GetListLaunchVehicle(player);
        if (rows != null)
        {
            foreach (var row in rows)
            {
                if (row?.launchVehicle == null || row.launchVehicle.launchVehicleType == null) continue;
                if (!seen.Add(row.launchVehicle.ID)) continue;
                var facility = objectData?.GetFakeLVFromFacilityReverse(row.launchVehicle);
                var category = GetLaunchSupportCategory(providerOI, row.launchVehicle, facility);
                result.Add(new LaunchSupportOption
                {
                    Vehicle = row.launchVehicle,
                    Type = row.launchVehicle.launchVehicleType,
                    Facility = facility,
                    Category = category,
                    IsFacilityBacked = facility != null,
                    Label = BuildLaunchSupportLabel(row.launchVehicle, facility, category),
                    TierAdjustment = GetLaunchSupportTierAdjustment(category)
                });
            }
        }

        // Fallback: inspect the body's own LV list instead of scanning the whole scene.
        // Stock facility LVs are inserted into ObjectInfo.ListLaunchVehicle when their fake LV is created.
        foreach (var lv in providerOI.ListLaunchVehicle)
        {
            if (lv == null || lv.launchVehicleType == null) continue;
            if (lv.GetCompany() != player) continue;
            if (lv.objectInfo != providerOI) continue;
            if (!seen.Add(lv.ID)) continue;
            var facility = objectData?.GetFakeLVFromFacilityReverse(lv);
            var category = GetLaunchSupportCategory(providerOI, lv, facility);
            result.Add(new LaunchSupportOption
            {
                Vehicle = lv,
                Type = lv.launchVehicleType,
                Facility = facility,
                Category = category,
                IsFacilityBacked = facility != null,
                Label = BuildLaunchSupportLabel(lv, facility, category),
                TierAdjustment = GetLaunchSupportTierAdjustment(category)
            });
        }

        return result;
    }

    private static string DescribeAvailableLaunchSupport(ObjectInfo providerOI, Company player, PlannerSnapshot snapshot = null)
    {
        var support = GetAvailableLaunchSupport(providerOI, player, snapshot);
        if (support.Count == 0)
        {
            if (providerOI?.IsUseInSpaceElevator == true && providerOI.parentObjectInfo?.LowOrbitCustom != null)
                return $"; special-launch=space-elevator->{providerOI.parentObjectInfo.LowOrbitCustom.GetObjectInfo()?.ObjectName}";
            return string.Empty;
        }

        var labels = string.Join(", ", support
            .Select(option => option.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct()
            .Take(6));

        var elevator = providerOI?.IsUseInSpaceElevator == true && providerOI.parentObjectInfo?.LowOrbitCustom != null
            ? $", space-elevator->{providerOI.parentObjectInfo.LowOrbitCustom.GetObjectInfo()?.ObjectName}"
            : string.Empty;

        return string.IsNullOrWhiteSpace(labels)
            ? string.Empty
            : $"; available launch support={labels}{elevator}";
    }

    private static string BuildLaunchSupportLabel(LaunchVehicle lv, Facility facility, string category)
    {
        var lvName = lv?.launchVehicleType?.Name ?? "LV";
        if (facility != null)
        {
            var facilityName = facility.facilityDescriptor?.GetText(longText: false) ?? facility.GetType().Name;
            return $"{lvName} via {facilityName} [{category}]";
        }

        if (!string.IsNullOrWhiteSpace(category) && category != "standard-launch")
            return $"{lvName} [{category}]";

        return lvName;
    }

    private static string GetLaunchSupportCategory(ObjectInfo providerOI, LaunchVehicle lv, Facility facility)
    {
        if (facility != null)
        {
            var facilityName = facility.facilityDescriptor?.GetText(longText: false) ?? facility.GetType().Name;
            return ClassifyLaunchSupport(facilityName, lv?.launchVehicleType?.Name ?? "LV");
        }

        if (providerOI?.IsUseInSpaceElevator == true && providerOI.parentObjectInfo?.LowOrbitCustom != null)
            return "space-elevator";

        return "standard-launch";
    }

    private static string ClassifyLaunchSupport(string facilityName, string lvName)
    {
        var text = $"{facilityName} {lvName}".ToLowerInvariant();
        if (text.Contains("elevator"))
            return "space-elevator";
        if (text.Contains("spin"))
            return "spin-launch";
        if (text.Contains("magnetic") || text.Contains("rail") || text.Contains("catapult") || text.Contains("mass driver"))
            return "magnetic-rail";
        return "facility-launch";
    }

    private static int GetLaunchSupportTierAdjustment(string category)
    {
        switch (category)
        {
            case "space-elevator":
                return -45;
            case "spin-launch":
                return -40;
            case "magnetic-rail":
                return -38;
            case "facility-launch":
                return -24;
            default:
                return 0;
        }
    }

    private static void TrackPlannerBlocker(PlannerBlocker bestBlocker, int tier, int priority, string reason)
    {
        if (bestBlocker == null || string.IsNullOrEmpty(reason))
            return;
        if (tier < bestBlocker.Tier
            || (tier == bestBlocker.Tier && priority < bestBlocker.Priority)
            || (tier == bestBlocker.Tier && priority == bestBlocker.Priority && string.IsNullOrEmpty(bestBlocker.Reason)))
        {
            bestBlocker.Tier = tier;
            bestBlocker.Priority = priority;
            bestBlocker.Reason = reason;
        }
    }

    private static bool IsNoLogisticsDataReason(string reason)
    {
        return !string.IsNullOrWhiteSpace(reason)
            && reason.IndexOf("No logistics data", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsMissingOptionalStagingSpacecraftReason(string reason)
    {
        return !string.IsNullOrWhiteSpace(reason)
            && (reason.IndexOf("No logistics data", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("No spacecraft logistics", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static Spacecraft PeekCyclicalOrbitalContainer(Company player)
    {
        // The stock low-orbit payload container is not a finite logistics spacecraft.
        // Use the shared instance only as a type/capacity reference; execution creates
        // a dedicated cyclical container instance for each LOC mission.
        return MonoBehaviourSingleton<ShipManager>.Instance?.GetLowOrbitContainer(player);
    }

    private static Spacecraft GetCyclicalOrbitalContainer(Company player)
    {
        var carrier = MonoBehaviourSingleton<ShipManager>.Instance?.AddOrbitalContainerForCyclicalMission(player);
        if (carrier != null && IsSpacecraftAvailableForLogistics(carrier, player))
            return carrier;
        return carrier ?? PeekCyclicalOrbitalContainer(player);
    }

    private static int GetRouteTier(ObjectInfo effectiveSource, ObjectInfo target)
    {
        // Heuristic route score only depends on source/target object context, so cache it
        // across requests. Lower is better; launch support and tie-breakers are applied later.
        if (effectiveSource == null || target == null)
            return int.MaxValue / 2;
        var key = $"{effectiveSource.id}->{target.id}";
        if (_routeTierCache.TryGetValue(key, out var cachedTier))
            return cachedTier;

        var sourcePenalty = GetSourceWellPenalty(effectiveSource);
        var relationPenalty = target.objectTypes == global::Data.EObjectTypes.Orbit
            ? GetOrbitTargetTier(effectiveSource, target)
            : GetSurfaceTargetTier(effectiveSource, target);
        var tier = sourcePenalty + relationPenalty;
        _routeTierCache[key] = tier;
        return tier;
    }

    private static string DescribeRouteScore(ObjectInfo effectiveSource, ObjectInfo target, int totalTier, int launchSupportAdjustment = 0)
    {
        if (effectiveSource == null || target == null)
            return $"total={totalTier}";
        var sourcePenalty = GetSourceWellPenalty(effectiveSource);
        var relationPenalty = target.objectTypes == global::Data.EObjectTypes.Orbit
            ? GetOrbitTargetTier(effectiveSource, target)
            : GetSurfaceTargetTier(effectiveSource, target);
        var sourceType = effectiveSource.objectTypes.ToString();
        var targetType = target.objectTypes.ToString();
        var sourceBody = GetCanonicalBody(effectiveSource)?.ObjectName ?? "null";
        var targetBody = GetCanonicalBody(target)?.ObjectName ?? "null";
        return $"total={totalTier};sourcePenalty={sourcePenalty};relationPenalty={relationPenalty};launchSupportAdjustment={launchSupportAdjustment};sourceType={sourceType};targetType={targetType};sourceBody={sourceBody};targetBody={targetBody}";
    }

    private static int GetSurfaceTargetTier(ObjectInfo source, ObjectInfo target)
    {
        // Surface targets prefer their own orbit, then same-body/local-system sources, then
        // broader interplanetary sources with a distance penalty.
        if (IsOrbitOf(source, target))
            return 0;
        if (source == target)
            return 4;

        var sourceBody = GetCanonicalBody(source);
        var targetBody = GetCanonicalBody(target);
        if (sourceBody == null || targetBody == null)
            return 200;

        if (sourceBody == targetBody)
            return source.objectTypes == global::Data.EObjectTypes.Orbit ? 1 : 6;

        if (AreSiblingBodies(sourceBody, targetBody))
            return 14;

        if (IsDirectParentChildBody(sourceBody, targetBody))
            return 18;

        return 30 + GetSystemDistancePenalty(sourceBody, targetBody);
    }

    private static int GetOrbitTargetTier(ObjectInfo source, ObjectInfo target)
    {
        // Orbit targets prefer exact orbit, same-body surface/orbit, then sibling/parent
        // local sources before falling back to external bodies.
        if (source == target)
            return 0;
        if (target.parentObjectInfo != null && source == target.parentObjectInfo)
            return 5;

        var sourceBody = GetCanonicalBody(source);
        var targetBody = GetCanonicalBody(target);
        if (sourceBody == null || targetBody == null)
            return 200;

        if (sourceBody == targetBody)
            return source.objectTypes == global::Data.EObjectTypes.Orbit ? 1 : 5;

        if (AreSiblingBodies(sourceBody, targetBody))
            return 12;

        if (IsDirectParentChildBody(sourceBody, targetBody))
            return 14;

        return 25 + GetSystemDistancePenalty(sourceBody, targetBody);
    }

    private static ObjectInfo GetCanonicalBody(ObjectInfo oi)
    {
        if (oi == null) return null;
        return oi.objectTypes == global::Data.EObjectTypes.Orbit ? oi.parentObjectInfo : oi;
    }

    private static bool AreSiblingBodies(ObjectInfo a, ObjectInfo b)
    {
        return a != null && b != null
            && a != b
            && a.parentObjectInfo != null
            && a.parentObjectInfo == b.parentObjectInfo;
    }

    private static bool IsDirectParentChildBody(ObjectInfo a, ObjectInfo b)
    {
        return a != null && b != null
            && (a.parentObjectInfo == b || b.parentObjectInfo == a);
    }

    private static int GetSystemDistancePenalty(ObjectInfo a, ObjectInfo b)
    {
        return Mathf.RoundToInt(Mathf.Abs(a.DistanceToSunInAU - b.DistanceToSunInAU) * 100f);
    }

    private static int GetSourceWellPenalty(ObjectInfo source)
    {
        // Penalize deep gravity wells so orbit-sourced materials generally beat surface
        // launches unless local availability/vehicle constraints say otherwise.
        if (source == null)
            return 200;
        if (source.objectTypes == global::Data.EObjectTypes.Orbit
            || source.objectTypes == global::Data.EObjectTypes.SolarOrbit)
            return 0;

        var body = GetCanonicalBody(source);
        if (body == null)
            return 100;

        switch (body.objectTypes)
        {
            case global::Data.EObjectTypes.Asteroid:
            case global::Data.EObjectTypes.Comet:
                return 8;
            case global::Data.EObjectTypes.Moons:
                return 15;
            case global::Data.EObjectTypes.DwarfPlanet:
                return 30;
            case global::Data.EObjectTypes.Protoplanet:
                return 45;
            case global::Data.EObjectTypes.Planet:
                return 60;
            default:
                return 40;
        }
    }

    private static bool RequiresLaunchVehicleForSpacecraft(ObjectInfo from, Spacecraft sc, Company player, double cargoAmount)
    {
        // Use a payload-sensitive self-launch check. Stratos-like craft can leave small
        // bodies under their own thrust, but a full payload may still require an LV.
        var scType = sc?.spacecraftType ?? sc?.GetTypeSpaceCraft();
        if (from == null || scType == null || player == null)
            return false;
        if (from.objectTypes == global::Data.EObjectTypes.Orbit)
            return false;
        if (!from.NeedVehicleToLaunch())
            return false;

        if (CanSelfLaunchFromSurface(from, sc, player, cargoAmount, out var acceleration, out var gravity, out var payloadLimit))
        {
            LogVerbose($"SELF-LAUNCH allowed: body={from.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} cargo={cargoAmount:0.#} limit={payloadLimit:0.#} accel={acceleration:0.#####} surfaceG={gravity:0.#####}");
            return false;
        }

        LogVerbose($"SELF-LAUNCH blocked: body={from.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={scType.NameRocketType} cargo={cargoAmount:0.#} limit={payloadLimit:0.#} accel={acceleration:0.#####} surfaceG={gravity:0.#####} main={player.mainObjectInfo?.ObjectName} needMoonLV={scType.needLaunchVehicleToGoToMoon}");
        return true;
    }

    private static bool RequiresLaunchVehicleForSpacecraft(ObjectInfo from, SpacecraftType scType, Company player)
    {
        if (from == null || scType == null || player == null)
            return false;
        if (from.objectTypes == global::Data.EObjectTypes.Orbit)
            return false;
        if (!from.NeedVehicleToLaunch())
            return false;
        return from.Equals(player.mainObjectInfo) || scType.needLaunchVehicleToGoToMoon;
    }

    private static double GetSelfLaunchPayloadLimit(ObjectInfo from, Spacecraft sc, Company player)
    {
        if (from == null || sc == null || player == null)
            return 0;
        var scType = sc.spacecraftType ?? sc.GetTypeSpaceCraft();
        if (scType == null)
            return 0;
        if (from.objectTypes == global::Data.EObjectTypes.Orbit || !from.NeedVehicleToLaunch())
            return scType.GetCargoCapacity(player);
        if (scType.LowOrbitContainer)
            return 0;

        var gravity = from.GravitationalAcceleration;
        if (gravity <= 0)
            return scType.GetCargoCapacity(player);

        var payloadLimit = scType.GetThrust(player) / (gravity * 1000.0) - sc.GetMass() - scType.GetFuelCapacity(player);
        return Math.Max(0, Math.Min(scType.GetCargoCapacity(player), Math.Floor(payloadLimit)));
    }

    private static bool CanSelfLaunchFromSurface(ObjectInfo from, Spacecraft sc, Company player, double cargoAmount,
        out double acceleration, out double gravity, out double payloadLimit)
    {
        acceleration = 0;
        gravity = from?.GravitationalAcceleration ?? 0;
        payloadLimit = GetSelfLaunchPayloadLimit(from, sc, player);

        if (from == null || sc == null || player == null)
            return false;
        var scType = sc.spacecraftType ?? sc.GetTypeSpaceCraft();
        if (scType == null)
            return false;
        if (from.objectTypes == global::Data.EObjectTypes.Orbit || !from.NeedVehicleToLaunch())
            return true;
        if (scType.LowOrbitContainer)
            return false;

        var payload = Math.Max(0, cargoAmount);
        var mass = sc.GetMass() + payload + scType.GetFuelCapacity(player);
        if (mass <= 0)
            return false;
        acceleration = scType.GetThrust(player) / (mass * 1000.0);
        return acceleration > gravity;
    }

    public static bool TryOverrideLogisticsSelfLaunchCheck(PMMissionParameter pmp, out bool requiresFullLaunchVehicleList)
    {
        requiresFullLaunchVehicleList = false;
        if (!IsLogisticsPlan(pmp) || pmp?.SC is not Spacecraft sc || pmp.Start == null || pmp.FlyCompany == null)
            return false;

        var start = pmp.Start;
        var scType = sc.spacecraftType ?? sc.GetTypeSpaceCraft();
        if (scType == null)
            return false;
        if (scType.MagneticCatapult)
            return true;

        if (start.objectTypes != global::Data.EObjectTypes.Orbit
            && start.objectTypes != global::Data.EObjectTypes.Asteroid
            && start.objectTypes != global::Data.EObjectTypes.Comet
            && start.objectTypes != global::Data.EObjectTypes.SolarOrbit)
        {
            if (start.parentObjectInfo != null && pmp.Target != null && pmp.Start != pmp.StartHermesCase)
                return true;
            if (scType.LowOrbitContainer)
            {
                requiresFullLaunchVehicleList = true;
                return true;
            }

            var cargo = pmp.CargoAll?.CargoCurrent ?? 0;
            var canSelfLaunch = CanSelfLaunchFromSurface(start, sc, pmp.FlyCompany, cargo,
                out var acceleration, out var gravity, out var payloadLimit);
            requiresFullLaunchVehicleList = !canSelfLaunch;
            LogVerbose($"SELF-LAUNCH stock-override: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} cargo={cargo:0.#} limit={payloadLimit:0.#} accel={acceleration:0.#####} surfaceG={gravity:0.#####} requiresLV={requiresFullLaunchVehicleList}");
            return true;
        }

        requiresFullLaunchVehicleList = scType.LowOrbitContainer;
        return true;
    }

    private static bool TryCreateFuelBootstrapDelivery(Data.LogisticsRequest blockedReq, ObjectInfo requesterOI,
        ResourceDefinition blockedResource, ResourceDefinition fuelType, double fuelShortfall, Company player)
    {
        if (blockedReq == null || requesterOI == null || blockedResource == null || fuelType == null || player == null)
            return false;
        if (blockedResource == fuelType || fuelShortfall <= 0)
            return false;

        var current = GetFuelStock(requesterOI, player, fuelType);
        var inFlight = GetInFlightDeliveryAmount(requesterOI, fuelType, player);
        var fakeFuelReq = new Data.LogisticsRequest
        {
            ResourceDefinition = fuelType,
            resourceDef = fuelType,
            requestedAmount = current + inFlight + fuelShortfall,
            status = Data.LogisticsRequestStatus.Pending
        };

        if (VerboseLoggingEnabled)
            LogVerbose($"RETURNFUEL bootstrap-dispatch: blockedResource={blockedResource.ID} target={requesterOI.ObjectName} fuel={fuelType.ID} shortfall={fuelShortfall:0.#} current={current:0.#} inFlight={inFlight:0.#}");
        TryCreateDeliveries(fakeFuelReq, requesterOI, fuelType, fuelShortfall, player);
        blockedReq.status = Data.LogisticsRequestStatus.InProgress;
        blockedReq.statusNote = LogisticsStrings.WaitingForReturnFuel(fuelType, requesterOI);
        return true;
    }

    private static ETransferType GetTransferTypeForSpacecraft(ObjectInfo quotaLocation, Spacecraft sc, Data.LogisticsProvider providerRule = null)
    {
        if (quotaLocation == null || sc?.spacecraftType == null)
            return ETransferType.Optimal;

        var assignedProvider = providerRule != null && Data.LogisticsNetwork.IsSpacecraftAssignedToProvider(sc.ID, providerRule)
            ? providerRule
            : Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID);
        var assignedSetting = Data.LogisticsNetwork.GetProviderSpacecraftSetting(assignedProvider, sc);
        if (assignedSetting != null)
            return assignedSetting.useFastestTransfer ? ETransferType.Fastest : ETransferType.Optimal;

        var data = Data.LogisticsNetwork.Get(quotaLocation);
        var quota = data?.spacecraftQuota?
            .FirstOrDefault(q => Data.LogisticsNetwork.QuotaMatches(q, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"));
        return quota?.useFastestTransfer == true ? ETransferType.Fastest : ETransferType.Optimal;
    }

    private static double GetMinimumShipmentForSpacecraft(ObjectInfo quotaLocation, Spacecraft sc, Data.LogisticsProvider providerRule = null)
    {
        if (quotaLocation == null || sc?.spacecraftType == null)
            return 0;
        if (sc.spacecraftType.LowOrbitContainer)
            return 0;

        var assignedProvider = providerRule != null && Data.LogisticsNetwork.IsSpacecraftAssignedToProvider(sc.ID, providerRule)
            ? providerRule
            : Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID);
        var assignedSetting = Data.LogisticsNetwork.GetProviderSpacecraftSetting(assignedProvider, sc);
        if (assignedSetting != null)
            return Math.Max(0, assignedSetting.minimumShipmentAmount);

        var data = Data.LogisticsNetwork.Get(quotaLocation);
        var quota = data?.spacecraftQuota?
            .FirstOrDefault(q => Data.LogisticsNetwork.QuotaMatches(q, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"));
        return Math.Max(0, quota?.minimumShipmentAmount ?? 0);
    }

    private static bool MeetsMinimumShipment(ObjectInfo quotaLocation, Spacecraft sc, double amount, out string reason, Data.LogisticsProvider providerRule = null)
    {
        reason = null;
        var minimumShipment = GetMinimumShipmentForSpacecraft(quotaLocation, sc, providerRule);
        if (minimumShipment <= 0 || amount >= minimumShipment)
            return true;

        reason = $"Waiting for minimum {sc?.spacecraftType?.NameRocketType ?? "spacecraft"} shipment at {quotaLocation?.ObjectName ?? "unknown"}: {amount:0.#}/{minimumShipment:0.#}";
        return false;
    }

    private static bool SetupDirectCycleMission(Data.LogisticsRequest req, Spacecraft sc,
        ResourceDefinition rd, double amount, ObjectInfo requesterOI, ObjectInfo providerOI,
        out ResourceDefinition blockedFuelType, out double blockedFuelShortfall,
        LaunchVehicleType lvTypeA = null, ObjectInfo accountingTargetOI = null, ObjectInfo pendingTargetOI = null,
        Data.LogisticsProvider providerRule = null)
    {
        using (TimeScope($"SetupDirectCycleMission {providerOI?.ObjectName ?? "null"}->{requesterOI?.ObjectName ?? "null"} {rd?.ID ?? "null"}"))
        {
        blockedFuelType = null;
        blockedFuelShortfall = 0;
        var player = MonoBehaviourSingleton<GameManager>.Instance.Player;
        if (sc == null || player == null) return false;
        // Direct setup handles real spacecraft missions. If lvTypeA is null from a surface,
        // capacity has already been reduced to the self-launch payload limit.
        if (sc.GetCompany() != player)
        {
            LogWarning($"SKIP cycle: spacecraft company is not player for {sc.spacecraftType?.NameRocketType ?? "SC"}");
            return false;
        }
        if (!ValidateSpacecraftForCycleCreation(sc, player, "direct-create"))
            return false;

        var realProvider = sc.CurrentlyOnThisObject;
        if (realProvider == null) return false;

        amount = ClampToOutstandingRequest(req, accountingTargetOI ?? requesterOI, rd, player, amount);
        var capacity = sc.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (lvTypeA == null && realProvider.NeedVehicleToLaunch())
        {
            var selfLaunchLimit = GetSelfLaunchPayloadLimit(realProvider, sc, player);
            capacity = Math.Min(capacity, selfLaunchLimit);
            LogVerbose($"SELF-LAUNCH manifest-cap: route={realProvider.ObjectName}->{requesterOI?.ObjectName ?? "null"} ship={sc.GetSpacecraftName()} scType={sc.spacecraftType?.NameRocketType} payloadLimit={selfLaunchLimit:0.#} effectiveCapacity={capacity:0.#}");
        }
        amount = Math.Min(amount, capacity);
        if (amount <= 0) return false;
        if (!MeetsProviderMinimumShipment(realProvider, rd, amount, out var providerMinimumReason))
        {
            req.statusNote = providerMinimumReason;
            LogVerbose($"SKIP cycle: {providerMinimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID}");
            return false;
        }
        if (!MeetsMinimumShipment(realProvider, sc, amount, out var minimumReason, providerRule))
        {
            req.statusNote = minimumReason;
            LogVerbose($"SKIP cycle: {minimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID}");
            return false;
        }

        // Build the actual outbound manifest before creating the cycle. Return fuel may
        // displace requested cargo, so `normalCargo` becomes the authoritative shipment.
        var scList = new List<ISpacecraftInfo> { sc as ISpacecraftInfo };
        if (!BuildCargoManifestWithReturnFuel(req, rd, amount, requesterOI, realProvider, sc, player,
                capacity, lvTypeA, out var cargoToB, out var normalCargo, out var reserveFuelCargo,
                out blockedFuelType, out blockedFuelShortfall, out var waitingForFuelProbe, providerRule))
        {
            if (waitingForFuelProbe)
            {
                req.status = Data.LogisticsRequestStatus.InProgress;
                req.statusNote = "Calculating return fuel reserve";
            }
            LogWarning($"SKIP cycle: return fuel reserve could not be manifested for {realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} requested={amount:0.#}");
            return false;
        }
        amount = normalCargo;
        if (!MeetsProviderMinimumShipment(realProvider, rd, amount, out providerMinimumReason))
        {
            req.statusNote = providerMinimumReason;
            LogVerbose($"SKIP cycle: post-manifest {providerMinimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} manifest={FormatCargo(cargoToB)}");
            return false;
        }
        if (!MeetsMinimumShipment(realProvider, sc, amount, out minimumReason, providerRule))
        {
            req.statusNote = minimumReason;
            LogVerbose($"SKIP cycle: post-manifest {minimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} manifest={FormatCargo(cargoToB)}");
            return false;
        }
        if (!ValidateSdkDispatchBoundary("direct-delivery", player, realProvider, requesterOI, sc, cargoToB, allowSyntheticCarrier: false, out var validationFailure))
        {
            req.statusNote = validationFailure;
            return false;
        }
        var transferType = GetTransferTypeForSpacecraft(realProvider, sc, providerRule);
        // Moon-case routes (planet ↔ moon) use a slider instead of a porkchop plot.
        // If the CycleMissionsData carries Fastest, stock's TryPlanCycleMission callback
        // re-sets ClickFastestButton from it and PlanFlyCode runs the porkchop grid search
        // on a route with no valid grid, producing garbage fuel values (propellant inflated
        // to 10x tank capacity) and WrongLV failures. Force Optimal at the source.
        if (transferType == ETransferType.Fastest
            && IsMoonCaseRoute(realProvider, requesterOI))
        {
            transferType = ETransferType.Optimal;
            LogVerbose($"MOONCASE transfer-override: route={realProvider.ObjectName}->{requesterOI.ObjectName} forced=Optimal (moon-case has no porkchop)");
        }
        if (!TryAcquireRoutePlanningLock(realProvider, requesterOI, rd, player, out var routeLockKey))
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = $"Planning mission for {realProvider.ObjectName} -> {requesterOI.ObjectName}";
            return true;
        }

        var endsMaxA = SolarSdk.CyclicalMissions.CreateResourceCountFromCargo(
            cargoToB,
            amount > 0 ? rd : sc.spacecraftType.GetFuelType(),
            amount > 0 ? amount : reserveFuelCargo);
        LogVerbose($"RESOURCECOUNT build: route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} manifest={FormatCargo(cargoToB)} endsA={SolarSdk.CyclicalMissions.FormatResourceCount(endsMaxA)} endsB=empty reserveFuel={reserveFuelCargo:0.#}");

        var cycleResult = SolarSdk.CyclicalMissions.CreateAndAddCycle(new SdkCycleDraft
        {
            // ResourceCount completion prevents stock cycles from repeating forever. The
            // cycle is removed once the outbound manifest is satisfied.
            Source = realProvider, Target = requesterOI, Company = player,
            CargoStart = ECargoStart.FlyWithWhatIsAvailable, CargoEnd = ECargoStart.FlyWithWhatIsAvailable,
            CargoAllStart = cargoToB, CargoAllEnd = CargoAll.CreateCargoEmpty(),
            LaunchVehicleTypeA = lvTypeA, LaunchVehicleTypeB = null, TransferType = transferType,
            Ends = EEnds.ResourceCount,
            EndsResourceCountDataA = new EndsResourceCountData(),
            EndsResourceCountMaxA = endsMaxA,
            EndsResourceCountDataB = new EndsResourceCountData(),
            EndsResourceCountMaxB = new EndsResourceCountData(),
            EndsObjectThisManyTimes = 1,
            Spacecraft = scList,
            CustomName = BuildLogisticsMissionName(realProvider, requesterOI, rd)
        }, sc, SdkOwnerTag, SdkReservationOwner, "direct-delivery");
        if (!cycleResult.Success)
        {
            ReleaseRoutePlanningLock(routeLockKey, "direct-delivery-cycle-create-failed");
            req.statusNote = cycleResult.FailureReason;
            LogWarning($"SKIP cycle: SDK cycle create failed context=direct-delivery reason={cycleResult.FailureCode}:{cycleResult.FailureReason}");
            return false;
        }

        var cmd = cycleResult.Cycle;
        var dispatchId = cycleResult.DispatchId;
        _cycleCreatedAt[cmd] = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        MarkPendingPlanningDelivery(pendingTargetOI ?? requesterOI, rd);
        MarkShipForReturn(sc, realProvider, requesterOI, rd);
        RegisterLogisticsCycleName(cmd);

        CommitStock(realProvider, rd, amount);
        var isDirectToFinal = accountingTargetOI == null || accountingTargetOI == requesterOI;
        if (req.oneShot && amount > 0 && isDirectToFinal)
            req.dispatchedAmount += amount;

        var label = lvTypeA != null
            ? $"LV+Container: A={realProvider.ObjectName} B={requesterOI.ObjectName} lv={lvTypeA.Name}"
            : $"SC: A={realProvider.ObjectName} B={requesterOI.ObjectName} ship=1";
        if (VerboseLoggingEnabled)
        {
            LogVerbose($"LOGI-MANIFEST direct: route={realProvider.ObjectName}->{requesterOI.ObjectName} rd={rd.ID} ship={sc.GetSpacecraftName()} scType={sc.spacecraftType?.NameRocketType} capacity={capacity:0.#} targetCargo={amount:0.#} reserveFuel={reserveFuelCargo:0.#} totalPayload={cargoToB.CargoCurrent:0.#} transfer={transferType} manifest={FormatCargo(cargoToB)}");
            LogVerbose($"Cycle: id={dispatchId ?? "none"} {label} rd={rd.ID} transfer={transferType} targetAmount={amount} reserveFuel={reserveFuelCargo:0.#} manifest={FormatCargo(cargoToB)}");
        }

        req.status = Data.LogisticsRequestStatus.InProgress;

        HandOffCycleToStockPlanner(sc, cmd, "direct-delivery", routeLockKey);
        return true;

        }
    }

    private static bool SetupCycleMission(Data.LogisticsRequest req, Spacecraft container,
        ResourceDefinition rd, double amount, ObjectInfo requesterOI, ObjectInfo providerOI,
        LaunchVehicleType lvTypeA, out ResourceDefinition blockedFuelType, out double blockedFuelShortfall,
        ObjectInfo accountingTargetOI = null, ObjectInfo pendingTargetOI = null, bool clampToOutstanding = true,
        Data.LogisticsProvider providerRule = null)
    {
        using (TimeScope($"SetupCycleMission {providerOI?.ObjectName ?? "null"}->{requesterOI?.ObjectName ?? "null"} {rd?.ID ?? "null"}"))
        {
        blockedFuelType = null;
        blockedFuelShortfall = 0;
        var player = MonoBehaviourSingleton<GameManager>.Instance.Player;
        if (container == null || player == null) return false;
        // LV/container setup covers both true LV+spacecraft launches and stock low-orbit
        // payload container launches. LOC routes still use stock cycles, but one container
        // instance is created per mission.
        if (container.GetCompany() != player)
        {
            LogWarning($"SKIP LV cycle: spacecraft/container company is not player for {container.spacecraftType?.NameRocketType ?? "SC"}");
            return false;
        }
        if (!ValidateSpacecraftForCycleCreation(container, player, "lv-create"))
            return false;

        var realProvider = providerOI;
        if (realProvider == null) return false;

        if (clampToOutstanding)
            amount = ClampToOutstandingRequest(req, accountingTargetOI ?? requesterOI, rd, player, amount);
        var scCapacity = container.spacecraftType?.GetCargoCapacity(player) ?? 0;
        amount = Math.Min(amount, scCapacity);
        if (amount <= 0) return false;
        if (!MeetsProviderMinimumShipment(realProvider, rd, amount, out var providerMinimumReason))
        {
            req.statusNote = providerMinimumReason;
            LogVerbose($"SKIP LV cycle: {providerMinimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID}");
            return false;
        }
        if (!MeetsMinimumShipment(realProvider, container, amount, out var minimumReason, providerRule))
        {
            req.statusNote = minimumReason;
            LogVerbose($"SKIP LV cycle: {minimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID}");
            return false;
        }

        // Same cargo contract as direct missions: if return fuel is required, reserve it in
        // the manifest before stock sees the cyclical mission.
        var scList = new List<ISpacecraftInfo> { container as ISpacecraftInfo };
        if (!BuildCargoManifestWithReturnFuel(req, rd, amount, requesterOI, realProvider, container, player,
                scCapacity, lvTypeA, out var cargoToB, out var normalCargo, out var reserveFuelCargo,
                out blockedFuelType, out blockedFuelShortfall, out var waitingForFuelProbe, providerRule))
        {
            if (waitingForFuelProbe)
            {
                req.status = Data.LogisticsRequestStatus.InProgress;
                req.statusNote = "Calculating return fuel reserve";
            }
            LogWarning($"SKIP LV cycle: return fuel reserve could not be manifested for {realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} requested={amount:0.#}");
            return false;
        }
        amount = normalCargo;
        if (!MeetsProviderMinimumShipment(realProvider, rd, amount, out providerMinimumReason))
        {
            req.statusNote = providerMinimumReason;
            LogVerbose($"SKIP LV cycle: post-manifest {providerMinimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} manifest={FormatCargo(cargoToB)}");
            return false;
        }
        if (!MeetsMinimumShipment(realProvider, container, amount, out minimumReason, providerRule))
        {
            req.statusNote = minimumReason;
            LogVerbose($"SKIP LV cycle: post-manifest {minimumReason} route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} manifest={FormatCargo(cargoToB)}");
            return false;
        }
        if (!ValidateSdkDispatchBoundary("lv-delivery", player, realProvider, requesterOI, container, cargoToB, allowSyntheticCarrier: container.ID < 0, out var validationFailure))
        {
            req.statusNote = validationFailure;
            return false;
        }

        var isLOC = container.spacecraftType?.LowOrbitContainer == true;
        var transferType = isLOC
            ? ETransferType.Optimal
            : GetTransferTypeForSpacecraft(realProvider, container, providerRule);
        // Moon-case override: same as SetupDirectCycleMission — stock's
        // TryPlanCycleMission reads TransferType from the CycleMissionsData and
        // re-applies ClickFastestButton in its callback, bypassing our prefix fix.
        if (transferType == ETransferType.Fastest
            && IsMoonCaseRoute(realProvider, requesterOI))
        {
            transferType = ETransferType.Optimal;
            LogVerbose($"MOONCASE transfer-override: route={realProvider.ObjectName}->{requesterOI.ObjectName} forced=Optimal (moon-case has no porkchop)");
        }
        if (!TryAcquireRoutePlanningLock(realProvider, requesterOI, rd, player, out var routeLockKey))
        {
            req.status = Data.LogisticsRequestStatus.InProgress;
            req.statusNote = $"Planning mission for {realProvider.ObjectName} -> {requesterOI.ObjectName}";
            return true;
        }

        var endsMaxA = SolarSdk.CyclicalMissions.CreateResourceCountFromCargo(
            cargoToB,
            amount > 0 ? rd : container.spacecraftType.GetFuelType(),
            amount > 0 ? amount : reserveFuelCargo);
        LogVerbose($"RESOURCECOUNT build: route={realProvider?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} manifest={FormatCargo(cargoToB)} endsA={SolarSdk.CyclicalMissions.FormatResourceCount(endsMaxA)} endsB=empty reserveFuel={reserveFuelCargo:0.#}");

        var cycleResult = SolarSdk.CyclicalMissions.CreateAndAddCycle(new SdkCycleDraft
        {
            // Even LOC staging is one-shot via ResourceCount. This avoids infinite stock
            // cycling while still letting stock perform launch/arrival mechanics.
            Source = realProvider, Target = requesterOI, Company = player,
            CargoStart = ECargoStart.FlyWithWhatIsAvailable, CargoEnd = ECargoStart.FlyWithWhatIsAvailable,
            CargoAllStart = cargoToB, CargoAllEnd = CargoAll.CreateCargoEmpty(),
            LaunchVehicleTypeA = lvTypeA, LaunchVehicleTypeB = null, TransferType = transferType,
            Ends = EEnds.ResourceCount,
            EndsResourceCountDataA = new EndsResourceCountData(),
            EndsResourceCountMaxA = endsMaxA,
            EndsResourceCountDataB = new EndsResourceCountData(),
            EndsResourceCountMaxB = new EndsResourceCountData(),
            EndsObjectThisManyTimes = 1,
            Spacecraft = scList,
            CustomName = BuildLogisticsMissionName(realProvider, requesterOI, rd)
        }, container, SdkOwnerTag, SdkReservationOwner, "lv-delivery");
        if (!cycleResult.Success)
        {
            ReleaseRoutePlanningLock(routeLockKey, "lv-delivery-cycle-create-failed");
            req.statusNote = cycleResult.FailureReason;
            LogWarning($"SKIP LV cycle: SDK cycle create failed reason={cycleResult.FailureCode}:{cycleResult.FailureReason}");
            return false;
        }

        var cmd = cycleResult.Cycle;
        var dispatchId = cycleResult.DispatchId;
        _cycleCreatedAt[cmd] = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        MarkPendingPlanningDelivery(pendingTargetOI ?? requesterOI, rd);
        MarkShipForReturn(container, realProvider, requesterOI, rd);
        RegisterLogisticsCycleName(cmd);
        CommitStock(realProvider, rd, amount);
        var isDirectToFinal = accountingTargetOI == null || accountingTargetOI == requesterOI;
        if (req.oneShot && amount > 0 && isDirectToFinal)
            req.dispatchedAmount += amount;

        var label = $"LV+{(isLOC?"Container":"SC")} Cycle: A={realProvider.ObjectName} B={requesterOI.ObjectName} lv={lvTypeA.Name} transfer={transferType}";
        if (VerboseLoggingEnabled)
        {
            LogVerbose($"LOGI-MANIFEST lv: route={realProvider.ObjectName}->{requesterOI.ObjectName} rd={rd.ID} carrier={container.GetSpacecraftName()} scType={container.spacecraftType?.NameRocketType} capacity={scCapacity:0.#} targetCargo={amount:0.#} reserveFuel={reserveFuelCargo:0.#} totalPayload={cargoToB.CargoCurrent:0.#} lv={lvTypeA?.Name ?? "none"} transfer={transferType} manifest={FormatCargo(cargoToB)}");
            LogVerbose($"Cycle: id={dispatchId ?? "none"} {label} rd={rd.ID} targetAmount={amount} reserveFuel={reserveFuelCargo:0.#} manifest={FormatCargo(cargoToB)}");
        }

        req.status = Data.LogisticsRequestStatus.InProgress;

        HandOffCycleToStockPlanner(container, cmd, "lv-delivery", routeLockKey);
        return true;

        }
    }

    private static EndsResourceCountData MakeResourceCount(ResourceDefinition rd, double amount)
    {
        var data = new EndsResourceCountData();
        data.listData.Add(new EndsResourceCountDataPart { rd = rd, count = amount });
        return data;
    }

    private static EndsResourceCountData MakeResourceCount(CargoAll cargoAll, ResourceDefinition fallbackRd, double fallbackAmount)
    {
        var data = new EndsResourceCountData();
        if (cargoAll != null)
        {
            foreach (var cargo in GetResourceCargoItems(cargoAll))
            {
                if (cargo.resourceType == null || cargo.cargoMass <= 0) continue;
                var existing = data.listData.FirstOrDefault(part => part.rd == cargo.resourceType);
                if (existing != null)
                {
                    existing.count += cargo.cargoMass;
                }
                else
                {
                    data.listData.Add(new EndsResourceCountDataPart { rd = cargo.resourceType, count = cargo.cargoMass });
                }
            }
        }

        if (data.listData.Count == 0 && fallbackRd != null && fallbackAmount > 0)
            data.listData.Add(new EndsResourceCountDataPart { rd = fallbackRd, count = fallbackAmount });

        LogVerbose($"RESOURCECOUNT from-manifest: manifest={FormatCargo(cargoAll)} fallback={fallbackRd?.ID ?? "null"}:{fallbackAmount:0.#} result={FormatResourceCount(data)}");
        return data;
    }

    private static string FormatResourceCount(EndsResourceCountData data)
    {
        if (data?.listData == null || data.listData.Count == 0) return "empty";
        return string.Join(", ", data.listData
            .Where(part => part?.rd != null)
            .Select(part => $"{part.rd.ID}:{part.count:0.#}"));
    }

    private static double ClampToOutstandingRequest(Data.LogisticsRequest req, ObjectInfo requesterOI,
        ResourceDefinition rd, Company player, double amount)
    {
        if (req == null || requesterOI == null || rd == null || player == null)
            return amount;
        if (AllowsSensibleOvership(req))
            return amount;
        // One-shot tracks dispatched amount, not destination stock. The caller's
        // `remaining` (requestTarget - dispatchedAmount) already bounds the candidate
        // correctly; re-clamping against destination stock would produce micro-shipments
        // when the destination has nearly reached the target through consumption.
        if (req.oneShot)
            return amount;

        var current = requesterOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
        var inFlight = GetInFlightDeliveryAmount(requesterOI, rd, player);
        var outstanding = Math.Max(0, RequestTarget(req) - RequestTargetTolerance(req) - current - inFlight);
        return Math.Min(amount, outstanding);
    }
}
