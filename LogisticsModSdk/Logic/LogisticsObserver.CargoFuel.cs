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
    public static void CapLogisticsCargoForPlannerLimits(PMMissionParameter pmp)
    {
        using (TimeScope($"CapLogisticsCargoForPlannerLimits {pmp?.Start?.ObjectName ?? "null"}->{pmp?.Target?.ObjectName ?? "null"}"))
        {
        if (!IsLogisticsPlan(pmp) || pmp.CargoAll == null) return;
        if (CanSkipPlannerCapCheckForSimpleLocLaunch(pmp))
            return;

        var result = pmp.CheckCanPlanMission().planMissionResult;
        if (ApplySmallReservePropellant(pmp))
            result = pmp.CheckCanPlanMission().planMissionResult;

        if (VerboseLoggingEnabled)
        {
            var cargoStart = pmp.CargoAll.CargoCurrent;
            var capacity = (pmp.SC?.GetTypeSpaceCraft()?.GetCargoCapacity(pmp.FlyCompany) ?? 0) * Math.Max(1, pmp.SCCount);
            LogVerbose($"LOGI-CAP before: {pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} result={result} cargo={cargoStart:0.#}/{capacity:0.#} propellant={pmp.CargoAll?.cargoFuel?.cargoMassPotencjal:0.#} sc={pmp.SC?.GetSpacecraftName()} scType={pmp.SC?.GetTypeSpaceCraft()?.NameRocketType} lv={pmp.LV?.GetLaunchVehicleType()?.Name} manifest={FormatCargo(pmp.CargoAll)}");
        }
        if (result == PMMissionParameter.EPlanMissionResult.AllOk) return;
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV) && pmp.LV == null)
        {
            LogWarning($"PLAN invalid: {pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} needs an LV but none was assigned; leaving cargo unchanged");
            return;
        }

        var limitingFailure =
            result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongThrust)
            || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongMaxCapacityFuelOk)
            || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV)
            || result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongResourcesCargoLoadLimit);

        if (!limitingFailure) return;

        var cargoItems = GetResourceCargoItems(pmp.CargoAll);
        if (cargoItems.Count == 0) return;

        var original = cargoItems.Select(c => c.cargoMass).ToArray();
        var originalTotal = original.Sum();
        if (originalTotal <= 0) return;

        double bestScale = -1;
        double low = 0;
        double high = 1;
        var bestResult = result;

        for (var i = 0; i < 6; i++)
        {
            var scale = (low + high) / 2;
            ApplyCargoScale(cargoItems, original, scale);

            var check = pmp.CheckCanPlanMission().planMissionResult;
            if (check == PMMissionParameter.EPlanMissionResult.AllOk)
            {
                bestScale = scale;
                bestResult = check;
                low = scale;
            }
            else
            {
                high = scale;
            }
        }

        if (bestScale >= 0)
        {
            ApplyCargoScale(cargoItems, original, bestScale);
            if (VerboseLoggingEnabled)
            {
                var cappedTotal = cargoItems.Sum(c => c.cargoMass);
                var capacity = (pmp.SC?.GetTypeSpaceCraft()?.GetCargoCapacity(pmp.FlyCompany) ?? 0) * Math.Max(1, pmp.SCCount);
                LogVerbose($"LOGI-CAP scaled: {pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} cargo={originalTotal:0.#}->{cappedTotal:0.#}/{capacity:0.#} scale={bestScale:0.###} dueTo={result} after={bestResult} manifest={FormatCargo(pmp.CargoAll)}");
            }
        }
        else
        {
            ApplyCargoScale(cargoItems, original, 0);
            var failureReason = TranslatePlanMissionResult(result) ?? $"Mission blocked ({result})";
            LogWarning($"CAP planner cargo: no valid cargo amount found for {pmp.Start?.ObjectName} -> {pmp.Target?.ObjectName}; original={originalTotal:0.#}, result={result} - aborting cycle");
            AbortLogisticsCycle(pmp, failureReason);
        }
        }
    }

    private static bool CanSkipPlannerCapCheckForSimpleLocLaunch(PMMissionParameter pmp)
    {
        if (pmp == null || pmp.CargoAll == null || pmp.SC == null || pmp.Start == null || pmp.Target == null || pmp.FlyCompany == null)
            return false;

        var scType = pmp.SC.GetTypeSpaceCraft();
        if (scType?.LowOrbitContainer != true)
            return false;
        if (pmp.LV == null)
            return false;
        if (!pmp.Start.NeedVehicleToLaunch() || !IsOrbitOf(pmp.Target, pmp.Start))
            return false;

        var capacity = scType.GetCargoCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        if (pmp.CargoAll.CargoCurrent > capacity + 0.001)
            return false;

        try
        {
            if (!pmp.LV.CheckMaximumPayload(pmp.CargoAll, pmp.SC))
                return false;
        }
        catch
        {
            return false;
        }

        LogVerbose($"LOGI-CAP skip-simple-loc: {pmp.Start.ObjectName}->{pmp.Target.ObjectName} cargo={pmp.CargoAll.CargoCurrent:0.#}/{capacity:0.#} lv={pmp.LV.GetLaunchVehicleType()?.Name ?? "none"}");
        return true;
    }

    private static void AbortLogisticsCycle(PMMissionParameter pmp, string failureReason = null)
    {
        var player = pmp?.FlyCompany ?? MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (pmp == null || player == null || cm == null) return;

        foreach (var cmd in cm.GetAllCycleMission(player).ToList())
        {
            if (!IsLogisticsMission(cmd)) continue;
            var sameDirection = cmd.A == pmp.Start && cmd.B == pmp.Target;
            var reverseDirection = cmd.B == pmp.Start && cmd.A == pmp.Target;
            if (!sameDirection && !reverseDirection) continue;

            var reason = failureReason ?? "Ship cannot carry any payload on this route";

            if (cmd.ListSC != null)
            {
                foreach (var sci in cmd.ListSC)
                {
                    if (sci is Spacecraft sc && sc.GetCompany() == player)
                    {
                        if (_returnHomeByShipId.TryGetValue(sc.ID, out var state) && state != null)
                        {
                            state.LastBlockedReason = reason;
                            state.LastBlockedStatusNote = reason;
                        }
                        else
                        {
                            _returnHomeByShipId.Remove(sc.ID);
                        }
                    }
                }
            }

            foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                ClearPendingPlanningDelivery(cmd.B, tabRes);

            DecommitCycleStock(cmd);
            _cycleCreatedAt.Remove(cmd);
            _cyclePlanningFailures.Remove(cmd);
            if (cmd.B != null)
            {
                foreach (var tabRes in cmd.cargoAllStart?.Tab ?? Array.Empty<ResourceDefinition>())
                    MarkBlockedPlanningRetryCooldown(cmd.B, tabRes, reason);

                SetCyclePlanFailureNote(cmd.B, cmd.cargoAllStart, reason);
                SetRelayCyclePlanFailureNote(cmd, reason);
            }
            LogWarning($"ABORT LOGI cycle: {cmd.A?.ObjectName}->{cmd.B?.ObjectName} name={cmd.customNameFromPlanMission} reason={reason}");
            RemoveLogisticsCycle(cm, cmd);
            return;
        }
    }

    private static bool ApplySmallReservePropellant(PMMissionParameter pmp)
    {
        if (!ReturnFuelEnabled() || pmp?.CargoAll?.cargoFuel == null || pmp.SC == null || pmp.FlyCompany == null)
            return false;

        var fuelType = pmp.FuelNeedToStart;
        var scType = pmp.SC.GetTypeSpaceCraft();
        if (fuelType == null || scType == null || scType.SolarSC)
            return false;

        var minFuel = pmp.MINFuelCost > 0 ? pmp.MINFuelCost : pmp.AllFuelNeed;
        if (minFuel <= 0)
            return false;

        var tankCapacity = scType.GetFuelCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        var targetPropellant = Math.Min(tankCapacity, Math.Ceiling(minFuel * ReservePropellantMultiplier));
        if (targetPropellant <= 0)
            return false;

        var currentTarget = pmp.CargoAll.cargoFuel.cargoMassPotencjal;
        if (currentTarget >= targetPropellant)
            return false;

        SolarSdk.MissionLoadout.ConfigureReservePropellant(pmp, fuelType, targetPropellant);

        LogVerbose($"RETURNFUEL reserve-propellant: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} targetPropellant={targetPropellant:0.#} tank={tankCapacity:0.#} normalCargo={pmp.CargoAll.CargoCurrent:0.#} reduceFuelToMinimum={pmp.ReduceFuelToMinimum}");
        return true;
    }

    private static List<Cargo> GetResourceCargoItems(CargoAll cargoAll)
    {
        return SolarSdk.MissionLoadout.GetRegularResourceCargoItems(cargoAll);
    }

    private static bool IsResourceCargo(Cargo cargo)
    {
        return cargo != null
            && cargo.resourceTypeType == EResourceTypeType.resorces
            && cargo.resourceType != null;
    }

    private static void ApplyCargoScale(List<Cargo> cargoItems, double[] original, double scale)
    {
        for (var i = 0; i < cargoItems.Count; i++)
            cargoItems[i].cargoMass = Math.Floor(original[i] * scale);
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        if (counts == null || counts.Count == 0) return "none";
        return string.Join(",", counts.Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    private static bool ReturnFuelEnabled()
    {
        return LogisticsModSdk.Plugin.ReturnFuelEnabled?.Value ?? true;
    }

    private static double ReturnFuelSafetyMultiplier()
    {
        var value = LogisticsModSdk.Plugin.ReturnFuelSafetyMultiplier?.Value ?? 1.5;
        return Math.Max(1, value);
    }

    private static bool ReserveCargoFirst()
    {
        return LogisticsModSdk.Plugin.ReturnFuelReserveCargoFirst?.Value ?? true;
    }

    private static double GetFuelStock(ObjectInfo oi, Company player, ResourceDefinition fuelType)
    {
        if (oi == null || player == null || fuelType == null) return 0;
        return oi.GetObjectInfoData(player)?.CheckResources(fuelType) ?? 0;
    }

    /// <summary>
    /// Returns the fuel stock accessible to a spacecraft at a destination body.
    /// Orbit-only ships at a surface body access fuel from the body's low orbit,
    /// not the surface. Falls back to surface stock if orbit has insufficient fuel
    /// and the ship can self-launch from the surface.
    /// </summary>

    private static double GetAccessibleFuelStock(ObjectInfo destination, Spacecraft sc, Company player, ResourceDefinition fuelType)
    {
        if (destination == null || player == null || fuelType == null)
            return 0;

        // If destination is already an orbit, fuel stock is straightforward
        if (destination.objectTypes == global::Data.EObjectTypes.Orbit)
            return GetFuelStock(destination, player, fuelType);

        // For surface destinations, check if the ship can actually land there
        var canLand = !RequiresLaunchVehicleForSpacecraft(destination, sc, player, 0);

        var orbitOI = destination.LowOrbitCustom?.GetObjectInfo();
        var orbitStock = orbitOI != null ? GetFuelStock(orbitOI, player, fuelType) : 0;
        var surfaceStock = canLand ? GetFuelStock(destination, player, fuelType) : 0;

        // Orbit-only ships can only access orbit fuel
        if (!canLand)
            return orbitStock;

        // Ships that can land: check orbit first, add surface if accessible
        return orbitStock + surfaceStock;
    }

    private static double GetProviderAvailableAfterMinimum(ObjectInfo providerOI, ResourceDefinition rd, Company player)
    {
        if (providerOI == null || rd == null || player == null) return 0;
        var data = Data.LogisticsNetwork.Get(providerOI);
        var oid = providerOI.GetObjectInfoData(player);
        if (oid == null) return 0;
        if ((data == null || data.providers == null || !data.providers.Any(p => p.isActive && p.ResourceDefinition == rd))
            && TryGetExportedOrbitProviderParent(providerOI, rd, out _))
        {
            var exportedResult = Math.Max(0, oid.CheckResources(rd) - GetCommittedStock(providerOI, rd));
            LogSurplusDiag(providerOI, rd, player, "exported-orbit", 0, exportedResult);
            return exportedResult;
        }
        if (data == null) return 0;

        var available = oid.CheckResources(rd);
        var minKeep = data.providers
            .Where(p => p.isActive && p.ResourceDefinition == rd)
            .Sum(p => p.minimumKeep);
        var committed = GetCommittedStock(providerOI, rd);
        var result = Math.Max(0, available - minKeep - committed);
        LogSurplusDiag(providerOI, rd, player, "normal", minKeep, result);
        return result;
    }

    // DIAGNOSTIC: logs the components of the surplus calculation so we can see which
    // term is wrong when a provider reports low/zero exportable stock despite a large
    // visible stockpile. Throttled per provider+resource to avoid flooding the log.

    private static readonly Dictionary<string, DateTime> _surplusDiagLast = new Dictionary<string, DateTime>();

    private static void LogSurplusDiag(ObjectInfo providerOI, ResourceDefinition rd, Company player,
        string path, double minKeep, double result)
    {
        if (!VerboseLoggingEnabled || providerOI == null || rd == null || player == null) return;
        var key = $"{providerOI.id}:{rd.ID}";
        var now = DateTime.UtcNow;
        if (_surplusDiagLast.TryGetValue(key, out var last) && (now - last).TotalSeconds < 1.5)
            return;
        _surplusDiagLast[key] = now;

        var oid = providerOI.GetObjectInfoData(player);
        double rawValue = -1;
        var rows = oid?.ListRowResourcesData;
        if (rows != null)
        {
            foreach (var row in rows)
            {
                if (row != null && row.ResourcesType == rd)
                {
                    rawValue = row.Value;
                    break;
                }
            }
        }
        var checkRes = oid != null ? oid.CheckResources(rd) : 0;
        var committed = GetCommittedStock(providerOI, rd);
        var data = Data.LogisticsNetwork.Get(providerOI);
        var provCount = data?.providers?.Count(p => p != null && p.isActive && p.ResourceDefinition == rd) ?? 0;
        var reservedDelta = rawValue >= 0 ? rawValue - checkRes : 0;
        LogVerbose($"SURPLUS-DIAG: provider={providerOI.ObjectName} rd={rd.ID} path={path} " +
            $"rawValue={rawValue:0.#} checkResources={checkRes:0.#} reservedDelta={reservedDelta:0.#} " +
            $"minKeep={minKeep:0.#} committed={committed:0.#} activeProviderRules={provCount} availableAfterMin={result:0.#}");
    }

    private static double GetProviderMinimumShipment(ObjectInfo providerOI, ResourceDefinition rd)
    {
        if (providerOI == null || rd == null) return 0;
        var data = Data.LogisticsNetwork.Get(providerOI);
        if ((data == null || data.providers == null || !data.providers.Any(p => p.isActive && p.ResourceDefinition == rd))
            && TryGetExportedOrbitProviderParent(providerOI, rd, out var parentProvider))
        {
            return GetProviderMinimumShipment(parentProvider, rd);
        }

        return data?.providers?
            .Where(p => p != null && p.isActive && p.ResourceDefinition == rd)
            .Select(p => Math.Max(0, p.minimumShipmentAmount))
            .DefaultIfEmpty(0)
            .Max() ?? 0;
    }

    private static bool MeetsProviderMinimumShipment(ObjectInfo providerOI, ResourceDefinition rd, double amount, out string reason)
    {
        reason = null;
        var minimumShipment = GetProviderMinimumShipment(providerOI, rd);
        if (minimumShipment <= 0 || amount >= minimumShipment)
            return true;

        reason = $"Waiting for minimum {rd?.Name ?? rd?.ID ?? "resource"} SEND shipment at {providerOI?.ObjectName ?? "unknown"}: {amount:0.#}/{minimumShipment:0.#}";
        return false;
    }

    private static bool AllowsSensibleOvership(Data.LogisticsRequest req)
    {
        return req != null && req.useMinimumAmount && req.reorderActive;
    }

    private static double GetCandidateAmount(Data.LogisticsRequest req, ObjectInfo providerOI, ResourceDefinition rd,
        double remaining, double available, double capacity, Spacecraft sc = null, ObjectInfo quotaLocation = null,
        Data.LogisticsProvider providerRule = null)
    {
        if (remaining <= 0 || available <= 0 || capacity <= 0) return 0;

        var desired = remaining;
        if (AllowsSensibleOvership(req))
        {
            desired = Math.Max(desired, GetProviderMinimumShipment(providerOI, rd));
            if (sc != null)
                desired = Math.Max(desired, GetMinimumShipmentForSpacecraft(quotaLocation ?? providerOI, sc, providerRule));
        }

        return Math.Min(Math.Min(available, capacity), desired);
    }

    private static bool TryGetExportedOrbitProviderParent(ObjectInfo orbitOI, ResourceDefinition rd, out ObjectInfo parentBody)
    {
        parentBody = null;
        if (orbitOI == null || rd == null || orbitOI.objectTypes != global::Data.EObjectTypes.Orbit)
            return false;

        parentBody = orbitOI.parentObjectInfo;
        var parentData = parentBody != null ? Data.LogisticsNetwork.Get(parentBody) : null;
        if (parentData?.providers == null)
        {
            parentBody = null;
            return false;
        }

        var hasExportProvider = parentData.providers.Any(p =>
            p != null && p.isActive && p.exportToOrbit && p.ResourceDefinition == rd);
        if (!hasExportProvider)
            parentBody = null;
        return hasExportProvider;
    }

    private static bool NetworkHasProviderForFuel(ResourceDefinition fuelType, Company player)
    {
        if (fuelType == null || player == null) return false;
        foreach (var oi in Data.LogisticsNetwork.GetAllObjects())
        {
            var data = Data.LogisticsNetwork.Get(oi);
            if (data == null) continue;
            if (!data.providers.Any(p => p.isActive && p.ResourceDefinition == fuelType)) continue;
            if (GetProviderAvailableAfterMinimum(oi, fuelType, player) > 0)
                return true;
        }
        return false;
    }

    private static double EstimatePrePlanReturnFuel(Spacecraft sc, Company player)
    {
        var type = sc?.spacecraftType;
        if (type == null || player == null || type.SolarSC) return 0;
        return Math.Ceiling(type.GetFuelCapacity(player) * PrePlanReturnFuelFractionOfTank);
    }

    private static Cargo FindResourceCargo(CargoAll cargoAll, ResourceDefinition rd)
    {
        return SolarSdk.MissionLoadout.FindRegularResourceCargo(cargoAll, rd);
    }

    private static void AddOrIncreaseResourceCargo(CargoAll cargoAll, ResourceDefinition rd, double amount)
    {
        SolarSdk.MissionLoadout.AddOrIncreaseResourceCargo(cargoAll, rd, amount);
    }

    private static double CargoAmountFor(CargoAll cargoAll, ResourceDefinition rd)
    {
        return SolarSdk.MissionLoadout.GetRegularResourceMass(cargoAll, rd);
    }

    private static bool CargoContainsResource(CargoAll cargoAll, ResourceDefinition rd)
    {
        return SolarSdk.MissionLoadout.ContainsRegularResource(cargoAll, rd);
    }

    private static bool CargoContainsResource(InfoCargoCyclicalMission cargoInfo, ResourceDefinition rd)
    {
        return cargoInfo?.Tab != null && cargoInfo.Tab.Any(tabRd => tabRd == rd);
    }

    private static double ReduceNonFuelCargo(CargoAll cargoAll, ResourceDefinition fuelType, double amountToRemove)
    {
        return SolarSdk.MissionLoadout.ReduceNonFuelResourceCargo(cargoAll, fuelType, amountToRemove);
    }

    private static bool BuildCargoManifestWithReturnFuel(Data.LogisticsRequest req, ResourceDefinition rd,
        double amount, ObjectInfo requesterOI, ObjectInfo providerOI, Spacecraft sc, Company player,
        double capacity, LaunchVehicleType lvType, out CargoAll cargoAll, out double normalCargo, out double reserveFuelCargo,
        out ResourceDefinition blockedFuelType, out double blockedFuelShortfall, out bool waitingForFuelProbe,
        Data.LogisticsProvider providerRule = null)
    {
        cargoAll = CargoAll.CreateCargoEmpty();
        normalCargo = Math.Min(amount, capacity);
        reserveFuelCargo = 0;
        blockedFuelType = null;
        blockedFuelShortfall = 0;
        waitingForFuelProbe = false;

        if (rd == null || normalCargo <= 0 || capacity <= 0)
            return false;

        AddOrIncreaseResourceCargo(cargoAll, rd, normalCargo);
        if (!ShouldReserveReturnFuel(providerOI, requesterOI, sc, player, providerRule))
        {
            normalCargo = CargoAmountFor(cargoAll, rd);
            LogVerbose($"RETURNFUEL reserve-skipped: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType ?? "null"} lv={lvType?.Name ?? "none"} reason=no-return-fuel-required manifest={FormatCargo(cargoAll)}");
            return cargoAll.CargoCurrent > 0;
        }

        if (!TryEstimateReturnFuelRequirement(providerOI, requesterOI, sc, player, cargoAll, lvType,
                out var waitingForProbe,
                out var fuelType, out var requiredReserve, out var destinationStock))
        {
            waitingForFuelProbe = waitingForProbe;
            if (waitingForFuelProbe)
            {
                LogVerbose($"RETURNFUEL estimate-pending: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType ?? "null"} rd={rd.ID} cargo={normalCargo:0.#} lv={lvType?.Name ?? "none"} manifest={FormatCargo(cargoAll)}");
                return false;
            }
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL estimate-skipped: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType ?? "null"} rd={rd.ID} cargo={normalCargo:0.#} lv={lvType?.Name ?? "none"} manifest={FormatCargo(cargoAll)}");
            normalCargo = CargoAmountFor(cargoAll, rd);
            return cargoAll.CargoCurrent > 0;
        }

        var existingFuelCargo = CargoAmountFor(cargoAll, fuelType);
        var shortfall = Math.Max(0, requiredReserve - destinationStock - existingFuelCargo);
        if (shortfall <= 0)
        {
            normalCargo = CargoAmountFor(cargoAll, rd);
            LogVerbose($"RETURNFUEL trust-domestic-stockpile: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} existingFuelCargo={existingFuelCargo:0.#} manifest={FormatCargo(cargoAll)}");
            return cargoAll.CargoCurrent > 0;
        }

        var providerFuelAvailable = GetProviderAvailableAfterMinimum(providerOI, fuelType, player);
        var maxFuelCargo = capacity * MaxReturnFuelCargoDisplacementFraction;
        var maxAdditionalFuelCargo = Math.Max(0, maxFuelCargo - existingFuelCargo);
        var fuelToAdd = Math.Min(shortfall, Math.Min(providerFuelAvailable, maxAdditionalFuelCargo));
        LogVerbose($"RETURNFUEL manifest-calc: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} existingFuelCargo={existingFuelCargo:0.#} shortfall={shortfall:0.#} providerFuel={providerFuelAvailable:0.#} capacity={capacity:0.#} maxFuelCargo={maxFuelCargo:0.#} plannedFuelAdd={fuelToAdd:0.#} before={FormatCargo(cargoAll)}");
        double reduced = 0;

        var freeCapacity = Math.Max(0, capacity - cargoAll.CargoCurrent);
        if (fuelToAdd > freeCapacity)
        {
            var displacementNeeded = fuelToAdd - freeCapacity;
            reduced = ReduceNonFuelCargo(cargoAll, fuelType, displacementNeeded);
        }

        freeCapacity = Math.Max(0, capacity - cargoAll.CargoCurrent);
        fuelToAdd = Math.Min(fuelToAdd, freeCapacity);
        if (fuelToAdd > 0)
        {
            AddOrIncreaseResourceCargo(cargoAll, fuelType, fuelToAdd);
            reserveFuelCargo = fuelToAdd;
        }

        existingFuelCargo = CargoAmountFor(cargoAll, fuelType);
        var remainingShortfall = Math.Max(0, requiredReserve - destinationStock - existingFuelCargo);
        if (remainingShortfall > 0)
        {
            blockedFuelType = fuelType;
            blockedFuelShortfall = remainingShortfall;
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-shortfall: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.spacecraftType?.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} providerFuel={providerFuelAvailable:0.#} fuelAdded={reserveFuelCargo:0.#} shortfall={remainingShortfall:0.#} manifest={FormatCargo(cargoAll)}");
            return false;
        }

        normalCargo = CargoAmountFor(cargoAll, rd);
        if (normalCargo <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL no-request-cargo-left: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} rd={rd.ID} fuel={fuelType.ID} fuelAdded={reserveFuelCargo:0.#} reducedCargo={reduced:0.#} manifest={FormatCargo(cargoAll)}");
            return false;
        }
        LogVerbose($"RETURNFUEL ship-reserve-manifest: route={providerOI?.ObjectName}->{requesterOI?.ObjectName} ship={sc?.GetSpacecraftName() ?? "null"} scType={sc?.spacecraftType?.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} fuelAdded={reserveFuelCargo:0.#} reducedCargo={reduced:0.#} normalCargo={normalCargo:0.#} manifest={FormatCargo(cargoAll)}");
        return cargoAll.CargoCurrent > 0;
    }

    private static bool ShouldReserveReturnFuel(ObjectInfo providerOI, ObjectInfo requesterOI, Spacecraft sc, Company player, Data.LogisticsProvider providerRule = null)
    {
        var scType = sc?.GetTypeSpaceCraft();
        if (!ReturnFuelEnabled() || providerOI == null || requesterOI == null || sc == null || player == null || scType == null)
            return false;

        if (!UseFuelProbeForSpacecraft(providerOI, sc, providerRule))
            return false;

        if (scType.SolarSC || scType.LowOrbitContainer || scType.MagneticCatapult)
            return false;

        if (scType.GetFuelCapacity(player) <= 0)
            return false;

        return true;
    }

    private static bool UseFuelProbeForSpacecraft(ObjectInfo quotaLocation, Spacecraft sc, Data.LogisticsProvider providerRule = null)
    {
        if (quotaLocation == null || sc?.spacecraftType == null)
            return true;

        var assignedProvider = providerRule != null && Data.LogisticsNetwork.IsSpacecraftAssignedToProvider(sc.ID, providerRule)
            ? providerRule
            : Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID);
        var assignedSetting = Data.LogisticsNetwork.GetProviderSpacecraftSetting(assignedProvider, sc);
        if (assignedSetting != null)
            return assignedSetting.useFuelProbe;

        var data = Data.LogisticsNetwork.Get(quotaLocation);
        var quota = data?.spacecraftQuota?
            .FirstOrDefault(q => Data.LogisticsNetwork.QuotaMatches(q, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"));
        return quota?.useFuelProbe ?? true;
    }

    private static bool TryEstimateReturnFuelRequirement(ObjectInfo providerOI, ObjectInfo requesterOI,
        Spacecraft sc, Company player, CargoAll cargoAll, LaunchVehicleType lvType,
        out bool waitingForProbe,
        out ResourceDefinition fuelType, out double requiredReserve, out double destinationStock)
    {
        waitingForProbe = false;
        fuelType = null;
        requiredReserve = 0;
        destinationStock = 0;
        if (!ReturnFuelEnabled())
        {
            LogVerbose($"RETURNFUEL probe-skip: disabled route={providerOI?.ObjectName}->{requesterOI?.ObjectName}");
            return false;
        }

        if (providerOI == null || requesterOI == null || sc == null || player == null || cargoAll == null)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL probe-skip: missing-input provider={providerOI?.ObjectName ?? "null"} requester={requesterOI?.ObjectName ?? "null"} ship={sc?.GetSpacecraftName() ?? "null"} player={player?.name ?? "null"} cargo={(cargoAll == null ? "null" : FormatCargo(cargoAll))}");
            return false;
        }

        var scType = sc.GetTypeSpaceCraft();
        if (scType == null || scType.SolarSC)
        {
            LogVerbose($"RETURNFUEL probe-skip: unsupported-ship route={providerOI.ObjectName}->{requesterOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType?.NameRocketType ?? "null"} solar={scType?.SolarSC.ToString() ?? "null"}");
            return false;
        }

        var probeKey = BuildReturnFuelProbeKey(providerOI, requesterOI, sc, player, lvType);
        if (!_returnFuelProbeCache.TryGetValue(probeKey, out var probe) || (!probe.Pending && !probe.Complete))
        {
            StartAsyncReturnFuelProbe(probeKey, providerOI, requesterOI, sc, player, lvType);
            waitingForProbe = true;
            return false;
        }

        if (probe.Pending)
        {
            waitingForProbe = true;
            return false;
        }

        if (probe.FuelType == null)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL probe-no-fueltype-cached: returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} lv={lvType?.Name ?? "none"} result={probe.Result} failure={probe.FailureReason ?? "none"}");
            return false;
        }

        fuelType = probe.FuelType;
        requiredReserve = probe.RequiredReserve;
        destinationStock = GetAccessibleFuelStock(requesterOI, sc, player, fuelType);
        LogVerbose($"RETURNFUEL probe-cache-hit: outbound={providerOI.ObjectName}->{requesterOI.ObjectName} return={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} lv={lvType?.Name ?? "none"} result={probe.Result} fuel={fuelType.ID} allFuel={probe.AllFuelNeed:0.#} minFuel={probe.MinFuelCost:0.#} fuelNeed={probe.FuelNeed:0.#} leftOver={probe.LeftOverFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} tank={scType.GetFuelCapacity(player):0.#} cargo={FormatCargo(cargoAll)}");
        if (requiredReserve <= 0)
        {
            var fallbackReserve = Math.Ceiling(scType.GetCargoCapacity(player) * MaxReturnFuelCargoDisplacementFraction);
            requiredReserve = fallbackReserve;
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL probe-zero-reserve-fallback: returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType.NameRocketType} lv={lvType?.Name ?? "none"} result={probe.Result} fuel={fuelType.ID} allFuel={probe.AllFuelNeed:0.#} minFuel={probe.MinFuelCost:0.#} fallbackReserve={fallbackReserve:0.#} destStock={destinationStock:0.#}");
        }
        return requiredReserve > 0;
    }

    private static string BuildReturnFuelProbeKey(ObjectInfo providerOI, ObjectInfo requesterOI,
        Spacecraft sc, Company player, LaunchVehicleType lvType)
    {
        var transfer = GetTransferTypeForSpacecraft(providerOI, sc);
        var scType = sc?.spacecraftType ?? sc?.GetTypeSpaceCraft();
        var fuelCapacity = scType == null || player == null ? 0 : scType.GetFuelCapacity(player);
        var cargoCapacity = scType == null || player == null ? 0 : scType.GetCargoCapacity(player);
        return string.Join("|",
            player?.ID ?? "company",
            providerOI?.id.ToString() ?? "provider",
            requesterOI?.id.ToString() ?? "requester",
            scType?.ID ?? scType?.NameRocketType ?? "sc",
            $"tank={Math.Round(fuelCapacity, 1)}",
            $"cargo={Math.Round(cargoCapacity, 1)}",
            lvType?.ID ?? lvType?.Name ?? "no-lv",
            transfer.ToString(),
            $"margin={ReturnFuelSafetyMultiplier():0.###}");
    }

    private static void StoreReturnFuelProbe(string key, ReturnFuelProbeState probe)
    {
        if (string.IsNullOrEmpty(key) || probe == null)
            return;

        if (!_returnFuelProbeCache.ContainsKey(key))
            _returnFuelProbeCacheOrder.Enqueue(key);
        _returnFuelProbeCache[key] = probe;

        var attempts = 0;
        while (_returnFuelProbeCache.Count > MaxReturnFuelProbeCacheEntries
            && _returnFuelProbeCacheOrder.Count > 0
            && attempts++ < MaxReturnFuelProbeCacheEntries * 2)
        {
            var evict = _returnFuelProbeCacheOrder.Dequeue();
            if (!_returnFuelProbeCache.TryGetValue(evict, out var existing))
                continue;
            if (existing.Pending)
            {
                _returnFuelProbeCacheOrder.Enqueue(evict);
                continue;
            }

            _returnFuelProbeCache.Remove(evict);
            LogVerbose($"RETURNFUEL probe-cache-evict: key={evict}");
        }
    }

    private static void StartAsyncReturnFuelProbe(string key, ObjectInfo providerOI, ObjectInfo requesterOI,
        Spacecraft sc, Company player, LaunchVehicleType lvType)
    {
        using (TimeScope($"StartAsyncReturnFuelProbe {requesterOI?.ObjectName ?? "null"}->{providerOI?.ObjectName ?? "null"}"))
        {
        if (string.IsNullOrEmpty(key) || providerOI == null || requesterOI == null || sc == null || player == null)
            return;
        if (_returnFuelProbeCache.TryGetValue(key, out var existing) && existing.Pending)
            return;

        var scType = sc.GetTypeSpaceCraft();
        var fuelType = scType?.GetFuelType();
        var probe = new ReturnFuelProbeState
        {
            Pending = true,
            Complete = false,
            RequestedAt = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now,
            FuelType = fuelType
        };
        StoreReturnFuelProbe(key, probe);

        var probeCargo = CargoAll.CreateCargoEmpty();
        var probeSpacecraft = new PlannerSpacecraftInfo(sc, requesterOI);
        var pmp = new PMMissionParameter();
        pmp.SetCompany(player);
        pmp.SetTabDestination(requesterOI, providerOI);
        pmp.SetTabCargo(probeCargo);
        pmp.SetTabSC(probeSpacecraft);
        pmp.SetTabLV(new List<ILaunchVehicleInfo>(), 0);
        pmp.ForCyclicalMission = true;
        pmp.ReduceFuelToMinimum = false;
        pmp.TryFixWrongThrust = true;
        pmp.TrajectoryColor = Color.blue;
        pmp.SetMissionOrigin(MissionInfo.EMissionCreator.Other);
        var transfer = GetTransferTypeForSpacecraft(providerOI, sc);
        // Moon-case routes (planet ↔ moon) use a slider, not a porkchop plot.
        // Setting ClickFastestButton on a moon case causes ButtonFastestClickButton
        // to run its porkchop grid search against an empty/invalid grid, corrupting
        // the trajectory and producing garbage fuel estimates. The probe's
        // PlannerSpacecraftInfo is invisible to both our prefix guards and stock's
        // moon-case early-exit in ButtonFastestClickButton (because
        // TransferTypeMoonCase defaults to Optimal, not Fastest).
        var isMoonCase = IsMoonCaseRoute(requesterOI, providerOI);
        pmp.TryFastAsPossible = transfer == ETransferType.Fastest && !isMoonCase;
        pmp.ClickFastestButton = transfer == ETransferType.Fastest && !isMoonCase;
        if (isMoonCase)
            pmp.TransferTypeMoonCase = ETransferType.Optimal;
        ApplyCachedPrecalculateData(pmp);

        if (VerboseLoggingEnabled)
            LogVerbose($"RETURNFUEL async-probe-start: key={key} returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} scType={scType?.NameRocketType ?? "null"} probePos={probeSpacecraft.GetActualPosition()?.ObjectName ?? "null"} transfer={transfer} moonCase={isMoonCase} fuel={fuelType?.ID ?? "null"}");
        MonoBehaviourSingleton<GameManager>.Instance.SetPMParameterForCodeJobSystem(pmp, () =>
        {
            using (TimeScope($"ReturnFuelProbeCallback {requesterOI?.ObjectName ?? "null"}->{providerOI?.ObjectName ?? "null"}"))
            {
            var result = pmp.CheckCanPlanMission().planMissionResult;
            var callbackFuelType = pmp.FuelNeedToStart ?? fuelType;
            var planFuelNeed = Math.Max(pmp.AllFuelNeed, pmp.MINFuelCost);
            var tankCapacity = scType?.GetFuelCapacity(player) ?? 0;
            var estimatedReturnFuel = Math.Min(Math.Max(0, planFuelNeed), tankCapacity * Math.Max(1, pmp.SCCount));
            var requiredReserve = Math.Ceiling(estimatedReturnFuel * ReturnFuelSafetyMultiplier());

            probe.Pending = false;
            probe.Complete = true;
            probe.CompletedAt = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.Now;
            probe.FuelType = callbackFuelType;
            probe.FuelNeed = pmp.FuelNeed;
            probe.MinFuelCost = pmp.MINFuelCost;
            probe.AllFuelNeed = pmp.AllFuelNeed;
            probe.LeftOverFuel = pmp.LeftOverFuel;
            probe.RequiredReserve = requiredReserve;
            probe.Result = result;
            probe.FailureReason = result == PMMissionParameter.EPlanMissionResult.AllOk ? null : result.ToString();
            CachePrecalculateData(pmp, "return-fuel-probe");

            if (VerboseLoggingEnabled)
                LogVerbose($"RETURNFUEL async-probe-result: key={key} returnRoute={requesterOI.ObjectName}->{providerOI.ObjectName} ship={sc.GetSpacecraftName()} result={result} fuel={callbackFuelType?.ID ?? "null"} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} fuelNeed={pmp.FuelNeed:0.#} leftOver={pmp.LeftOverFuel:0.#} reserve={requiredReserve:0.#} depart={pmp.DepartureTimeDate:yyyy-MM-dd} arrive={pmp.Arrival:yyyy-MM-dd}");
            }
        });
        }
    }

    private static void EnsureReturnFuelReserveFromPlan(PMMissionParameter pmp)
    {
        // This hook runs after stock has calculated the plan. If stock says the return leg
        // needs fuel at the destination, reserve cargo capacity now by displacing payload.
        if (!ReturnFuelEnabled() || pmp?.CargoAll == null || pmp.SC == null || pmp.FlyCompany == null || pmp.Target == null)
            return;

        var fuelType = pmp.FuelNeedToStart;
        var scType = pmp.SC.GetTypeSpaceCraft();
        if (fuelType == null || scType == null || scType.SolarSC)
            return;

        var planFuelNeed = pmp.MINFuelCost > 0 ? Math.Min(pmp.AllFuelNeed, pmp.MINFuelCost) : pmp.AllFuelNeed;
        var estimatedReturnFuel = Math.Min(Math.Max(0, planFuelNeed), scType.GetFuelCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount));
        var requiredReserve = Math.Ceiling(estimatedReturnFuel * ReturnFuelSafetyMultiplier());
        var destinationStock = pmp.SC is Spacecraft probeShip
            ? GetAccessibleFuelStock(pmp.Target, probeShip, pmp.FlyCompany, fuelType)
            : GetFuelStock(pmp.Target, pmp.FlyCompany, fuelType);
        var existingFuelCargo = CargoAmountFor(pmp.CargoAll, fuelType);
        var shortfall = Math.Max(0, requiredReserve - destinationStock - existingFuelCargo);

        if (shortfall <= 0)
        {
            LogVerbose($"RETURNFUEL trust-domestic-stockpile-plan: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} estimated={estimatedReturnFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} existingFuelCargo={existingFuelCargo:0.#} manifest={FormatCargo(pmp.CargoAll)}");
            return;
        }

        var capacity = scType.GetCargoCapacity(pmp.FlyCompany) * Math.Max(1, pmp.SCCount);
        var providerFuelAvailable = GetProviderAvailableAfterMinimum(pmp.Start, fuelType, pmp.FlyCompany);
        var fuelToAdd = Math.Min(shortfall, providerFuelAvailable);
        if (fuelToAdd <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-shortfall: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} estimated={estimatedReturnFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} shortfall={shortfall:0.#} providerFuel={providerFuelAvailable:0.#}");
            return;
        }

        var maxFuelCargo = capacity * MaxReturnFuelCargoDisplacementFraction;
        var maxAdditionalFuelCargo = Math.Max(0, maxFuelCargo - existingFuelCargo);
        fuelToAdd = Math.Min(fuelToAdd, maxAdditionalFuelCargo);
        if (fuelToAdd <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-cap-reached: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} maxFuelCargo={maxFuelCargo:0.#} existingFuelCargo={existingFuelCargo:0.#} manifest={FormatCargo(pmp.CargoAll)}");
            return;
        }

        var freeCapacity = Math.Max(0, capacity - pmp.CargoAll.CargoCurrent);
        double reduced = 0;
        if (fuelToAdd > freeCapacity)
        {
            var displacementNeeded = fuelToAdd - freeCapacity;
            reduced = ReduceNonFuelCargo(pmp.CargoAll, fuelType, displacementNeeded);
        }

        freeCapacity = Math.Max(0, capacity - pmp.CargoAll.CargoCurrent);
        fuelToAdd = Math.Min(fuelToAdd, freeCapacity);
        if (fuelToAdd <= 0)
        {
            if (VerboseLoggingEnabled)
                LogWarning($"RETURNFUEL plan-defer-no-room: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} reserve={requiredReserve:0.#} capacity={capacity:0.#} reducedCargo={reduced:0.#} manifest={FormatCargo(pmp.CargoAll)}");
            return;
        }

        AddOrIncreaseResourceCargo(pmp.CargoAll, fuelType, fuelToAdd);
        LogVerbose($"RETURNFUEL ship-reserve-plan: route={pmp.Start?.ObjectName}->{pmp.Target?.ObjectName} ship={scType.NameRocketType} fuel={fuelType.ID} allFuel={pmp.AllFuelNeed:0.#} minFuel={pmp.MINFuelCost:0.#} estimated={estimatedReturnFuel:0.#} reserve={requiredReserve:0.#} destStock={destinationStock:0.#} shortfall={shortfall:0.#} maxFuelCargo={maxFuelCargo:0.#} fuelAdded={fuelToAdd:0.#} reducedCargo={reduced:0.#} manifest={FormatCargo(pmp.CargoAll)}");
    }

    private static string FormatCargo(CargoAll cargoAll)
    {
        return SolarSdk.MissionLoadout.FormatCargo(cargoAll);
    }
}

