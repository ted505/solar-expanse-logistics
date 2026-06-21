using System;
using System.Linq;
using CustomUpdate;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Manager;
using SolarExpanseSdk.Core;
using ScriptableObjectScripts;

namespace LogisticsModSdk.Logic;

public static partial class LogisticsObserver
{
    public static void OnProviderRemoved(ObjectInfo providerOI, Data.LogisticsProvider provider)
    {
        if (providerOI == null || provider == null)
            return;

        var rd = provider.ResourceDefinition;
        ClearProviderPlanningState(providerOI, rd, "provider-removed");

        var assignedIds = provider.assignedSpacecraftIds?
            .Where(id => id >= 0)
            .Distinct()
            .ToList();
        if (assignedIds == null || assignedIds.Count == 0)
            return;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
        var clearedReturns = 0;
        var releasedReservations = 0;
        var preservedActive = 0;

        foreach (var shipId in assignedIds)
        {
            var sc = ships?.FirstOrDefault(ship => ship != null && ship.ID == shipId);
            if (_returnHomeByShipId.TryGetValue(shipId, out var state)
                && state != null
                && state.Home == providerOI
                && (rd == null || state.Resource == rd))
            {
                if (!state.HasLeftHome)
                {
                    ResetReturnPlanState(state);
                    ResetReturnFailureState(state);
                    _returnHomeByShipId.Remove(shipId);
                    clearedReturns++;
                }
                else
                {
                    preservedActive++;
                }
            }

            var hasActiveWork = sc != null
                && IsSpacecraftAlreadyCommitted(sc, player, out _, includeReturnReservation: false);
            if (!hasActiveWork && SolarSdk.Fleet.IsReserved(shipId))
            {
                if (SolarSdk.Fleet.ReleaseSpacecraft(shipId, SdkReservationOwner))
                    releasedReservations++;
            }
            else if (hasActiveWork)
            {
                preservedActive++;
            }
        }

        if (VerboseLoggingEnabled)
        {
            LogVerbose($"PROVIDER removed-cleanup: provider={providerOI.ObjectName} rd={rd?.ID ?? "null"} assigned={assignedIds.Count} clearedReturns={clearedReturns} releasedReservations={releasedReservations} preservedActive={preservedActive}");
        }
    }

    private static void ClearProviderPlanningState(ObjectInfo providerOI, ResourceDefinition rd, string reason)
    {
        if (providerOI == null || rd == null)
            return;

        var resourceSuffix = ":" + rd.ID;
        var routeSourceMarker = ":" + providerOI.id + "->";

        var blockedCount = RemoveKeysEndingWith(_blockedPlanningRetries, resourceSuffix);
        var throttleCount = RemoveKeysEndingWith(_requestPlanThrottle, resourceSuffix);
        var pendingCount = RemoveKeysEndingWith(_pendingPlanningDeliveries, resourceSuffix);
        var routeLockCount = RemoveKeysMatching(_routePlanningLocks, key =>
            key != null
            && key.IndexOf(routeSourceMarker, StringComparison.Ordinal) >= 0
            && key.EndsWith(resourceSuffix, StringComparison.Ordinal));
        var committedRemoved = _committedStock.Remove(CommittedStockKey(providerOI, rd));

        ClearStagedRouteSupportCache(reason);

        if (VerboseLoggingEnabled)
        {
            LogVerbose($"PROVIDER planning-cleanup: provider={providerOI.ObjectName} rd={rd.ID} blocked={blockedCount} throttles={throttleCount} pending={pendingCount} routeLocks={routeLockCount} committedRemoved={committedRemoved}");
        }
    }

    private static int RemoveKeysEndingWith<TValue>(System.Collections.Generic.Dictionary<string, TValue> dictionary, string suffix)
    {
        if (dictionary == null || string.IsNullOrEmpty(suffix))
            return 0;

        var keys = dictionary.Keys
            .Where(key => key != null && key.EndsWith(suffix, StringComparison.Ordinal))
            .ToList();
        foreach (var key in keys)
            dictionary.Remove(key);
        return keys.Count;
    }

    private static int RemoveKeysMatching<TValue>(System.Collections.Generic.Dictionary<string, TValue> dictionary, Func<string, bool> predicate)
    {
        if (dictionary == null || predicate == null)
            return 0;

        var keys = dictionary.Keys
            .Where(predicate)
            .ToList();
        foreach (var key in keys)
            dictionary.Remove(key);
        return keys.Count;
    }
}
