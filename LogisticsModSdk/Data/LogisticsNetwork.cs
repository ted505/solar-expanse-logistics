using System;
using System.Collections.Generic;
using System.Linq;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using LogisticsModSdk.Logic;
using Manager;
using ScriptableObjectScripts;
using UnityEngine;

namespace LogisticsModSdk.Data;

// Persistent rule store for the logistics network. Runtime planning state belongs in
// LogisticsObserver; this class tracks the player-configured GET/SEND/quota data by body.
public static class LogisticsNetwork
{
    private static Dictionary<int, LogisticsObjectData> _dataByObject
        = new Dictionary<int, LogisticsObjectData>();

    public static LogisticsObjectData GetOrCreate(ObjectInfo oi)
    {
        if (oi == null) return null;
        if (!_dataByObject.TryGetValue(oi.id, out var data))
        {
            data = new LogisticsObjectData { ObjectInfo = oi, objectInfoSaveId = oi.id.ToString() };
            _dataByObject[oi.id] = data;
            if (LogisticsObserver.VerboseLoggingEnabled)
                LogisticsObserver.LogVerbose($"NETWORK add object: id={oi.id} name=\"{oi.ObjectName}\"");
        }
        else if (data.ObjectInfo == null)
        {
            data.ObjectInfo = oi;
        }
        return data;
    }

    public static LogisticsObjectData Get(ObjectInfo oi)
    {
        if (oi == null) return null;
        _dataByObject.TryGetValue(oi.id, out var data);
        return data;
    }

    public static LogisticsRequest AddRequest(ObjectInfo oi, ResourceDefinition rd, double amount)
    {
        return AddRequest(oi, rd, amount, amount, false);
    }

    public static LogisticsRequest AddRequest(ObjectInfo oi, ResourceDefinition rd, double targetAmount,
        double minimumAmount, bool useMinimumAmount, bool oneShot = false,
        bool autoBuy = false, double autoBuyMaxPrice = 0, int priority = 0,
        int networkId = 0)
    {
        if (!LogisticsResourceFilter.IsSupported(rd))
            return null;

        var data = GetOrCreate(oi);
        // Minimum is the reorder trigger; requestedAmount remains the fill target.
        minimumAmount = System.Math.Max(0, System.Math.Min(minimumAmount, targetAmount));
        var req = new LogisticsRequest
        {
            resourceDef = rd,
            ResourceDefinition = rd,
            requestedAmount = targetAmount,
            minimumAmount = minimumAmount,
            useMinimumAmount = useMinimumAmount,
            oneShot = oneShot,
            priority = priority,
            autoBuy = autoBuy,
            autoBuyMaxPrice = autoBuyMaxPrice,
            networkId = ClampNetworkId(networkId),
            status = LogisticsRequestStatus.Pending
        };
        data.requests.Add(req);
        if (LogisticsObserver.VerboseLoggingEnabled)
            LogisticsObserver.LogVerbose($"Added request: {rd.ID} target={targetAmount} minimum={(useMinimumAmount ? minimumAmount : targetAmount)} priority={priority} oneShot={oneShot} autoBuy={autoBuy} maxPrice={autoBuyMaxPrice:0.##} net={networkId} on {oi.ObjectName}");
        return req;
    }

