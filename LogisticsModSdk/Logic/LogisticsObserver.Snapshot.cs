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
}

