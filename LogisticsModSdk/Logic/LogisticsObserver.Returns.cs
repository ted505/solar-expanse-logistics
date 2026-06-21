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
                LogVerboseCoalesced($"returnhome-cooldown|{sc.ID}|{current.id}|{home.id}", $"RETURNHOME cooldown: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} note={returnCooldownNote}");
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
            LogVerboseCoalesced($"returnhome-skip-create-cooldown|{sc.ID}|{current.id}|{home.id}", $"RETURNHOME skip-create-cooldown: ship={sc.GetSpacecraftName()} id={sc.ID} current={current.ObjectName} home={home.ObjectName} note={returnCooldownNote}");
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
            backhaulAmountLimit = Math.Max(0, Math.Floor(rawCapacity * FastestBackhaulCargoFraction));
            LogVerbose($"RETURNHOME fast-backhaul-cap: ship={sc.GetSpacecraftName()} id={sc.ID} rawCapacity={rawCapacity:0.#} cap={backhaulAmountLimit:0.#} current={current.ObjectName} home={home.ObjectName}");
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
        var capacity = Math.Max(0, rawCapacity);
        if (capacity <= 0)
        {
            LogVerbose($"BACKHAUL skip-capacity: ship={sc.GetSpacecraftName()} rawCapacity={rawCapacity:0.#} current={current.ObjectName} home={home.ObjectName}");
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

        LogVerbose($"BACKHAUL matched: ship={sc.GetSpacecraftName()} rd={backhaulRd.ID} amount={backhaulAmount:0.#} surplus={best.surplus:0.#} need={best.need:0.#} capacity={capacity:0.#} limit={(double.IsPositiveInfinity(amountLimit) ? "none" : amountLimit.ToString("0.#"))} rawCapacity={rawCapacity:0.#} priority={best.req.priority} current={current.ObjectName} home={home.ObjectName} target={best.target?.ObjectName ?? "null"}");
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
        _inFlightCargoLedger.TryGetValue(key, out var ledgerExisting);
        _inFlightCargoLedger[key] = ledgerExisting + amount;
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
}

