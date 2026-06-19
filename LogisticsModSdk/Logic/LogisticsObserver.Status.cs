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
    public static string TranslatePlanMissionResult(PMMissionParameter.EPlanMissionResult result)
    {
        if (result == PMMissionParameter.EPlanMissionResult.AllOk)
            return null;

        var parts = new List<string>();
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.NoFuelCantBuy))
            parts.Add("Insufficient fuel at source");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongRemoveFuel))
            parts.Add("Cannot load fuel");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongThrust))
            parts.Add("Insufficient thrust for route");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongMaxCapacityFuelOk))
            parts.Add("Route requires too much fuel for any payload");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongLV))
            parts.Add("Launch vehicle required");
        if (result.HasFlag(PMMissionParameter.EPlanMissionResult.WrongResourcesCargoLoadLimit))
            parts.Add("Cargo exceeds load limit");

        if (parts.Count == 0)
            return $"Mission blocked ({result})";

        return string.Join("; ", parts);
    }

    public struct QuotaShipStatus
    {
        public string Name;
        public string Location;
        public string StatusText;
        public DateTime? ETA;
        public ShipState State;
    }

    private static QuotaShipStatus BuildShipStatus(Spacecraft sc, ObjectInfo home, bool forceReserved = false)
    {
        var status = new QuotaShipStatus
        {
            Name = sc?.GetSpacecraftName() ?? sc?.spacecraftType?.NameRocketType ?? "SC",
            Location = sc?.CurrentlyOnThisObject?.ObjectName ?? "?"
        };
        if (sc == null)
            return status;

        ReturnHomeState returnState = null;
        var isTracked = sc.ID >= 0 && _returnHomeByShipId.TryGetValue(sc.ID, out returnState) && returnState != null;
        var mi = sc.GetMissionInfo();

        if (sc.CurrentPhase == Spacecraft.EPhase.Fly || sc.CurrentPhase == Spacecraft.EPhase.Launch
            || sc.CurrentPhase == Spacecraft.EPhase.Landing)
        {
            status.State = ShipState.InTransit;
            status.Location = mi?.target?.ObjectName ?? sc.CurrentlyOnThisObject?.ObjectName ?? "?";
            status.StatusText = forceReserved
                ? (sc.CurrentPhase == Spacecraft.EPhase.Landing ? "Reserved landing" : "Reserved transit")
                : (sc.CurrentPhase == Spacecraft.EPhase.Landing ? "Landing" : "In transit");
            if (mi != null && mi.DateArrive != default)
                status.ETA = mi.DateArrive;
            return status;
        }

        if (sc.CurrentPhase == Spacecraft.EPhase.PlanedMission)
        {
            status.State = ShipState.Pending;
            status.Location = mi?.target?.ObjectName ?? "?";
            status.StatusText = forceReserved ? "Reserved planned" : "Planned";
            if (mi != null && mi.DateArrive != default)
                status.ETA = mi.DateArrive;
            return status;
        }

        if (isTracked && returnState != null)
        {
            var current = sc.CurrentlyOnThisObject;
            if (IsReturnRetryCoolingDown(returnState, out _)
                || (returnState.ConsecutiveReturnCycleFailures > 0 && !string.IsNullOrEmpty(returnState.LastBlockedReason)))
            {
                status.State = ShipState.Blocked;
                status.Location = current?.ObjectName ?? "?";
                status.StatusText = returnState.LastBlockedStatusNote ?? returnState.LastBlockedReason ?? "Blocked";
                return status;
            }

            if (home != null && SameObjectInfo(current, home) && sc.CurrentPhase == Spacecraft.EPhase.None)
            {
                status.State = forceReserved ? ShipState.Pending : ShipState.Idle;
                status.Location = home.ObjectName;
                status.StatusText = forceReserved ? "Reserved" : "Idle";
                return status;
            }

            if (sc.CurrentPhase == Spacecraft.EPhase.None && home != null && !SameObjectInfo(current, home))
            {
                status.State = ShipState.Pending;
                status.Location = current?.ObjectName ?? "?";
                status.StatusText = forceReserved ? "Reserved away" : "Awaiting return";
                return status;
            }

            status.State = ShipState.Pending;
            status.Location = current?.ObjectName ?? "?";
            status.StatusText = forceReserved ? "Reserved return" : "Returning";
            if (mi != null && mi.DateArrive != default)
                status.ETA = mi.DateArrive;
            return status;
        }

        status.State = forceReserved ? ShipState.Pending : ShipState.Idle;
        status.Location = sc.CurrentlyOnThisObject?.ObjectName ?? home?.ObjectName ?? "?";
        status.StatusText = forceReserved ? "Reserved" : "Idle";
        return status;
    }

    public static List<QuotaShipStatus> GetShipStatusesForQuota(ObjectInfo quotaHome, Data.ShipQuotaEntry quota)
    {
        var result = new List<QuotaShipStatus>();
        if (quotaHome == null || quota == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;

        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();

        foreach (var sc in ships)
        {
            if (sc == null || sc.spacecraftType == null || sc.GetCompany() != player)
                continue;
            if (!Data.LogisticsNetwork.QuotaMatches(quota, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"))
                continue;
            var assignedProvider = sc.ID >= 0 ? Data.LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID) : null;
            var isAssignedToSend = assignedProvider != null;

            ReturnHomeState returnState = null;
            var isTracked = sc.ID >= 0 && _returnHomeByShipId.TryGetValue(sc.ID, out returnState) && returnState != null;
            var isHomeHere = isTracked && SameObjectInfo(returnState?.Home, quotaHome);
            var isIdleAtHome = sc.CurrentPhase == Spacecraft.EPhase.None
                && SameObjectInfo(sc.CurrentlyOnThisObject, quotaHome)
                && Data.LogisticsNetwork.IsSpacecraftReadyForLogistics(sc, player, cm);

            if (!isHomeHere && !isIdleAtHome && !(isAssignedToSend && SameObjectInfo(sc.CurrentlyOnThisObject, quotaHome)))
                continue;

            result.Add(BuildShipStatus(sc, quotaHome, isAssignedToSend));
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return result;
    }

    public static List<QuotaShipStatus> GetAllShipsForGetRequest(ObjectInfo target, ResourceDefinition rd)
    {
        var result = new List<QuotaShipStatus>();
        if (target == null || rd == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        var seenIds = new HashSet<int>();
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.MinValue;
        var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;

        // 1. Ships in-flight or planned toward target with this resource (from missions)
        if (mm?.ListMissionInfo != null)
        {
            foreach (var mi in mm.ListMissionInfo)
            {
                if (mi == null || mi.complete || mi.cancel || mi.company != player)
                    continue;
                if (mi.target != target)
                    continue;
                if (!IsLogisticsMissionInfo(mi))
                    continue;
                if (mi.cargoAll != null && !CargoContainsResource(mi.cargoAll, rd)
                    && (mi.cargoAll.listCargoToOrbit == null || !mi.cargoAll.listCargoToOrbit.Any(c => c != null && c.resourceType == rd && c.cargoMass > 0)))
                    continue;

                if (mi.spacecraftInfo2 is Spacecraft sc && sc.ID >= 0 && seenIds.Add(sc.ID))
                    result.Add(BuildShipStatus(sc, target));
            }
        }

        // 2. Ships in _returnHomeByShipId whose destination is this target for this resource
        //    (delivered cargo and are now at destination, awaiting return or blocked)
        foreach (var kv in _returnHomeByShipId)
        {
            var state = kv.Value;
            if (state == null || !SameObjectInfo(state.Destination, target) || state.Resource != rd)
                continue;
            if (!seenIds.Add(kv.Key))
                continue;

            var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
            if (ships == null) continue;
            var sc = ships.FirstOrDefault(s => s != null && s.ID == kv.Key && s.GetCompany() == player);
            if (sc != null)
                result.Add(BuildShipStatus(sc, state.Home));
        }

        // 3. Ships from active LOGI cycles targeting this body for this resource
        //    that haven't produced a mission yet (still at source, planning or blocked)
        if (cm != null)
        {
            foreach (var cmd in cm.GetAllCycleMission(player))
            {
                if (cmd == null || cmd.CheckComplete() || !IsLogisticsDeliveryMission(cmd))
                    continue;
                if (!SameObjectInfo(cmd.B, target))
                    continue;
                if (!CargoContainsResource(cmd.cargoAllStart, rd))
                    continue;
                if (cmd.ListSC == null) continue;

                foreach (var sci in cmd.ListSC)
                {
                    if (sci is Spacecraft sc && sc.GetCompany() == player && sc.ID >= 0 && seenIds.Add(sc.ID))
                        result.Add(BuildShipStatus(sc, cmd.A));
                }
            }
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            if (s != 0) return s;
            var etaA = a.ETA ?? DateTime.MaxValue;
            var etaB = b.ETA ?? DateTime.MaxValue;
            var eta = etaA.CompareTo(etaB);
            return eta != 0 ? eta : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    public static List<QuotaShipStatus> GetAllShipsForSendProvider(ObjectInfo source, ResourceDefinition rd, Data.LogisticsProvider provider)
    {
        var result = new List<QuotaShipStatus>();
        if (source == null || rd == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        var seenIds = new HashSet<int>();
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        var mm = MonoBehaviourSingleton<MissionInfoManager>.Instance;

        // 1. Explicitly assigned ships
        if (provider?.assignedSpacecraftIds != null)
        {
            var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
            if (ships != null)
            {
                foreach (var sc in ships)
                {
                    if (sc == null || sc.GetCompany() != player || sc.ID < 0)
                        continue;
                    if (!provider.assignedSpacecraftIds.Contains(sc.ID))
                        continue;
                    if (!seenIds.Add(sc.ID))
                        continue;
                    result.Add(BuildShipStatus(sc, source));
                }
            }
        }

        // 2. Shared-pool ships: from active LOGI cycles originating here for this resource
        if (provider == null || provider.useSharedSpacecraftPool)
        {
            if (cm != null)
            {
                foreach (var cmd in cm.GetAllCycleMission(player))
                {
                    if (cmd == null || cmd.CheckComplete() || !IsLogisticsDeliveryMission(cmd))
                        continue;
                    if (!SameObjectInfo(cmd.A, source))
                        continue;
                    if (!CargoContainsResource(cmd.cargoAllStart, rd))
                        continue;
                    if (cmd.ListSC == null) continue;

                    foreach (var sci in cmd.ListSC)
                    {
                        if (sci is Spacecraft sc && sc.GetCompany() == player && sc.ID >= 0 && seenIds.Add(sc.ID))
                            result.Add(BuildShipStatus(sc, source));
                    }
                }
            }

            // Also check in-flight missions from this source
            if (mm?.ListMissionInfo != null)
            {
                foreach (var mi in mm.ListMissionInfo)
                {
                    if (mi == null || mi.complete || mi.cancel || mi.company != player)
                        continue;
                    if (mi.start != source)
                        continue;
                    if (!IsLogisticsMissionInfo(mi))
                        continue;
                    if (mi.cargoAll != null && !CargoContainsResource(mi.cargoAll, rd))
                        continue;

                    if (mi.spacecraftInfo2 is Spacecraft sc && sc.ID >= 0 && seenIds.Add(sc.ID))
                        result.Add(BuildShipStatus(sc, source));
                }
            }

            // Ships in return-home that were sent from this source with this resource
            foreach (var kv in _returnHomeByShipId)
            {
                var state = kv.Value;
                if (state == null || !SameObjectInfo(state.Home, source) || state.Resource != rd)
                    continue;
                if (!seenIds.Add(kv.Key))
                    continue;

                var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip;
                var sc = ships?.FirstOrDefault(s => s != null && s.ID == kv.Key && s.GetCompany() == player);
                if (sc != null)
                    result.Add(BuildShipStatus(sc, source));
            }
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    public static List<QuotaShipStatus> GetShipStatusesForAssignedIds(IEnumerable<int> shipIds, ObjectInfo home, bool forceReserved = true)
    {
        var result = new List<QuotaShipStatus>();
        var ids = shipIds?.Where(id => id >= 0).Distinct().ToHashSet();
        if (ids == null || ids.Count == 0)
            return result;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();
        foreach (var sc in ships)
        {
            if (sc == null || sc.GetCompany() != player || !ids.Contains(sc.ID))
                continue;
            result.Add(BuildShipStatus(sc, home, forceReserved));
        }

        result.Sort((a, b) =>
        {
            var s = a.State.CompareTo(b.State);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    public static bool IsLogisticsMissionInfo(MissionInfo mi)
    {
        return mi?.missionName != null
            && mi.missionName.StartsWith("[LOGI", StringComparison.Ordinal);
    }

    public static void RegisterLogisticsMissionInfo(MissionInfo mi)
    {
        if (!IsLogisticsMissionInfo(mi))
            return;

        _knownLogisticsMissionInfos[mi.id] = mi;
        var dispatchId = SolarSdk.CyclicalMissions.FindDispatchId(mi);
        if (!string.IsNullOrEmpty(dispatchId))
            SolarSdk.CyclicalMissions.RegisterMissionInfo(dispatchId, mi);
    }

    public static void SetCyclePlanFailureNote(ObjectInfo target, InfoCargoCyclicalMission cargoInfo, string tooltip)
    {
        if (target == null || string.IsNullOrEmpty(tooltip)) return;
        var data = Data.LogisticsNetwork.Get(target);
        if (data?.requests == null) return;

        var resources = cargoInfo?.Tab;
        foreach (var req in data.requests)
        {
            if (req.status != Data.LogisticsRequestStatus.InProgress
                && req.status != Data.LogisticsRequestStatus.Pending)
                continue;

            if (resources != null && resources.Length > 0)
            {
                var rd = req.ResourceDefinition;
                if (rd == null || !resources.Any(r => r == rd))
                    continue;
            }

            req.statusNote = tooltip;
            LogVerbose($"CYCLE plan-failure-note: target={target.ObjectName} rd={req.ResourceDefinition?.ID} note={tooltip}");
        }
    }

    public static int SetRelayCyclePlanFailureNote(CycleMissionsData cmd, string tooltip)
    {
        if (cmd?.A == null || cmd.B == null || string.IsNullOrEmpty(tooltip))
            return 0;

        var resources = cmd.cargoAllStart?.Tab;
        var updated = 0;
        foreach (var requester in Data.LogisticsNetwork.GetAllObjects())
        {
            var data = Data.LogisticsNetwork.Get(requester);
            if (data?.requests == null)
                continue;

            foreach (var req in data.requests)
            {
                if (req == null
                    || req.relayStage != Data.RelayStage.WaitingForSourceOrbitStock
                    || req.relaySourceObjectId != cmd.A.id
                    || req.relayOrbitObjectId != cmd.B.id)
                {
                    continue;
                }

                if (resources != null && resources.Length > 0)
                {
                    var rd = req.ResourceDefinition;
                    if (rd == null || !resources.Any(r => r == rd))
                        continue;
                }

                req.statusNote = tooltip;
                updated++;
                LogVerbose($"CYCLE relay-plan-failure-note: target={requester.ObjectName} staging={cmd.A.ObjectName}->{cmd.B.ObjectName} rd={req.ResourceDefinition?.ID} note={tooltip}");
            }
        }

        return updated;
    }

    public static void SetShipBlockedReason(IEnumerable<ISpacecraftInfo> ships, string reason)
    {
        if (ships == null || string.IsNullOrEmpty(reason)) return;
        foreach (var sci in ships)
        {
            if (sci is Spacecraft sc && sc.ID >= 0
                && _returnHomeByShipId.TryGetValue(sc.ID, out var state) && state != null)
            {
                state.LastBlockedReason = reason;
                state.LastBlockedStatusNote = reason;
                LogVerbose($"SHIP blocked-reason: ship={sc.GetSpacecraftName()} id={sc.ID} reason={reason}");
            }
        }
    }

    public static string BuildLogisticsMissionName(ObjectInfo from, ObjectInfo to, ResourceDefinition rd, bool isReturn = false, ResourceDefinition backhaulRd = null)
    {
        var prefix = isReturn ? "[LOGI-RETURN]" : "[LOGI]";
        var icon = rd?.IconString;
        var iconPart = string.IsNullOrWhiteSpace(icon) ? string.Empty : $" {icon}";
        var backhaulIcon = backhaulRd?.IconString;
        var backhaulPart = isReturn && !string.IsNullOrWhiteSpace(backhaulIcon) ? $"{backhaulIcon}" : string.Empty;
        return $"{prefix}{iconPart}{backhaulPart} {from?.ObjectName ?? "UNKNOWN"} -> {to?.ObjectName ?? "UNKNOWN"}";
    }

    public static bool IsLogisticsPlan(PMMissionParameter pmp)
    {
        return !string.IsNullOrEmpty(FindLogisticsCycleName(pmp));
    }

    public static string FindLogisticsCycleName(PMMissionParameter pmp)
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (pmp == null || player == null || cm == null)
            return null;

        if (pmp.FlyCompany != null && pmp.FlyCompany != player)
            return null;

        if (pmp.SC is Spacecraft pmpSc)
        {
            if (pmpSc.ID >= 0 && _cycleNameByShipId.TryGetValue(pmpSc.ID, out var cachedShipName))
                return cachedShipName;

            var scCmd = cm.GetCycleMission(pmpSc);
            if (scCmd != null && IsLogisticsMission(scCmd) && !string.IsNullOrEmpty(scCmd.customNameFromPlanMission))
            {
                RegisterLogisticsCycleName(scCmd);
                return scCmd.customNameFromPlanMission;
            }
        }

        if (pmp.Start != null && pmp.Target != null)
        {
            var routeKey = MakeCycleRouteKey(pmp.Start, pmp.Target, player);
            if (routeKey != null && _cycleNameByRouteKey.TryGetValue(routeKey, out var cachedRouteName))
                return cachedRouteName;

            var allCycles = cm.GetAllCycleMission(player);
            foreach (var cmd in allCycles)
            {
                if (!IsLogisticsMission(cmd)) continue;
                if (string.IsNullOrEmpty(cmd.customNameFromPlanMission)) continue;

                var sameDirection = cmd.A == pmp.Start && cmd.B == pmp.Target;
                var reverseDirection = cmd.B == pmp.Start && cmd.A == pmp.Target;
                if (sameDirection || reverseDirection)
                {
                    RegisterLogisticsCycleName(cmd);
                    return cmd.customNameFromPlanMission;
                }
            }
        }

        return null;
    }

    public static string FindLogisticsCycleName(ISpacecraftInfo spacecraftInfo, IEnumerable<ISpacecraftInfo> spacecraftInfos)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (cm == null)
            return null;

        if (spacecraftInfo is Spacecraft sc)
        {
            var cmd = cm.GetCycleMission(sc);
            if (cmd != null && IsLogisticsMission(cmd) && !string.IsNullOrEmpty(cmd.customNameFromPlanMission))
            {
                RegisterLogisticsCycleName(cmd);
                return cmd.customNameFromPlanMission;
            }
        }

        if (spacecraftInfos == null)
            return null;

        foreach (var sci in spacecraftInfos)
        {
            if (sci is not Spacecraft listShip)
                continue;

            var cmd = cm.GetCycleMission(listShip);
            if (cmd != null && IsLogisticsMission(cmd) && !string.IsNullOrEmpty(cmd.customNameFromPlanMission))
            {
                RegisterLogisticsCycleName(cmd);
                return cmd.customNameFromPlanMission;
            }
        }

        return null;
    }

    public static string FindLogisticsCycleName(ObjectInfo start, ObjectInfo target, Company company,
        IEnumerable<ISpacecraftInfo> spacecraftInfos, CargoAll cargoAll)
    {
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (start == null || target == null || company == null || cm == null) return null;

        var routeKey = MakeCycleRouteKey(start, target, company);
        if (routeKey != null && _cycleNameByRouteKey.TryGetValue(routeKey, out var cachedRouteName))
            return cachedRouteName;

        var spacecraftSet = new HashSet<ISpacecraftInfo>();
        if (spacecraftInfos != null)
        {
            foreach (var sci in spacecraftInfos)
            {
                if (sci == null) continue;
                spacecraftSet.Add(sci);
                if (sci is Spacecraft sc && sc.ID >= 0 && _cycleNameByShipId.TryGetValue(sc.ID, out var cachedShipName))
                    return cachedShipName;
            }
        }

        foreach (var cmd in cm.GetAllCycleMission(company))
        {
            if (!IsLogisticsMission(cmd)) continue;
            if (string.IsNullOrEmpty(cmd.customNameFromPlanMission)) continue;

            if (spacecraftSet.Count > 0 && cmd.ListSC != null)
            {
                foreach (var sci in cmd.ListSC)
                {
                    if (sci != null && spacecraftSet.Contains(sci))
                    {
                        RegisterLogisticsCycleName(cmd);
                        return cmd.customNameFromPlanMission;
                    }
                }
            }

            var sameDirection = cmd.A == start && cmd.B == target;
            var reverseDirection = cmd.B == start && cmd.A == target;
            if (!sameDirection && !reverseDirection) continue;

            if (cargoAll == null || CargoOverlaps(cmd.cargoAllStart, cargoAll) || CargoOverlaps(cmd.cargoAllEnd, cargoAll))
            {
                RegisterLogisticsCycleName(cmd);
                return cmd.customNameFromPlanMission;
            }
        }

        return null;
    }

    private static bool CargoOverlaps(InfoCargoCyclicalMission cycleCargo, CargoAll missionCargo)
    {
        if (cycleCargo?.Tab == null || missionCargo == null) return false;
        foreach (var rd in cycleCargo.Tab)
        {
            if (rd == null) continue;
            if (CargoContainsResource(missionCargo, rd))
                return true;
        }
        return false;
    }

    public static int GetAwayLogisticsSpacecraftCountForQuota(ObjectInfo quotaHome, LogisticsModSdk.Data.ShipQuotaEntry quota)
    {
        if (quotaHome == null || quota == null || _returnHomeByShipId.Count == 0)
            return 0;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null)
            return 0;

        var count = 0;
        foreach (var sc in GetTrackedReturnShips(player))
        {
            if (sc == null || sc.spacecraftType == null || sc.ID < 0)
                continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state == null)
                continue;
            if (!SameObjectInfo(state.Home, quotaHome))
                continue;
            if (!Data.LogisticsNetwork.QuotaMatches(quota, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC"))
                continue;

            // Planned-but-not-launched ships are still visible at the quota home. This count is
            // specifically for assigned ships the player cannot currently see at that node.
            if (!SameObjectInfo(sc.CurrentlyOnThisObject, quotaHome) || sc.CurrentPhase != Spacecraft.EPhase.None)
                count++;
        }

        return count;
    }

    public static int GetReturnReservedSpacecraftCountAt(ObjectInfo currentLocation, string typeId, string fallbackName)
    {
        if (currentLocation == null || _returnHomeByShipId.Count == 0)
            return 0;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null)
            return 0;

        var count = 0;
        foreach (var sc in GetTrackedReturnShips(player))
        {
            if (sc == null || sc.spacecraftType == null || sc.ID < 0)
                continue;
            if (!SameObjectInfo(sc.CurrentlyOnThisObject, currentLocation))
                continue;
            if (!_returnHomeByShipId.TryGetValue(sc.ID, out var state) || state?.Home == null)
                continue;
            if (SameObjectInfo(state.Home, currentLocation))
                continue;
            if (!SameQuotaKey(typeId, sc.spacecraftType.ID)
                && !SameQuotaKey(typeId, sc.spacecraftType.NameRocketType ?? fallbackName)
                && !SameQuotaKey(fallbackName, sc.spacecraftType.ID)
                && !SameQuotaKey(fallbackName, sc.spacecraftType.NameRocketType ?? fallbackName))
                continue;

            count++;
        }

        return count;
    }

    private static bool SameQuotaKey(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameObjectInfo(ObjectInfo a, ObjectInfo b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.id == b.id;
    }

    private static string FormatReturnShipGroup(int count, string label, List<string> details)
    {
        if (details == null || details.Count == 0)
            return $"{count} ship{(count == 1 ? "" : "s")} {label}";
        if (count == 1)
            return $"{details[0]} {label}";

        var shown = string.Join(", ", details.Take(3));
        var suffix = details.Count > 3 ? $", +{details.Count - 3} more" : "";
        return $"{count} ships {label}: {shown}{suffix}";
    }
}