    public static LogisticsProvider AddProvider(ObjectInfo oi, ResourceDefinition rd, double minimumKeep,
        bool autoSell = false, AutoSellMode autoSellMode = AutoSellMode.Continuous,
        double autoSellMaxPerMonth = 0, double autoSellMinPrice = 0, int priority = 0,
        bool exportToOrbit = false, double minimumShipmentAmount = 0, double exportOrbitMaxStock = 0,
        int networkId = 0, bool useSharedSpacecraftPool = true, IEnumerable<int> assignedSpacecraftIds = null,
        IEnumerable<ProviderSpacecraftSetting> assignedSpacecraftSettings = null)
    {
        if (!LogisticsResourceFilter.IsSupported(rd))
            return null;

        var data = GetOrCreate(oi);
        var prov = new LogisticsProvider
        {
            resourceDef = rd,
            ResourceDefinition = rd,
            minimumKeep = minimumKeep,
            isActive = true,
            priority = priority,
            autoSell = autoSell,
            autoSellMode = autoSellMode,
            autoSellMaxPerMonth = autoSellMaxPerMonth,
            autoSellMinPrice = autoSellMinPrice,
            exportToOrbit = exportToOrbit,
            networkId = ClampNetworkId(networkId),
            minimumShipmentAmount = Math.Max(0, minimumShipmentAmount),
            exportOrbitMaxStock = Math.Max(0, exportOrbitMaxStock),
            useSharedSpacecraftPool = useSharedSpacecraftPool,
            assignedSpacecraftIds = assignedSpacecraftIds?.Where(id => id >= 0).Distinct().ToList() ?? new List<int>(),
            assignedSpacecraftSettings = assignedSpacecraftSettings?
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.typeName))
                .Select(s => new ProviderSpacecraftSetting
                {
                    typeName = s.typeName,
                    useFastestTransfer = s.useFastestTransfer,
                    minimumShipmentAmount = Math.Max(0, s.minimumShipmentAmount),
                    backhaul = s.backhaul,
                    useFuelProbe = s.useFuelProbe
                })
                .ToList() ?? new List<ProviderSpacecraftSetting>()
        };
        data.providers.Add(prov);
        if (LogisticsObserver.VerboseLoggingEnabled)
            LogisticsObserver.LogVerbose($"Added provider: {rd.ID} min={minimumKeep} priority={priority} autoSell={autoSell} mode={autoSellMode} minPrice={autoSellMinPrice:0.##} maxPerMonth={autoSellMaxPerMonth:0.#} minShipment={minimumShipmentAmount:0.#} exportOrbitMax={exportOrbitMaxStock:0.#} net={networkId} on {oi.ObjectName}");
        return prov;
    }

    public static ProviderSpacecraftSetting GetOrCreateProviderSpacecraftSetting(LogisticsProvider provider, string typeName)
    {
        if (provider == null || string.IsNullOrWhiteSpace(typeName))
            return null;

        if (provider.assignedSpacecraftSettings == null)
            provider.assignedSpacecraftSettings = new List<ProviderSpacecraftSetting>();
        var setting = provider.assignedSpacecraftSettings.FirstOrDefault(s => SameQuotaKey(s.typeName, typeName));
        if (setting == null)
        {
            setting = new ProviderSpacecraftSetting { typeName = typeName, useFuelProbe = true };
            provider.assignedSpacecraftSettings.Add(setting);
        }
        return setting;
    }

    public static ProviderSpacecraftSetting GetProviderSpacecraftSetting(LogisticsProvider provider, Spacecraft sc)
    {
        var type = sc?.spacecraftType;
        if (provider?.assignedSpacecraftSettings == null || type == null)
            return null;

        return provider.assignedSpacecraftSettings.FirstOrDefault(s =>
            SameQuotaKey(s.typeName, TypeKey(type.ID, type.NameRocketType ?? "SC"))
            || SameQuotaKey(s.typeName, type.NameRocketType ?? "SC"));
    }

    public static LogisticsProvider FindProviderAssignedToSpacecraft(int spacecraftId)
    {
        if (spacecraftId < 0) return null;

        foreach (var data in _dataByObject.Values)
        {
            if (data?.providers == null) continue;
            foreach (var provider in data.providers)
            {
                if (IsSpacecraftAssignedToProvider(spacecraftId, provider))
                    return provider;
            }
        }

        return null;
    }

    public static bool IsSpacecraftAssignedToProvider(int spacecraftId, LogisticsProvider provider)
    {
        return spacecraftId >= 0
            && provider?.assignedSpacecraftIds != null
            && provider.assignedSpacecraftIds.Contains(spacecraftId);
    }

    public static bool IsSpacecraftAssignedToOtherProvider(int spacecraftId, LogisticsProvider currentProvider = null)
    {
        if (spacecraftId < 0) return false;

        foreach (var data in _dataByObject.Values)
        {
            if (data?.providers == null) continue;
            foreach (var provider in data.providers)
            {
                if (provider == null || ReferenceEquals(provider, currentProvider)) continue;
                if (IsSpacecraftAssignedToProvider(spacecraftId, provider))
                    return true;
            }
        }

        return false;
    }

    public static void RemoveRequest(ObjectInfo oi, int index)
    {
        var data = Get(oi);
        if (data != null && index >= 0 && index < data.requests.Count)
        {
            var req = data.requests[index];
            if (req.isDirect && req.directLinkedObjectId >= 0 && req.ResourceDefinition != null)
                RemoveLinkedDirectProvider(req.directLinkedObjectId, req.ResourceDefinition, oi?.id ?? -1);
            data.requests.RemoveAt(index);
        }
    }

    public static void RemoveProvider(ObjectInfo oi, int index)
    {
        var data = Get(oi);
        if (data != null && index >= 0 && index < data.providers.Count)
        {
            var prov = data.providers[index];
            if (prov.isDirect && prov.directLinkedObjectId >= 0 && prov.ResourceDefinition != null)
                RemoveLinkedDirectRequest(prov.directLinkedObjectId, prov.ResourceDefinition, oi?.id ?? -1);
            LogisticsObserver.OnProviderRemoved(oi, prov);
            data.providers.RemoveAt(index);
        }
    }

    public static List<ShipQuotaEntry> GetQuotas(ObjectInfo oi, bool isSpacecraft)
    {
        var data = GetOrCreate(oi);
        return isSpacecraft ? data.spacecraftQuota : data.launchVehicleQuota;
    }

    public static int GetQuota(ObjectInfo oi, string typeName, bool isSpacecraft)
    {
        var data = Get(oi);
        if (data == null) return 0;
        var quotas = isSpacecraft ? data.spacecraftQuota : data.launchVehicleQuota;
        var entry = quotas.Find(q => q.typeName == typeName);
        return entry?.count ?? 0;
    }

    public static ShipQuotaEntry GetQuotaEntry(ObjectInfo oi, string typeName, bool isSpacecraft)
    {
        var data = Get(oi);
        if (data == null) return null;
        var quotas = isSpacecraft ? data.spacecraftQuota : data.launchVehicleQuota;
        return quotas.Find(q => q.typeName == typeName);
    }

    public static void SetQuota(ObjectInfo oi, string typeName, int count, bool isSpacecraft)
    {
        var quotas = GetQuotas(oi, isSpacecraft);
        var entry = quotas.Find(q => q.typeName == typeName);
        if (entry != null)
            entry.count = count;
        else if (count > 0)
            quotas.Add(new ShipQuotaEntry { typeName = typeName, count = count });
    }

    public static void SetQuotaMinimumShipment(ObjectInfo oi, string typeName, bool isSpacecraft, double minimumShipmentAmount)
    {
        // Stored on quota entries because the threshold is a per-vehicle-type policy, not
        // a property of a particular request.
        var quotas = GetQuotas(oi, isSpacecraft);
        var entry = quotas.Find(q => q.typeName == typeName);
        if (entry == null)
        {
            entry = new ShipQuotaEntry { typeName = typeName, count = 1 };
            quotas.Add(entry);
        }
        entry.minimumShipmentAmount = System.Math.Max(0, minimumShipmentAmount);
    }

    public static void SetQuotaTransferPreference(ObjectInfo oi, string typeName, bool isSpacecraft, bool useFastestTransfer)
    {
        // Transfer preference is read by the planner when choosing stock Optimal/Fastest.
        // It is only meaningful for spacecraft quotas; LV rows keep the field harmlessly.
        var quotas = GetQuotas(oi, isSpacecraft);
        var entry = quotas.Find(q => q.typeName == typeName);
        if (entry == null)
        {
            entry = new ShipQuotaEntry { typeName = typeName, count = 1 };
            quotas.Add(entry);
        }
        entry.useFastestTransfer = useFastestTransfer;
    }

    public static void SetQuotaBackhaul(ObjectInfo oi, string typeName, bool isSpacecraft, bool backhaul)
    {
        var quotas = GetQuotas(oi, isSpacecraft);
        var entry = quotas.Find(q => q.typeName == typeName);
        if (entry == null)
        {
            entry = new ShipQuotaEntry { typeName = typeName, count = 1 };
            quotas.Add(entry);
        }
        entry.backhaul = backhaul;
    }

    public static void SetQuotaUseFuelProbe(ObjectInfo oi, string typeName, bool isSpacecraft, bool useFuelProbe)
    {
        var quotas = GetQuotas(oi, isSpacecraft);
        var entry = quotas.Find(q => q.typeName == typeName);
        if (entry == null)
        {
            entry = new ShipQuotaEntry { typeName = typeName, count = 1 };
            quotas.Add(entry);
        }
        entry.useFuelProbe = useFuelProbe;
    }

    public static void RemoveQuota(ObjectInfo oi, string typeName, bool isSpacecraft)
    {
        var data = Get(oi);
        if (data == null) return;
        var quotas = isSpacecraft ? data.spacecraftQuota : data.launchVehicleQuota;
        quotas.RemoveAll(q => q.typeName == typeName);
    }

    public static void ClearAll()
    {
        // Save/load boundary: persisted data is reconstructed from the save serializer, so
        // this in-memory table must be cleared to prevent cross-save contamination.
        var count = _dataByObject.Count;
        _dataByObject.Clear();
        if (LogisticsObserver.VerboseLoggingEnabled)
            LogisticsObserver.LogVerbose($"DIAG ClearAll: cleared {count} entries");
    }

    public static void RemoveObject(ObjectInfo oi)
    {
        if (oi != null)
        {
            if (LogisticsObserver.VerboseLoggingEnabled)
                LogisticsObserver.LogVerbose($"DIAG RemoveObject: id={oi.id} name=\"{oi.ObjectName}\"");
            _dataByObject.Remove(oi.id);
        }
    }

    public static List<ObjectInfo> GetAllObjects()
    {
        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        var result = new List<ObjectInfo>();
        foreach (var kv in _dataByObject)
        {
            var oi = kv.Value.ObjectInfo as ObjectInfo;
            if (oi == null && objManager != null)
            {
                oi = objManager.GetByID(kv.Key);
                if (oi != null)
                    kv.Value.ObjectInfo = oi;
                else
                    LogisticsObserver.LogWarning($"DIAG GetAllObjects: id={kv.Key} could NOT resolve via objManager");
            }
            if (oi != null)
                result.Add(oi);
        }
        return result;
    }

    public static bool HasPlannerRules()
    {
        // Used by the daily idle gate. Quota-only objects do not require planner work; GET
        // requests and active Auto-Sell providers do.
        foreach (var kv in _dataByObject)
        {
            var data = kv.Value;
            if (data == null)
                continue;

            if (data.requests != null && data.requests.Count > 0)
                return true;

            if (data.providers == null)
                continue;

            foreach (var provider in data.providers)
            {
                if (provider != null && provider.isActive && (provider.autoSell || provider.exportToOrbit))
                    return true;
            }
        }

        return false;
    }

    public static bool HasMarketAutomationRules()
    {
        foreach (var kv in _dataByObject)
        {
            var data = kv.Value;
            if (data == null)
                continue;

            if (data.requests != null && data.requests.Any(req => req != null && req.autoBuy))
                return true;

            if (data.providers != null
                && data.providers.Any(provider => provider != null && provider.isActive && provider.autoSell))
            {
                return true;
            }
        }

        return false;
    }

    public static HashSet<ResourceDefinition> GetAvailableResourcesOnObject(ObjectInfo oi, Company player)
    {
        var result = new HashSet<ResourceDefinition>();
        if (oi == null || player == null) return result;

        var oid = oi.GetObjectInfoData(player);
        if (oid == null) return result;

        var am = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
        if (am?.AllResourceDefinitions == null) return result;

        foreach (var rd in am.AllResourceDefinitions.ListNotEmpty)
        {
            if (!LogisticsResourceFilter.IsSupported(rd))
                continue;

            if (oid.CheckResources(rd) > 0)
                result.Add(rd);
        }
        return result;
    }

    public static Dictionary<string, int> GetShipTypeCountsOnObject(ObjectInfo oi, bool isSpacecraft)
    {
        // UI-facing count of currently ready vehicles. For spacecraft this intentionally
        // excludes ships in missions/cycles so quota rows can show values like 2/7.
        var result = new Dictionary<string, int>();
        if (oi == null) return result;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return result;

        if (isSpacecraft)
        {
            var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
            foreach (var sc in UnityEngine.Object.FindObjectsOfType<Spacecraft>())
            {
                if (sc == null || sc.spacecraftType == null) continue;
                if (sc.GetCompany() != player) continue;
                if (sc.CurrentlyOnThisObject != oi) continue;
                if (!IsSpacecraftReadyForLogistics(sc, player, cm)) continue;
                if (IsSpacecraftAssignedToOtherProvider(sc.ID)) continue;
                var tn = TypeKey(sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC");
                if (!result.ContainsKey(tn)) result[tn] = 0;
                result[tn]++;
            }
        }
        else
        {
            var rows = oi.GetListLaunchVehicle(player);
            if (rows == null) return result;

            foreach (var row in rows)
            {
                var lv = row?.launchVehicle;
                if (lv == null || lv.launchVehicleType == null) continue;
                if (lv.GetCompany() != player) continue;
                if (lv.objectInfo != oi) continue;
                // Match stock ObjectInfo behavior: reusable LVs remain listed while
                // recovering, so logistics quotas should still be configurable.
                if (!lv.IsReadyToLaunchReusable() && lv.launchVehicleType.reusability <= 0f) continue;
                var tn = TypeKey(lv.launchVehicleType.ID, lv.launchVehicleType.Name ?? "LV");
                if (!result.ContainsKey(tn)) result[tn] = 0;
                result[tn]++;
            }
        }
        return result;
    }

    public static int GetReadySpacecraftCountForQuota(ObjectInfo oi, ShipQuotaEntry quota)
    {
        if (oi == null || quota == null) return 0;
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (player == null) return 0;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        var count = 0;

        foreach (var sc in UnityEngine.Object.FindObjectsOfType<Spacecraft>())
        {
            if (sc == null || sc.spacecraftType == null) continue;
            if (sc.GetCompany() != player) continue;
            if (sc.CurrentlyOnThisObject != oi) continue;
            if (!IsSpacecraftReadyForLogistics(sc, player, cm)) continue;
            if (IsSpacecraftAssignedToOtherProvider(sc.ID)) continue;
            if (!QuotaMatches(quota, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? "SC")) continue;
            count++;
        }

        return count;
    }

    public static bool IsSpacecraftReadyForLogistics(Spacecraft sc, Company player, CycleMissionManager cm)
    {
        // A ship can look idle in planet view while still referenced by a stock cycle.
        // Check both direct controller state and every cycle ListSC by object/id.
        if (sc == null || sc.spacecraftType == null || player == null) return false;
        if (sc.GetCompany() != player) return false;
        if (sc.CurrentPhase != Spacecraft.EPhase.None) return false;

        var directCycle = cm?.GetCycleMission(sc);
        if (directCycle != null && !directCycle.CheckComplete()) return false;

        var controllerCycle = sc.CraftCyclicalMissionController?.CycleMissionsData;
        if (controllerCycle != null && !controllerCycle.CheckComplete()) return false;

        if (cm == null) return true;
        foreach (var cmd in cm.GetAllCycleMission(player))
        {
            if (cmd == null || cmd.CheckComplete() || cmd.ListSC == null)
                continue;

            foreach (var sci in cmd.ListSC)
            {
                if (sci is not Spacecraft other)
                    continue;
                if (ReferenceEquals(sc, other))
                    return false;
                if (sc.ID >= 0 && other.ID >= 0 && sc.ID == other.ID)
                    return false;
            }
        }

        return true;
    }

    public static bool ObjectRequiresLVForLaunch(ObjectInfo oi)
    {
        return oi?.NeedVehicleToLaunch() ?? false;
    }

    public static string TypeKey(string id, string fallbackName)
    {
        return !string.IsNullOrEmpty(id) ? id : fallbackName;
    }

    public static bool QuotaMatches(ShipQuotaEntry quota, string id, string fallbackName)
    {
        // Quotas are persisted by type key, but old saves/UI paths may store display names.
        // Accept both, case-insensitively, for migration resilience.
        if (quota == null) return false;
        var key = TypeKey(id, fallbackName);
        return SameQuotaKey(quota.typeName, key) || SameQuotaKey(quota.typeName, fallbackName);
    }

    public static int ActiveCountFor(Dictionary<string, int> active, string id, string fallbackName)
    {
        var result = 0;
        if (active == null) return 0;
        active.TryGetValue(TypeKey(id, fallbackName), out result);
        if (!string.IsNullOrEmpty(fallbackName) && active.TryGetValue(fallbackName, out var legacy))
            result += legacy;
        return result;
    }

    private static bool SameQuotaKey(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<ResourceDefinition> GetNetworkResourcesSet(Company player)
    {
        return GetNetworkResourcesSet(player, GetAllObjects());
    }

    public static HashSet<ResourceDefinition> GetNetworkResourcesSet(Company player, IEnumerable<ObjectInfo> objects)
    {
        // "Has a provider in network" should mean there is surplus above SEND reserve, not
        // merely that a provider row exists.
        var result = new HashSet<ResourceDefinition>();
        if (player == null) return result;

        foreach (var oi in objects ?? Enumerable.Empty<ObjectInfo>())
        {
            var data = Get(oi);
            if (data == null) continue;

            var oid = oi.GetObjectInfoData(player);
            if (oid == null) continue;

            foreach (var prov in data.providers)
            {
                if (!prov.isActive) continue;
                var rd = prov.ResourceDefinition;
                if (!LogisticsResourceFilter.IsSupported(rd)) continue;

                if (oid.CheckResources(rd) > prov.minimumKeep)
                    result.Add(rd);
            }
        }
        return result;
    }

    // --- Network ID helpers ---------------------------------------------------

    public const int LocalSystemNetworkId = -1;
    public const int MaxNetworkId = 10;

    private static readonly int[] NetworkIdOrder = { 0, LocalSystemNetworkId, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    public static int ClampNetworkId(int id)
    {
        if (id == LocalSystemNetworkId) return LocalSystemNetworkId;
        return Math.Max(0, Math.Min(MaxNetworkId, id));
    }

    public static int StepNetworkId(int current, int direction)
    {
        var idx = Array.IndexOf(NetworkIdOrder, current);
        if (idx < 0) idx = 0;
        idx += direction;
        idx = Math.Max(0, Math.Min(NetworkIdOrder.Length - 1, idx));
        return NetworkIdOrder[idx];
    }

    public static string NetworkLabel(int networkId)
    {
        if (networkId == LocalSystemNetworkId) return "Local System";
        return networkId <= 0 ? "Any" : networkId.ToString();
    }

    /// <summary>
    /// Returns true if a provider's networkId is compatible with a request's networkId.
    /// Network 0 ("Any") matches other Any and numbered networks (1-10).
    /// Network -1 ("Local System") ONLY matches other Local System (spatial check separate).
    /// Non-zero positive networks match the same number or Any (0).
    /// </summary>
    public static bool NetworksMatch(int requestNetworkId, int providerNetworkId)
    {
        // Local System is its own isolated channel — never matches Any or numbered
        if (requestNetworkId == LocalSystemNetworkId || providerNetworkId == LocalSystemNetworkId)
            return requestNetworkId == providerNetworkId;
        // Any matches Any and numbered
        if (requestNetworkId == 0 || providerNetworkId == 0)
            return true;
        return requestNetworkId == providerNetworkId;
    }

    /// <summary>
    /// Full network match including spatial check for Local System.
    /// </summary>
    public static bool NetworksMatchWithLocation(int requestNetworkId, int providerNetworkId,
        ObjectInfo requestBody, ObjectInfo providerBody)
    {
        // Local System only matches Local System, with spatial check
        if (requestNetworkId == LocalSystemNetworkId || providerNetworkId == LocalSystemNetworkId)
        {
            if (requestNetworkId != LocalSystemNetworkId || providerNetworkId != LocalSystemNetworkId)
                return false;
            return AreInSameLocalSystem(requestBody, providerBody);
        }
        // Any matches Any and numbered
        if (requestNetworkId == 0 || providerNetworkId == 0)
            return true;
        return requestNetworkId == providerNetworkId;
    }

    /// <summary>
    /// Returns the local system parent (the planet) for any body.
    /// For a planet, returns itself. For a moon, returns its parent planet.
    /// For an orbit, resolves to the body first. Returns null for stars/belts/solar orbits.
    /// </summary>
    public static ObjectInfo GetLocalSystemParent(ObjectInfo oi)
    {
        if (oi == null) return null;

        // Strip orbit to its body
        var body = oi.objectTypes == global::Data.EObjectTypes.Orbit ? oi.parentObjectInfo : oi;
        if (body == null) return null;

        // Planet or dwarf planet IS the system parent
        if (body.objectTypes == global::Data.EObjectTypes.Planet
            || body.objectTypes == global::Data.EObjectTypes.DwarfPlanet
            || body.objectTypes == global::Data.EObjectTypes.Protoplanet)
            return body;

        // Moon — parent is the planet
        if (body.objectTypes == global::Data.EObjectTypes.Moons && body.parentObjectInfo != null)
            return body.parentObjectInfo;

        // Asteroid/comet with a parent planet
        if ((body.objectTypes == global::Data.EObjectTypes.Asteroid || body.objectTypes == global::Data.EObjectTypes.Comet)
            && body.parentObjectInfo != null)
            return body.parentObjectInfo;

        return null;
    }

    public static bool AreInSameLocalSystem(ObjectInfo a, ObjectInfo b)
    {
        if (a == null || b == null) return false;
        var parentA = GetLocalSystemParent(a);
        var parentB = GetLocalSystemParent(b);
        if (parentA == null || parentB == null) return false;
        return parentA.id == parentB.id;
    }

    /// <summary>
    /// Checks whether a body has at least one active provider for a resource whose
    /// networkId is compatible with the given request networkId.
    /// </summary>
    public static bool HasMatchingNetworkProvider(LogisticsObjectData provData, ResourceDefinition rd,
        int requestNetworkId, ObjectInfo requestBody = null, ObjectInfo providerBody = null)
    {
        if (provData?.providers == null || !LogisticsResourceFilter.IsSupported(rd)) return false;
        foreach (var p in provData.providers)
        {
            if (!p.isActive || p.ResourceDefinition != rd) continue;
            if (requestBody != null && providerBody != null)
            {
                if (NetworksMatchWithLocation(requestNetworkId, p.networkId, requestBody, providerBody))
                    return true;
            }
            else
            {
                if (NetworksMatch(requestNetworkId, p.networkId))
                    return true;
            }
        }
        return false;
    }

    // --- Direct route helpers ------------------------------------------------

    public static LogisticsRequest FindLinkedDirectRequest(int objectId, ResourceDefinition rd, int linkedFromObjectId)
    {
        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        var oi = objManager?.GetByID(objectId);
        if (oi == null || rd == null) return null;
        var data = Get(oi);
        if (data?.requests == null) return null;
        return data.requests.FirstOrDefault(r => r != null
            && r.isDirect
            && r.directLinkedObjectId == linkedFromObjectId
            && r.ResourceDefinition == rd);
    }

    public static LogisticsProvider FindLinkedDirectProvider(int objectId, ResourceDefinition rd, int linkedFromObjectId)
    {
        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        var oi = objManager?.GetByID(objectId);
        if (oi == null || rd == null) return null;
        var data = Get(oi);
        if (data?.providers == null) return null;
        return data.providers.FirstOrDefault(p => p != null
            && p.isDirect
            && p.directLinkedObjectId == linkedFromObjectId
            && p.ResourceDefinition == rd);
    }

    public static void RemoveLinkedDirectRequest(int objectId, ResourceDefinition rd, int linkedFromObjectId)
    {
        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        var oi = objManager?.GetByID(objectId);
        if (oi == null || rd == null) return;
        var data = Get(oi);
        if (data?.requests == null) return;
        data.requests.RemoveAll(r => r != null
            && r.isDirect
            && r.directLinkedObjectId == linkedFromObjectId
            && r.ResourceDefinition == rd);
    }

    public static void RemoveLinkedDirectProvider(int objectId, ResourceDefinition rd, int linkedFromObjectId)
    {
        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        var oi = objManager?.GetByID(objectId);
        if (oi == null || rd == null) return;
        var data = Get(oi);
        if (data?.providers == null) return;
        foreach (var provider in data.providers
            .Where(p => p != null
                && p.isDirect
                && p.directLinkedObjectId == linkedFromObjectId
                && p.ResourceDefinition == rd)
            .ToList())
        {
            LogisticsObserver.OnProviderRemoved(oi, provider);
            data.providers.Remove(provider);
        }
    }

    public static ObjectInfo ResolveObjectById(int objectId)
    {
        if (objectId < 0) return null;
        return MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(objectId);
    }
}
