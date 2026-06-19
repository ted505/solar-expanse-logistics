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

        // In-flight cargo is logistics-owned after mission creation. Rebuild once after
        // load/runtime reset, then copy the ledger each daily tick instead of rescanning
        // every stock MissionInfo.
        using (TimeScope("SnapshotIndex.inFlightCargo"))
            CopyInFlightCargoLedgerToSnapshot(player, snapshot);

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

    private static void CopyInFlightCargoLedgerToSnapshot(Company player, PlannerSnapshot snapshot)
    {
        if (snapshot == null)
            return;

        if (_inFlightCargoLedgerNeedsRebuild)
            RebuildInFlightCargoLedger(player, snapshot.Missions);

        snapshot.InFlightCargoByTargetAndResource.Clear();
        foreach (var pair in _inFlightCargoLedger)
            snapshot.InFlightCargoByTargetAndResource[pair.Key] = pair.Value;
    }

    public static void RebuildInFlightCargoLedger(Company player = null, IEnumerable<MissionInfo> missions = null)
    {
        using (TimeScope("InFlightLedger.rebuild"))
        {
        _inFlightCargoLedger.Clear();
        _knownLogisticsMissionInfos.Clear();

        player ??= MonoBehaviourSingleton<GameManager>.Instance?.Player;
        missions ??= MonoBehaviourSingleton<MissionInfoManager>.Instance?.ListMissionInfo;
        if (player == null || missions == null)
        {
            _inFlightCargoLedgerNeedsRebuild = true;
            return;
        }

        foreach (var mi in missions)
        {
            if (!IsLogisticsMissionInfo(mi))
                continue;

            RegisterLogisticsMissionInfo(mi, updateLedger: false);
            if (mi == null || mi.complete || mi.cancel) continue;
            if (mi.company != player || mi.target == null || mi.cargoAll == null) continue;

            AddMissionCargoToInFlightLedger(mi);
        }

        _inFlightCargoLedgerNeedsRebuild = false;
        LogVerbose($"INFLIGHT ledger-rebuild: entries={_inFlightCargoLedger.Count} missions={_knownLogisticsMissionInfos.Count}");
        }
    }
}

