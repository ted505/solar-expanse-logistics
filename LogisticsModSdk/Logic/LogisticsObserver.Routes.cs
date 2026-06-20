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
        return TryCreateDeliveries(req, requester, rd, remaining, player, out _, snapshot);
    }

    private static string TryCreateDeliveries(Data.LogisticsRequest req, ObjectInfo requester,
        ResourceDefinition rd, double remaining, Company player, out bool createdDispatch, PlannerSnapshot snapshot = null)
    {
        createdDispatch = false;
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
            if (ExecuteRouteCandidate(candidate, req, requester, rd, player, out var candidateCreatedDispatch, snapshot))
            {
                createdDispatch = candidateCreatedDispatch;
                CloseReorderLatchIfTargetCovered(req, requester, rd, player, snapshot);
                return null;
            }
        }
        if (IsMinimumShipmentStatus(req?.statusNote))
            return req.statusNote;
        var executeReason = IsBlockingStatusNote(req?.statusNote)
            ? req.statusNote
            : bestBlocker.Reason ?? "all candidates failed during execution";
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
        List<ObjectInfo> providerObjects;
        using (TimeScope($"RouteCandidates.providers {requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
            providerObjects = GetProviderObjectsForResource(rd, snapshot).ToList();
            foreach (var providerOI in providerObjects)
            {
                if (providerOI == requester) continue;

                Data.LogisticsObjectData provData;
                using (TimeScope($"RouteCandidates.provider-data {providerOI?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
                {
                    provData = Data.LogisticsNetwork.Get(providerOI);
                    if (provData == null && TryGetExportedOrbitProviderParent(providerOI, rd, out var exportedParent))
                        provData = Data.LogisticsNetwork.Get(exportedParent);
                }
                if (provData == null) continue;
                List<Data.LogisticsProvider> matchingProviders;
                using (TimeScope($"RouteCandidates.matching-rules {providerOI?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
                    matchingProviders = GetMatchingProviderRules(provData, rd, req.networkId, requester, providerOI).ToList();
                if (matchingProviders.Count == 0)
                    continue;

            double available;
            using (TimeScope($"RouteCandidates.provider-surplus {providerOI?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
                available = GetProviderAvailableAfterMinimum(providerOI, rd, player);
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
                using (TimeScope($"RouteCandidates.direct {providerOI?.ObjectName ?? "null"}->{requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
                    AddDirectRouteCandidates(result, req, providerRule, providerOI, requester, rd, remaining, available, player, scActive, lvActive, bestBlocker, snapshot);
                using (TimeScope($"RouteCandidates.staged {providerOI?.ObjectName ?? "null"}->{requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
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

        if (!TryGetStagedRouteSupport(providerOI, sourceOrbit, requester, player, scActive, lvActive,
                providerRule, snapshot, out var stagedSupport))
        {
            var stageReason = stagedSupport?.Reason ?? LogisticsStrings.NoSurfaceLaunchPathFrom(providerOI);
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={stageReason}");
            var missingOptionalStagingSpacecraft = IsMissingOptionalStagingSpacecraftReason(stageReason);
            TrackPlannerBlocker(bestBlocker,
                missingOptionalStagingSpacecraft ? routeTier + 100 : routeTier,
                missingOptionalStagingSpacecraft ? 9 : 2,
                stageReason);
            return;
        }

        routeTier += stagedSupport.SupportTierAdjustment;
        routeDetail = VerboseLoggingEnabled ? DescribeRouteScore(sourceOrbit, requester, routeTier, stagedSupport.SupportTierAdjustment) : null;
        if (VerboseLoggingEnabled)
            routeDetail += DescribePriorityScore(providerOI, rd);

        var stageCarrier = stagedSupport.StageCarrier;
        var finalCarrier = stagedSupport.FinalCarrier;
        var stageLvType = stagedSupport.LaunchVehicleType;
        var stageCapacity = stagedSupport.StageCapacity;
        var finalCapacity = stagedSupport.FinalCapacity;
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
            var finalReason = stagedSupport.Reason ?? LogisticsStrings.NoSpacecraftAvailableAt(sourceOrbit);
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={finalReason}");
            var missingOptionalStagingSpacecraft = IsMissingOptionalStagingSpacecraftReason(finalReason);
            TrackPlannerBlocker(bestBlocker, missingOptionalStagingSpacecraft ? routeTier + 100 : routeTier,
                missingOptionalStagingSpacecraft ? 9 : 3, finalReason);
            return;
        }

        double amount;
        using (TimeScope($"RouteCandidates.staged-amount {providerOI?.ObjectName ?? "null"}->{requester?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
            amount = GetCandidateAmount(req, providerOI, rd, remaining, available,
                Math.Min(stageCapacity, finalCapacity), finalCarrier, sourceOrbit, providerRule);
        if (amount <= 0) return;
        bool meetsProviderMinimum;
        string providerMinimumReason;
        using (TimeScope($"RouteCandidates.staged-provider-min {providerOI?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
            meetsProviderMinimum = MeetsProviderMinimumShipment(providerOI, rd, amount, out providerMinimumReason);
        if (!meetsProviderMinimum)
        {
            if (VerboseLoggingEnabled)
                LogBepInEx($"ROUTE candidate-blocked: rd={rd.ID} kind={RouteKind.StageSourceSurfaceToOrbit} label={providerOI.ObjectName} -> {sourceOrbit.ObjectName} -> {requester.ObjectName} score={routeTier} detail={routeDetail} reason={providerMinimumReason}");
            TrackPlannerBlocker(bestBlocker, routeTier, 7, providerMinimumReason);
            return;
        }
        bool meetsMinimum;
        string minimumReason;
        using (TimeScope($"RouteCandidates.staged-ship-min {sourceOrbit?.ObjectName ?? "null"} {rd?.ID ?? "null"}", 0))
            meetsMinimum = MeetsMinimumShipment(sourceOrbit, finalCarrier, amount, out minimumReason, providerRule);
        if (!meetsMinimum)
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
            ScoreBreakdown = string.IsNullOrWhiteSpace(stagedSupport.SupportDetail) ? routeDetail : $"{routeDetail};launchSupport={stagedSupport.SupportDetail};stageCapacity={stageCapacity:0.#};finalCapacity={finalCapacity:0.#}"
        });
    }

    private static bool ExecuteRouteCandidate(RouteCandidate candidate, Data.LogisticsRequest req,
        ObjectInfo requester, ResourceDefinition rd, Company player, out bool createdDispatch, PlannerSnapshot snapshot = null)
    {
        createdDispatch = false;
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
                    createdDispatch = true;
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
                    createdDispatch = true;
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
                    SetProgressStatusNote(req, LogisticsStrings.StagingTo(candidate.StageOrbit));
                    if (VerboseLoggingEnabled)
                    {
                        LogVerbose($"PROC ranked: {rd.ID} x{candidate.Amount:0.#} {candidate.Label} kind={candidate.Kind}");
                        LogBepInEx($"ROUTE chosen: rd={rd.ID} kind={candidate.Kind} label={candidate.Label} score={candidate.Tier} detail={candidate.ScoreBreakdown}");
                    }
                    createdDispatch = true;
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
            SetProgressStatusNote(req, lockStatus);
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
            SetProgressStatusNote(req, minimumReason);
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
        SetProgressStatusNote(req, LogisticsStrings.ShippingFrom(sourceOrbit));
        if (VerboseLoggingEnabled)
            LogVerbose($"RELAY final-leg-dispatch: rd={rd.ID} sourceOrbit={sourceOrbit.ObjectName} target={requester.ObjectName} amount={amount:0.#}");
        MarkNewDispatchCreated();
        return true;
    }
}

