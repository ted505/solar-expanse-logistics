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

public static partial class LogisticsObserver
{
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
        _protectedReturnReserveByCycle.Remove(cmd);
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
        var bootstrapStatus = TryCreateDeliveries(fakeFuelReq, requesterOI, fuelType, fuelShortfall, player);
        blockedReq.status = Data.LogisticsRequestStatus.InProgress;
        blockedReq.statusNote = string.IsNullOrWhiteSpace(bootstrapStatus)
            ? LogisticsStrings.WaitingForReturnFuel(fuelType, requesterOI)
            : $"{LogisticsStrings.WaitingForReturnFuel(fuelType, requesterOI)}; {bootstrapStatus}";

        if (!string.IsNullOrWhiteSpace(bootstrapStatus))
        {
            var reason = $"Return fuel bootstrap blocked for {fuelType.ID} at {requesterOI.ObjectName}: {bootstrapStatus}";
            MarkBlockedPlanningRetryCooldown(requesterOI, blockedResource, reason);
            LogVerbose($"RETURNFUEL bootstrap-blocked: blockedResource={blockedResource.ID} target={requesterOI.ObjectName} fuel={fuelType.ID} reason=\"{bootstrapStatus}\"");
        }
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
        RegisterProtectedReturnFuelReserve(cmd, cargoToB, reserveFuelCargo);
        _cycleCreatedAt[cmd] = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        MarkPendingPlanningDelivery(pendingTargetOI ?? requesterOI, rd);
        MarkShipForReturn(sc, realProvider, requesterOI, rd);
        RegisterLogisticsCycleName(cmd);

        CommitStock(realProvider, rd, amount);
        if (reserveFuelCargo > 0 && cargoToB?.cargoFuel?.resourceType != null)
            CommitStock(realProvider, cargoToB.cargoFuel.resourceType, reserveFuelCargo);
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
        RegisterProtectedReturnFuelReserve(cmd, cargoToB, reserveFuelCargo);
        _cycleCreatedAt[cmd] = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        MarkPendingPlanningDelivery(pendingTargetOI ?? requesterOI, rd);
        MarkShipForReturn(container, realProvider, requesterOI, rd);
        RegisterLogisticsCycleName(cmd);
        CommitStock(realProvider, rd, amount);
        if (reserveFuelCargo > 0 && cargoToB?.cargoFuel?.resourceType != null)
            CommitStock(realProvider, cargoToB.cargoFuel.resourceType, reserveFuelCargo);
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

