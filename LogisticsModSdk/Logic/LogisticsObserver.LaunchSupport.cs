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
        using (TimeScope($"FindAllIdleSpacecraft {location?.ObjectName ?? "null"}->{routeTarget?.ObjectName ?? "null"} nonContainer={requireNonContainer}", 0))
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
        using (TimeScope($"TryFindSurfaceLaunch {providerOI?.ObjectName ?? "null"}->{targetOI?.ObjectName ?? "null"} containerOnly={requireContainerOnly} regularSC={requireRegularSC}", 0))
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

        var allLaunchSupport = GetAvailableLaunchSupport(providerOI, player, snapshot)
            .Where(option => option?.Vehicle != null
                && option.Type != null
                && option.Vehicle.GetCompany() == player
                && option.Vehicle.objectInfo == providerOI)
            .ToList();
        if (allLaunchSupport.Count == 0)
        {
            reason = LogisticsStrings.NoLaunchVehiclesAt(providerOI);
            return false;
        }

        var lvQuotas = provData.launchVehicleQuota.Where(q => q.count > 0).ToList();
        if (lvQuotas.Count == 0)
        {
            reason = LogisticsStrings.NoLvQuotaAt(providerOI);
            return false;
        }

        // GetAvailableLaunchSupport folds stock LVs and fake/facility launch vehicles into
        // one comparable list so staging and direct surface launches use identical rules.
        var allReadyLV = allLaunchSupport
            .Where(option => option.Vehicle.IsReadyToLaunchReusable())
            .ToList();
        if (allReadyLV.Count == 0)
        {
            reason = allLaunchSupport.Any(IsLaunchSupportRecoveringForReuse)
                ? LogisticsStrings.AllLvsCoolingDownAt(providerOI)
                : LogisticsStrings.NoReadyLvAt(providerOI);
            return false;
        }

        var matchingLV = allLaunchSupport
            .Where(option => lvQuotas.Any(q => Data.LogisticsNetwork.QuotaMatches(q, option.Type.ID, option.Type.Name ?? "LV")))
            .ToList();
        var matchingReadyLV = allReadyLV
            .Where(option => lvQuotas.Any(q => Data.LogisticsNetwork.QuotaMatches(q, option.Type.ID, option.Type.Name ?? "LV")))
            .ToList();
        if (matchingReadyLV.Count == 0)
        {
            reason = matchingLV.Any(IsLaunchSupportRecoveringForReuse)
                ? LogisticsStrings.MatchingLvsCoolingDownAt(providerOI)
                : LogisticsStrings.NoMatchingLvQuotaAt(providerOI);
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
    }

    private static bool TryGetStagedRouteSupport(ObjectInfo providerOI, ObjectInfo sourceOrbit, ObjectInfo requester,
        Company player, Dictionary<string, int> scActive, Dictionary<string, int> lvActive,
        Data.LogisticsProvider providerRule, PlannerSnapshot snapshot, out StagedRouteSupport support)
    {
        using (TimeScope($"LV-STAGE support {providerOI?.ObjectName ?? "null"}->{sourceOrbit?.ObjectName ?? "null"}->{requester?.ObjectName ?? "null"}", 0))
        {
        support = null;
        if (providerOI == null || sourceOrbit == null || requester == null || player == null)
            return false;

        var key = BuildStagedRouteSupportCacheKey(providerOI, sourceOrbit, requester, player,
            scActive, lvActive, providerRule, snapshot);
        if (snapshot?.StagedRouteSupportByKey != null
            && key != null
            && snapshot.StagedRouteSupportByKey.TryGetValue(key, out var cached))
        {
            support = cached;
            LogVerboseCoalesced($"lv-stage-cache|{key}", $"LV-STAGE support-cache-hit: provider={providerOI.ObjectName} orbit={sourceOrbit.ObjectName} target={requester.ObjectName} success={cached.Success} reason={cached.Reason ?? "none"}");
            return cached.Success;
        }
        if (key != null && _stagedRouteSupportCache.TryGetValue(key, out var runtimeCached))
        {
            support = runtimeCached;
            if (snapshot?.StagedRouteSupportByKey != null)
                snapshot.StagedRouteSupportByKey[key] = runtimeCached;
            LogVerboseCoalesced($"lv-stage-runtime-cache|{key}", $"LV-STAGE support-runtime-cache-hit: provider={providerOI.ObjectName} orbit={sourceOrbit.ObjectName} target={requester.ObjectName} success={runtimeCached.Success} reason={runtimeCached.Reason ?? "none"}");
            return runtimeCached.Success;
        }

        support = new StagedRouteSupport();
        using (TimeScope($"LV-STAGE resolve {providerOI.ObjectName}->{sourceOrbit.ObjectName}->{requester.ObjectName}"))
        {
            if (!TryFindSurfaceLaunch(providerOI, sourceOrbit, player, scActive, lvActive,
                    requireContainerOnly: true, requireRegularSC: false,
                    out var stageLvType, out var stageCarrier, out var stageReason,
                    out var stageSupportDetail, out var stageSupportAdjustment, snapshot, providerRule))
            {
                support.Success = false;
                support.Reason = stageReason;
                StoreStagedRouteSupport(snapshot, key, support);
                return false;
            }

            var launchSupport = GetAvailableLaunchSupport(providerOI, player, snapshot);
            var matchingOption = launchSupport.FirstOrDefault(opt =>
                opt?.Type != null && SameLaunchVehicleType(opt.Type, stageLvType));
            var stageCapacity = GetSurfaceToOrbitPayloadCapacity(providerOI, player, stageCarrier, matchingOption, stageLvType);
            if (stageCapacity <= 0)
            {
                support.Success = false;
                support.Reason = LogisticsStrings.NoOrbitalPayloadCapacityFrom(providerOI);
                support.LaunchVehicleType = stageLvType;
                support.StageCarrier = stageCarrier;
                support.SupportDetail = stageSupportDetail;
                support.SupportTierAdjustment = stageSupportAdjustment;
                StoreStagedRouteSupport(snapshot, key, support);
                return false;
            }

            Spacecraft finalCarrier;
            string finalCarrierReason;
            using (TimeScope($"LV-STAGE final-carrier {sourceOrbit.ObjectName}->{requester.ObjectName}"))
            {
                finalCarrier = FindBestIdleSpacecraft(sourceOrbit, player, scActive, requireNonContainer: true,
                    out finalCarrierReason, snapshot, requester, providerRule);
            }

            var finalCapacity = finalCarrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
            if (finalCapacity <= 0)
            {
                support.Success = false;
                support.Reason = finalCarrierReason ?? LogisticsStrings.NoSpacecraftAvailableAt(sourceOrbit);
                support.LaunchVehicleType = stageLvType;
                support.StageCarrier = stageCarrier;
                support.StageCapacity = stageCapacity;
                support.SupportDetail = stageSupportDetail;
                support.SupportTierAdjustment = stageSupportAdjustment;
                StoreStagedRouteSupport(snapshot, key, support);
                return false;
            }

            support.Success = true;
            support.LaunchVehicleType = stageLvType;
            support.StageCarrier = stageCarrier;
            support.FinalCarrier = finalCarrier;
            support.StageCapacity = stageCapacity;
            support.FinalCapacity = finalCapacity;
            support.SupportDetail = stageSupportDetail;
            support.SupportTierAdjustment = stageSupportAdjustment;
            StoreStagedRouteSupport(snapshot, key, support);
            return true;
        }
        }
    }

    private static void StoreStagedRouteSupport(PlannerSnapshot snapshot, string key, StagedRouteSupport support)
    {
        if (string.IsNullOrWhiteSpace(key) || support == null)
            return;
        if (snapshot?.StagedRouteSupportByKey != null)
            snapshot.StagedRouteSupportByKey[key] = support;

        if (!_stagedRouteSupportCache.ContainsKey(key))
            _stagedRouteSupportCacheOrder.Enqueue(key);
        _stagedRouteSupportCache[key] = support;

        while (_stagedRouteSupportCacheOrder.Count > MaxStagedRouteSupportCacheEntries)
        {
            var evict = _stagedRouteSupportCacheOrder.Dequeue();
            _stagedRouteSupportCache.Remove(evict);
        }
    }

    private static void ClearStagedRouteSupportCache(string reason)
    {
        if (_stagedRouteSupportCache.Count == 0)
            return;
        var count = _stagedRouteSupportCache.Count;
        _stagedRouteSupportCache.Clear();
        _stagedRouteSupportCacheOrder.Clear();
        LogVerboseCoalesced($"lv-stage-cache-clear|{reason}", $"LV-STAGE support-cache-clear: reason={reason} entries={count}");
    }

    private static string BuildStagedRouteSupportCacheKey(ObjectInfo providerOI, ObjectInfo sourceOrbit, ObjectInfo requester,
        Company player, Dictionary<string, int> scActive, Dictionary<string, int> lvActive,
        Data.LogisticsProvider providerRule, PlannerSnapshot snapshot)
    {
        var routeKey = BuildStagedRouteSupportKey(providerOI, sourceOrbit, requester, providerRule);
        if (routeKey == null)
            return null;

        return $"{routeKey}|availability={BuildStagedRouteAvailabilitySignature(providerOI, sourceOrbit, requester, player, scActive, lvActive, snapshot)}";
    }

    private static string BuildStagedRouteAvailabilitySignature(ObjectInfo providerOI, ObjectInfo sourceOrbit, ObjectInfo requester,
        Company player, Dictionary<string, int> scActive, Dictionary<string, int> lvActive, PlannerSnapshot snapshot)
    {
        var providerData = Data.LogisticsNetwork.Get(providerOI);
        var orbitData = Data.LogisticsNetwork.Get(sourceOrbit);
        return string.Join(";",
            $"lvq={BuildQuotaSignature(providerData?.launchVehicleQuota)}",
            $"scq={BuildQuotaSignature(orbitData?.spacecraftQuota)}",
            $"activeSC={BuildCountSignature(scActive)}",
            $"activeLV={BuildCountSignature(lvActive)}",
            $"committed={BuildCommittedShipSignature(snapshot)}",
            $"launch={BuildLaunchSupportAvailabilitySignature(providerOI, player, snapshot)}",
            $"ships={BuildFinalCarrierAvailabilitySignature(sourceOrbit, requester, player, snapshot)}");
    }

    private static string BuildQuotaSignature(IEnumerable<Data.ShipQuotaEntry> quotas)
    {
        if (quotas == null)
            return "none";

        var parts = quotas
            .Where(q => q != null && q.count > 0)
            .OrderBy(q => q.typeName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(q => $"{q.typeName ?? "unknown"}:{q.count}");
        var signature = string.Join(",", parts);
        return string.IsNullOrEmpty(signature) ? "none" : signature;
    }

    private static string BuildCountSignature(Dictionary<string, int> counts)
    {
        if (counts == null || counts.Count == 0)
            return "none";

        return string.Join(",", counts
            .Where(pair => pair.Value != 0)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}:{pair.Value}"));
    }

    private static string BuildCommittedShipSignature(PlannerSnapshot snapshot)
    {
        if (snapshot?.CommittedShipIds == null || snapshot.CommittedShipIds.Count == 0)
            return "none";

        return string.Join(",", snapshot.CommittedShipIds.OrderBy(id => id));
    }

    private static string BuildLaunchSupportAvailabilitySignature(ObjectInfo providerOI, Company player, PlannerSnapshot snapshot)
    {
        var options = GetAvailableLaunchSupport(providerOI, player, snapshot);
        if (options == null || options.Count == 0)
            return "none";

        var parts = options
            .Where(option => option?.Vehicle != null
                && option.Type != null
                && option.Vehicle.GetCompany() == player
                && option.Vehicle.objectInfo == providerOI
                && option.Vehicle.IsReadyToLaunchReusable())
            .OrderBy(option => Data.LogisticsNetwork.TypeKey(option.Type.ID, option.Type.Name ?? "LV"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Vehicle.ID)
            .Select(option =>
            {
                var typeKey = Data.LogisticsNetwork.TypeKey(option.Type.ID, option.Type.Name ?? "LV");
                var active = CountActiveLaunchVehicleUsesAt(providerOI, option.Type, player, snapshot);
                return $"{typeKey}#{option.Vehicle.ID}:active={active}:tier={option.TierAdjustment}";
            });
        var signature = string.Join(",", parts);
        return string.IsNullOrEmpty(signature) ? "none" : signature;
    }

    private static string BuildFinalCarrierAvailabilitySignature(ObjectInfo sourceOrbit, ObjectInfo requester,
        Company player, PlannerSnapshot snapshot)
    {
        var ships = GetShipsAtLocation(sourceOrbit, player, snapshot)
            .Where(sc => sc?.spacecraftType != null && !sc.spacecraftType.LowOrbitContainer)
            .OrderBy(sc => sc.ID)
            .ToList();
        if (ships.Count == 0)
            return "none";

        var committed = snapshot?.CommittedShipIds;
        return string.Join(",", ships.Select(sc =>
        {
            var type = sc.spacecraftType;
            var typeKey = Data.LogisticsNetwork.TypeKey(type.ID, type.NameRocketType ?? "SC");
            var available = IsSpacecraftAvailableForLogistics(sc, player, committed);
            var inRange = IsSpacecraftInRangeForRouteNoLog(sc, requester, player);
            var capacity = type.GetCargoCapacity(player);
            return $"{sc.ID}:{typeKey}:phase={sc.CurrentPhase}:loc={sc.CurrentlyOnThisObject?.id ?? -1}:available={available}:range={inRange}:cap={capacity:0.#}";
        }));
    }

    private static bool IsSpacecraftInRangeForRouteNoLog(Spacecraft sc, ObjectInfo routeTarget, Company player)
    {
        var type = sc?.spacecraftType;
        if (type == null || player == null || routeTarget == null || !type.SolarSC)
            return true;

        var solarRange = type.GetSolarRange(player);
        var targetDistance = routeTarget.DistanceToSunInAU;
        return solarRange + 0.0001f >= targetDistance;
    }

    private static string BuildStagedRouteSupportKey(ObjectInfo providerOI, ObjectInfo sourceOrbit, ObjectInfo requester,
        Data.LogisticsProvider providerRule)
    {
        if (providerOI == null || sourceOrbit == null || requester == null)
            return null;

        var assigned = providerRule?.assignedSpacecraftIds == null
            ? string.Empty
            : string.Join(",", providerRule.assignedSpacecraftIds.OrderBy(id => id));
        var providerKey = providerRule == null
            ? "none"
            : $"net={providerRule.networkId};shared={providerRule.useSharedSpacecraftPool};assigned={assigned}";
        return $"{providerOI.id}->{sourceOrbit.id}->{requester.id}|{providerKey}";
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

    private static bool IsLaunchSupportRecoveringForReuse(LaunchSupportOption option)
    {
        var lv = option?.Vehicle;
        return lv?.launchVehicleType != null
            && lv.launchVehicleType.reusability > 0f
            && lv.launchTime.HasValue
            && !lv.IsReadyToLaunchReusable();
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
                || reason.IndexOf("No spacecraft logistics", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("No spacecraft present", StringComparison.OrdinalIgnoreCase) >= 0);
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
}

