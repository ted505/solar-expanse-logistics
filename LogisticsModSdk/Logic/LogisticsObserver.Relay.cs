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
            LogVerboseCoalesced($"relay-final-active|{finalTargetOI.id}|{rd.ID}", $"RELAY final-leg-active: target={finalTargetOI.ObjectName} rd={rd.ID}; checking whether additional staged cargo is still needed");

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

            LogVerboseCoalesced($"req-active-mission|{requester.id}|{rd.ID}|{mi.id}", $"REQ active-mission-present: target={requester.ObjectName} rd={rd.ID} mission={mi.id} name=\"{mi.missionName}\" launch={mi.DateLaunch:yyyy-MM-dd} amount={cargoAmount:0.#}");
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
}

