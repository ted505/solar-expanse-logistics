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

// Central planner/runtime coordinator for logistics. Split across partial files by
// responsibility because stock cyclical missions are async and can complete, detach,
// or be rebuilt by stock controllers outside the original call stack.
public static partial class LogisticsObserver
{
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
}

