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
    private static void ProcessAutoSellProviders(Company player, PlannerSnapshot snapshot)
    {
        if (player == null) return;
        // Auto-Sell runs before exports so logistics does not ship cargo that the provider
        // rule already sold into local market demand.
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
        var monthKey = $"{now.Year:D4}-{now.Month:D2}";
        foreach (var oi in snapshot?.Objects ?? Data.LogisticsNetwork.GetAllObjects())
        {
            var data = Data.LogisticsNetwork.Get(oi);
            var oid = oi?.GetObjectInfoData(player);
            if (data?.providers == null || oid == null) continue;

            foreach (var provider in data.providers)
            {
                var rd = provider.ResourceDefinition;
                if (!provider.isActive || !provider.autoSell || !Data.LogisticsResourceFilter.IsSupported(rd))
                    continue;

                if (!string.Equals(provider.autoSellMonthKey, monthKey, StringComparison.Ordinal))
                {
                    provider.autoSellMonthKey = monthKey;
                    provider.autoSellSoldThisMonth = 0;
                }

                var stock = oid.CheckResources(rd);
                var committed = GetCommittedStock(oi, rd);
                var surplus = Math.Max(0, stock - provider.minimumKeep - committed);
                if (provider.autoSellMode == Data.AutoSellMode.PerMonth)
                {
                    var remainingMonthly = Math.Max(0, provider.autoSellMaxPerMonth - provider.autoSellSoldThisMonth);
                    surplus = Math.Min(surplus, remainingMonthly);
                }
                if (surplus <= 0)
                    continue;

                var sold = FulfillMarketOffers(player, oi, rd, surplus, buySell: true,
                    minPrice: provider.autoSellMinPrice, maxPrice: double.MaxValue,
                    buyCheapestFirst: false, snapshot: snapshot);
                if (sold > 0)
                {
                    provider.autoSellSoldThisMonth += sold;
                    LogVerbose($"AUTO-SELL fulfilled: body={oi?.ObjectName} rd={rd.ID} sold={sold:0.#} minPrice={provider.autoSellMinPrice:0.##} mode={provider.autoSellMode}");
                }
            }
        }
    }

    private static void ProcessExportToOrbit(Company player, PlannerSnapshot snapshot)
    {
        if (player == null) return;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null) return;
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;

        foreach (var oi in snapshot?.Objects ?? Data.LogisticsNetwork.GetAllObjects())
        {
            if (oi == null || !oi.NeedVehicleToLaunch()) continue;
            if (oi.objectTypes == global::Data.EObjectTypes.Orbit) continue;

            var data = Data.LogisticsNetwork.Get(oi);
            if (data?.providers == null) continue;

            var orbitOI = oi.LowOrbitCustom?.GetObjectInfo();
            if (orbitOI == null) continue;

            foreach (var provider in data.providers)
            {
                if (!provider.isActive || !provider.exportToOrbit) continue;
                var rd = provider.ResourceDefinition;
                if (!Data.LogisticsResourceFilter.IsSupported(rd)) continue;

                var exportRequest = new Data.LogisticsRequest
                {
                    ResourceDefinition = rd,
                    requestedAmount = double.MaxValue,
                    status = Data.LogisticsRequestStatus.InProgress
                };

                var surplus = GetProviderAvailableAfterMinimum(oi, rd, player);
                if (surplus <= 0) continue;

                if (provider.exportOrbitMaxStock > 0)
                {
                    var orbitStock = orbitOI.GetObjectInfoData(player)?.CheckResources(rd) ?? 0;
                    var orbitInbound = GetInFlightDeliveryAmount(orbitOI, rd, player, snapshot);
                    if (orbitStock + orbitInbound >= provider.exportOrbitMaxStock)
                    {
                        LogVerbose($"EXPORT-ORBIT cap-reached: body={oi.ObjectName} orbit={orbitOI.ObjectName} rd={rd.ID} stock={orbitStock:0.#} inFlight={orbitInbound:0.#} cap={provider.exportOrbitMaxStock:0.#}");
                        continue;
                    }
                }

                if (HasRoutePlanningLock(oi, orbitOI, rd, player, out _))
                    continue;

                var scActive = snapshot?.ScActive ?? new Dictionary<string, int>();
                var lvActive = snapshot?.LvActive ?? new Dictionary<string, int>();
                if (!TryFindSurfaceLaunch(oi, orbitOI, player, scActive, lvActive,
                    requireContainerOnly: true, requireRegularSC: false,
                    out var lvType, out var carrier, out var reason,
                    out var supportDetail, out var tierAdjust, snapshot))
                {
                    LogVerbose($"EXPORT-ORBIT skip: body={oi.ObjectName} rd={rd.ID} surplus={surplus:0.#} reason={reason}");
                    continue;
                }

                var launchSupport = GetAvailableLaunchSupport(oi, player, snapshot);
                var matchingOption = launchSupport.FirstOrDefault(opt =>
                    opt?.Type != null && SameLaunchVehicleType(opt.Type, lvType));
                var carrierCapacity = GetSurfaceToOrbitPayloadCapacity(oi, player, carrier, matchingOption, lvType);
                if (carrierCapacity <= 0) continue;

                var providerMinimumShipment = GetProviderMinimumShipment(oi, rd);
                var minFillAmount = providerMinimumShipment > 0
                    ? providerMinimumShipment
                    : 0;
                if (surplus < minFillAmount)
                {
                    LogVerbose($"EXPORT-ORBIT wait-fill: body={oi.ObjectName} rd={rd.ID} surplus={surplus:0.#} minFill={minFillAmount:0.#} capacity={carrierCapacity:0.#} category={matchingOption?.Category ?? "standard"} tierAdj={tierAdjust}");
                    continue;
                }

                var amount = Math.Min(surplus, carrierCapacity);
                if (carrier?.spacecraftType?.LowOrbitContainer == true)
                    carrier = GetCyclicalOrbitalContainer(player);
                if (carrier == null)
                    continue;

                if (!SetupCycleMission(exportRequest, carrier, rd, amount, orbitOI, oi, lvType,
                        out _, out _, clampToOutstanding: false))
                {
                    LogVerbose($"EXPORT-ORBIT setup-failed: body={oi.ObjectName} orbit={orbitOI.ObjectName} rd={rd.ID} amount={amount:0.#}");
                    continue;
                }

                RecordDispatchInSnapshot(snapshot, carrier, lvType);
                LogVerbose($"EXPORT-ORBIT dispatch: body={oi.ObjectName} orbit={orbitOI.ObjectName} rd={rd.ID} amount={amount:0.#} surplus={surplus:0.#} capacity={carrierCapacity:0.#} lv={lvType?.Name ?? "none"} support={supportDetail ?? "none"} category={matchingOption?.Category ?? "standard"}");
            }
        }
    }

    private static double GetSurfaceToOrbitPayloadCapacity(ObjectInfo source, Company player, Spacecraft carrier,
        LaunchSupportOption support, LaunchVehicleType fallbackLvType)
    {
        var carrierCapacity = carrier?.spacecraftType?.GetCargoCapacity(player) ?? 0;
        if (carrierCapacity <= 0)
            return 0;

        // The low-orbit container reports a huge pseudo-capacity. For LV/LOC lifts, the
        // real bottleneck is the selected launch support's payload on this body.
        if (carrier?.spacecraftType?.LowOrbitContainer != true)
            return carrierCapacity;

        var lvType = support?.Type ?? fallbackLvType;
        if (lvType == null || source == null || player == null)
            return carrierCapacity;

        var payload = lvType.MaxPayloadOnThisObject(source, player);
        if (double.IsNaN(payload) || payload <= 0)
            return 0;

        var carrierMass = carrier.GetMass();
        return Math.Max(0, Math.Min(carrierCapacity, payload - carrierMass));
    }

    private static bool IsLogisticsOrbitExportMission(CycleMissionsData cmd)
    {
        return cmd?.customNameFromPlanMission != null
            && cmd.customNameFromPlanMission.StartsWith("[LOGI-ORBIT]", StringComparison.Ordinal);
    }

    private static double ProcessAutoBuyRequest(Data.LogisticsRequest req, ObjectInfo requesterOI,
        ResourceDefinition rd, double requestTarget, double alreadyThere, double inFlight, Company player,
        PlannerSnapshot snapshot = null)
    {
        // Auto-Buy fills only the remaining shortage to target. If local market purchases
        // cover the request, normal route planning is skipped for that daily pass.
        if (req == null || !req.autoBuy || requesterOI == null || rd == null || player == null)
            return 0;
        if (req.autoBuyMaxPrice <= 0)
            return 0;

        var shortage = Math.Max(0, requestTarget - alreadyThere - inFlight);
        if (shortage <= 0)
            return 0;

        var bought = FulfillMarketOffers(player, requesterOI, rd, shortage, buySell: false,
            minPrice: 0, maxPrice: req.autoBuyMaxPrice, buyCheapestFirst: true, snapshot: snapshot);
        if (bought > 0)
            LogVerbose($"AUTO-BUY fulfilled: body={requesterOI?.ObjectName} rd={rd.ID} bought={bought:0.#} maxPrice={req.autoBuyMaxPrice:0.##}");
        return bought;
    }

    private static IEnumerable<Offer> GetMarketOffers(ObjectInfo oi, ResourceDefinition rd, bool buySell,
        PlannerSnapshot snapshot = null)
    {
        var key = MarketOfferKey(oi, rd, buySell);
        if (key != null && snapshot?.MarketOffersByObjectResourceSide != null)
        {
            return snapshot.MarketOffersByObjectResourceSide.TryGetValue(key, out var indexedOffers)
                ? indexedOffers
                : Enumerable.Empty<Offer>();
        }

        return MonoBehaviourSingleton<MarketOfferManager>.Instance?.Offerts ?? Enumerable.Empty<Offer>();
    }

    private static double FulfillMarketOffers(Company player, ObjectInfo oi, ResourceDefinition rd,
        double desiredAmount, bool buySell, double minPrice, double maxPrice, bool buyCheapestFirst,
        PlannerSnapshot snapshot = null)
    {
        // Fulfill through stock offers rather than mutating resources/money directly; this
        // preserves stock accounting and any market analytics/hooks attached to FullFill.
        var offers = GetMarketOffers(oi, rd, buySell, snapshot);
        if (player == null || oi == null || rd == null || offers == null || desiredAmount <= 0)
            return 0;

        var query = offers.Where(offer => offer != null
            && !offer.OfferDone
            && offer.WhereOffer == oi
            && offer.Rd == rd
            && offer.BuySell == buySell
            && offer.CountLeft > 0
            && offer.PricePerUnit >= minPrice
            && offer.PricePerUnit <= maxPrice);
        query = buyCheapestFirst
            ? query.OrderBy(offer => offer.PricePerUnit)
            : query.OrderByDescending(offer => offer.PricePerUnit);

        double fulfilled = 0;
        foreach (var offer in query.ToList())
        {
            var remaining = desiredAmount - fulfilled;
            if (remaining <= 0)
                break;

            var amount = Math.Min(remaining, offer.CountLeft);
            if (!buySell)
            {
                var affordable = offer.PricePerUnit > 0
                    ? Math.Floor(player.MoneyController.CurrentMoney / offer.PricePerUnit)
                    : 0;
                amount = Math.Min(amount, affordable);
            }
            if (amount <= 0)
                break;

            if (offer.CanFullFill(player, (float)amount, out _) && offer.FullFill(player, amount))
                fulfilled += amount;
        }
        return fulfilled;
    }
}

