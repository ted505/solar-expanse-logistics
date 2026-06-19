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
    private const string SdkOwnerTag = "logi";

    private const string SdkReservationOwner = "logisticsmodsdk";

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
            _logWriter = new StreamWriter(path, false) { AutoFlush = false };
            _logWriter.WriteLine($"=== {DateTime.Now} session={_logSession} ===");
            _lastLogFlushUtc = DateTime.UtcNow;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {level}{msg}";
        _logWriter.WriteLine(line);
        FlushLogIfNeeded(level == "[WARN] " || level == "[ERROR] ");
    }

    private static bool WriteLogCoalesced(string level, string key, string msg, bool forceFlush)
    {
        if (string.IsNullOrWhiteSpace(key) || VerboseLogCoalesceSeconds <= 0)
        {
            WriteLog(level, msg);
            return true;
        }

        var now = DateTime.UtcNow;
        var coalesceKey = $"{level}|{key}";
        if (_coalescedLogState.TryGetValue(coalesceKey, out var state)
            && (now - state.LastWriteUtc).TotalSeconds < VerboseLogCoalesceSeconds)
        {
            state.Suppressed++;
            return false;
        }

        var line = msg;
        if (state != null && state.Suppressed > 0)
        {
            line = $"{msg} [suppressed {state.Suppressed} similar lines]";
            state.Suppressed = 0;
        }

        if (state == null)
            _coalescedLogState[coalesceKey] = state = new CoalescedLogState();
        state.LastWriteUtc = now;
        WriteLog(level, line);
        if (forceFlush)
            FlushLogIfNeeded(true);
        return true;
    }

    private static void FlushLogIfNeeded(bool force)
    {
        if (_logWriter == null)
            return;

        _pendingLogLineCount++;
        var now = DateTime.UtcNow;
        var interval = LogFlushIntervalSeconds;
        if (!force
            && interval > 0
            && _pendingLogLineCount < 128
            && (now - _lastLogFlushUtc).TotalSeconds < interval)
            return;

        _logWriter.Flush();
        _pendingLogLineCount = 0;
        _lastLogFlushUtc = now;
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
        var newDispatchesThisTick = 0;

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
                LogVerboseCoalesced($"req-eval|{requesterOI?.id}|{rd.ID}", $"REQ eval: target={requesterOI?.ObjectName} rd={rd.ID} fillTarget={requestTarget:0.#} minimum={requestMinimum:0.#} stock={alreadyThere:0.#} dispatched={req.dispatchedAmount:0.#} status={req.status}");
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
                    LogVerboseCoalesced($"req-active-cycle|{requesterOI?.id}|{rd.ID}", $"REQ active-cycle-present: target={requesterOI?.ObjectName} rd={rd.ID}; checking whether additional cargo is still needed");
                }

                if (!hasActiveDelivery && !string.IsNullOrEmpty(blockedReturnNote))
                {
                    req.status = Data.LogisticsRequestStatus.InProgress;
                    req.statusNote = blockedReturnNote;
                    LogVerboseCoalesced($"req-return-blocked|{requesterOI?.id}|{rd.ID}", $"REQ return-blocked-note-present: target={requesterOI?.ObjectName} rd={rd.ID} note={blockedReturnNote}; continuing outbound planning");
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

                if (ShouldPaceNewDispatch(newDispatchesThisTick, out var pacingStatus))
                {
                    req.status = hasActiveDelivery || !string.IsNullOrEmpty(blockedReturnNote)
                        ? Data.LogisticsRequestStatus.InProgress
                        : Data.LogisticsRequestStatus.Pending;
                    req.statusNote = !string.IsNullOrEmpty(blockedReturnNote)
                        ? $"{blockedReturnNote}; {pacingStatus}"
                        : pacingStatus;
                    LogVerboseCoalesced($"dispatch-pacing|{requesterOI?.id}|{rd.ID}", $"DISPATCH pacing-skip: target={requesterOI?.ObjectName} rd={rd.ID} count={newDispatchesThisTick} max={MaxNewDispatchesPerDay} nextWallClock={_nextNewDispatchWallClockUtc:O}");
                    continue;
                }

                req.status = hasActiveDelivery
                    ? Data.LogisticsRequestStatus.InProgress
                    : Data.LogisticsRequestStatus.Pending;
                var pendingReason = TryCreateDeliveries(req, requesterOI, rd, remaining, player, out var createdDispatch, snapshot);
                if (string.IsNullOrEmpty(pendingReason))
                {
                    ClearRequestPlanningThrottle(requesterOI, rd);
                    if (createdDispatch)
                    {
                        newDispatchesThisTick++;
                        MarkNewDispatchCreated();
                    }
                }
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

    private static bool ShouldPaceNewDispatch(int newDispatchesThisTick, out string statusNote)
    {
        statusNote = null;
        if (newDispatchesThisTick >= MaxNewDispatchesPerDay)
        {
            statusNote = "Waiting for logistics dispatch pacing";
            return true;
        }

        var nowUtc = DateTime.UtcNow;
        if (_nextNewDispatchWallClockUtc != default && nowUtc < _nextNewDispatchWallClockUtc)
        {
            var ms = Math.Max(0, (_nextNewDispatchWallClockUtc - nowUtc).TotalMilliseconds);
            statusNote = $"Waiting for logistics dispatch pacing ({ms:0}ms)";
            return true;
        }

        return false;
    }

    private static void MarkNewDispatchCreated()
    {
        var cooldownMs = DispatchCreationCooldownMs;
        if (cooldownMs <= 0)
        {
            _nextNewDispatchWallClockUtc = default;
            return;
        }

        _nextNewDispatchWallClockUtc = DateTime.UtcNow.AddMilliseconds(cooldownMs);
    }

    private static bool IsTransientPlanningStatus(string statusNote)
    {
        return !string.IsNullOrEmpty(statusNote)
            && (statusNote.StartsWith("Planning mission", StringComparison.Ordinal)
                || statusNote.StartsWith("Waiting for logistics dispatch pacing", StringComparison.Ordinal)
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
            LogVerboseCoalesced($"dispatch-cooldown|{key}", $"DISPATCH cooldown: target={requester?.ObjectName} rd={rd?.ID} retryAfter={state.RetryAfter:yyyy-MM-dd} reason={state.Reason}");
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
}

