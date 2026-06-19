using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Info;
using LogisticsModSdk.Logic;
using Manager;
using ScriptableObjectScripts;
using UnityEngine;
using Newtonsoft.Json;

namespace LogisticsModSdk.Data;

public static class LogisticsPersistence
{
    private static string GetPath(string saveName)
    {
        var dir = Path.Combine(Application.dataPath, "..", "BepInEx", "saves", saveName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "LogisticsSdkData.json");
    }

    private static string GetLegacyPath(string saveName)
    {
        var dir = Path.Combine(Application.dataPath, "..", "BepInEx", "saves", saveName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "LogisticsData.json");
    }

    [Serializable]
    private class SaveData
    {
        public List<SavedObject> objects = new List<SavedObject>();
    }

    [Serializable]
    private class SavedObject
    {
        public int objectId;
        public List<SavedRequest> requests = new List<SavedRequest>();
        public List<SavedProvider> providers = new List<SavedProvider>();
        public List<SavedQuota> spacecraftQuota = new List<SavedQuota>();
        public List<SavedQuota> launchVehicleQuota = new List<SavedQuota>();
    }

    [Serializable]
    private class SavedRequest
    {
        public string resourceId;
        public double amount;
        public double minimumAmount;
        public bool useMinimumAmount;
        public bool reorderActive;
        public bool oneShot;
        public double dispatchedAmount;
        public int priority;
        public int networkId;
        public bool autoBuy;
        public double autoBuyMaxPrice;
        public int status;
        public int relayStage;
        public int relaySourceObjectId;
        public int relayOrbitObjectId;
        public int relayFinalTargetObjectId;
        public bool isDirect;
        public int directLinkedObjectId = -1;
    }

    [Serializable]
    private class SavedProvider
    {
        public string resourceId;
        public double minKeep;
        public bool active;
        public int priority;
        public int networkId;
        public bool autoSell;
        public int autoSellMode;
        public double autoSellMaxPerMonth;
        public double autoSellMinPrice;
        public string autoSellMonthKey;
        public double autoSellSoldThisMonth;
        public bool exportToOrbit;
        public double minimumShipmentAmount;
        public double exportOrbitMaxStock;
        public bool? useSharedSpacecraftPool;
        public List<int> assignedSpacecraftIds;
        public List<SavedProviderSpacecraftSetting> assignedSpacecraftSettings;
        public bool isDirect;
        public int directLinkedObjectId = -1;
    }

    [Serializable]
    private class SavedProviderSpacecraftSetting
    {
        public string typeName;
        public bool useFastestTransfer;
        public double minimumShipmentAmount;
        public bool backhaul;
        public bool? useFuelProbe;
    }

    [Serializable]
    private class SavedQuota
    {
        public string typeName;
        public int count;
        public bool useFastestTransfer;
        public double minimumShipmentAmount;
        public bool backhaul;
        public bool? useFuelProbe;
    }

    public static void Save(string saveName)
    {
        try
        {
            var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            var allObjectInfos = objManager?.allObjectInfos;
            if (allObjectInfos != null)
            {
                var deadKeys = LogisticsNetwork.GetAllObjects()
                    .Where(oi => oi == null || !allObjectInfos.Contains(oi))
                    .ToList();
                foreach (var deadOi in deadKeys)
                {
                    LogisticsObserver.Log($"Save: removing stale data for object id={deadOi?.id ?? -1}");
                    LogisticsNetwork.RemoveObject(deadOi);
                }
            }

            var data = new SaveData();

            foreach (var oi in LogisticsNetwork.GetAllObjects())
            {
                var ld = LogisticsNetwork.Get(oi);
                if (ld == null) continue;

                var so = new SavedObject { objectId = oi.id };

                foreach (var r in ld.requests)
                {
                    so.requests.Add(new SavedRequest
                    {
                        resourceId = r.ResourceDefinition?.ID ?? r.resourceDef.id,
                        amount = r.requestedAmount,
                        minimumAmount = r.minimumAmount,
                        useMinimumAmount = r.useMinimumAmount,
                        reorderActive = r.reorderActive,
                        oneShot = r.oneShot,
                        dispatchedAmount = r.dispatchedAmount,
                        priority = r.priority,
                        networkId = r.networkId,
                        autoBuy = r.autoBuy,
                        autoBuyMaxPrice = r.autoBuyMaxPrice,
                        status = (int)r.status,
                        relayStage = (int)r.relayStage,
                        relaySourceObjectId = r.relaySourceObjectId,
                        relayOrbitObjectId = r.relayOrbitObjectId,
                        relayFinalTargetObjectId = r.relayFinalTargetObjectId,
                        isDirect = r.isDirect,
                        directLinkedObjectId = r.directLinkedObjectId
                    });
                }

                foreach (var p in ld.providers)
                {
                    so.providers.Add(new SavedProvider
                    {
                        resourceId = p.ResourceDefinition?.ID ?? p.resourceDef.id,
                        minKeep = p.minimumKeep,
                        active = p.isActive,
                        priority = p.priority,
                        networkId = p.networkId,
                        autoSell = p.autoSell,
                        autoSellMode = (int)p.autoSellMode,
                        autoSellMaxPerMonth = p.autoSellMaxPerMonth,
                        autoSellMinPrice = p.autoSellMinPrice,
                        autoSellMonthKey = p.autoSellMonthKey,
                        autoSellSoldThisMonth = p.autoSellSoldThisMonth,
                        exportToOrbit = p.exportToOrbit,
                        minimumShipmentAmount = p.minimumShipmentAmount,
                        exportOrbitMaxStock = p.exportOrbitMaxStock,
                        useSharedSpacecraftPool = p.useSharedSpacecraftPool,
                        assignedSpacecraftIds = p.assignedSpacecraftIds?.Where(id => id >= 0).Distinct().ToList() ?? new List<int>(),
                        assignedSpacecraftSettings = p.assignedSpacecraftSettings?
                            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.typeName))
                            .Select(s => new SavedProviderSpacecraftSetting
                            {
                                typeName = s.typeName,
                                useFastestTransfer = s.useFastestTransfer,
                                minimumShipmentAmount = s.minimumShipmentAmount,
                                backhaul = s.backhaul,
                                useFuelProbe = s.useFuelProbe
                            })
                            .ToList() ?? new List<SavedProviderSpacecraftSetting>(),
                        isDirect = p.isDirect,
                        directLinkedObjectId = p.directLinkedObjectId
                    });
                }

                foreach (var q in ld.spacecraftQuota)
                    so.spacecraftQuota.Add(new SavedQuota { typeName = q.typeName, count = q.count, useFastestTransfer = q.useFastestTransfer, minimumShipmentAmount = q.minimumShipmentAmount, backhaul = q.backhaul, useFuelProbe = q.useFuelProbe });

                foreach (var q in ld.launchVehicleQuota)
                    so.launchVehicleQuota.Add(new SavedQuota { typeName = q.typeName, count = q.count, useFastestTransfer = q.useFastestTransfer, minimumShipmentAmount = q.minimumShipmentAmount, backhaul = q.backhaul, useFuelProbe = q.useFuelProbe });

                data.objects.Add(so);
            }

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(GetPath(saveName), json);
            LogisticsObserver.Log($"Saved to {saveName}");
        }
        catch (Exception ex)
        {
            LogisticsObserver.LogError($"Save error: {ex}");
        }
    }

    public static void Load(string saveName)
    {
        try
        {
            LogisticsNetwork.ClearAll();

            var path = GetPath(saveName);
            if (!File.Exists(path))
            {
                var legacyPath = GetLegacyPath(saveName);
                if (File.Exists(legacyPath))
                {
                    path = legacyPath;
                    LogisticsObserver.Log($"Importing legacy LogisticsData.json for {saveName}; future saves will use LogisticsSdkData.json");
                }
            }

            if (!File.Exists(path))
            {
                LogisticsObserver.Log($"No save for {saveName}");
                return;
            }

            var json = File.ReadAllText(path);
            var data = JsonConvert.DeserializeObject<SaveData>(json);
            if (data == null || data.objects == null) return;

            var allResources = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllResourceDefinitions;
            var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;

            foreach (var so in data.objects)
            {
                var oi = objManager?.GetByID(so.objectId);
                if (oi == null)
                {
                    LogisticsObserver.LogWarning($"Load: object id={so.objectId} not found, skipping");
                    continue;
                }

                var ld = LogisticsNetwork.GetOrCreate(oi);

                foreach (var sr in so.requests)
                {
                    var rd = allResources?.GetByID(sr.resourceId);
                    ld.requests.Add(new LogisticsRequest
                    {
                        resourceDef = (ResourceDefinitionIDSave)rd,
                        ResourceDefinition = rd,
                        requestedAmount = sr.amount,
                        minimumAmount = sr.minimumAmount,
                        useMinimumAmount = sr.useMinimumAmount,
                        reorderActive = sr.reorderActive,
                        oneShot = sr.oneShot,
                        dispatchedAmount = sr.dispatchedAmount,
                        priority = sr.priority,
                        networkId = LogisticsNetwork.ClampNetworkId(sr.networkId),
                        autoBuy = sr.autoBuy,
                        autoBuyMaxPrice = sr.autoBuyMaxPrice,
                        status = (LogisticsRequestStatus)sr.status,
                        relayStage = (RelayStage)sr.relayStage,
                        relaySourceObjectId = sr.relaySourceObjectId,
                        relayOrbitObjectId = sr.relayOrbitObjectId,
                        relayFinalTargetObjectId = sr.relayFinalTargetObjectId,
                        isDirect = sr.isDirect,
                        directLinkedObjectId = sr.directLinkedObjectId
                    });
                }

                foreach (var sp in so.providers)
                {
                    var rd = allResources?.GetByID(sp.resourceId);
                    ld.providers.Add(new LogisticsProvider
                    {
                        resourceDef = (ResourceDefinitionIDSave)rd,
                        ResourceDefinition = rd,
                        minimumKeep = sp.minKeep,
                        isActive = sp.active,
                        priority = sp.priority,
                        networkId = LogisticsNetwork.ClampNetworkId(sp.networkId),
                        autoSell = sp.autoSell,
                        autoSellMode = (AutoSellMode)sp.autoSellMode,
                        autoSellMaxPerMonth = sp.autoSellMaxPerMonth,
                        autoSellMinPrice = sp.autoSellMinPrice,
                        autoSellMonthKey = sp.autoSellMonthKey,
                        autoSellSoldThisMonth = sp.autoSellSoldThisMonth,
                        exportToOrbit = sp.exportToOrbit,
                        minimumShipmentAmount = sp.minimumShipmentAmount,
                        exportOrbitMaxStock = sp.exportOrbitMaxStock,
                        useSharedSpacecraftPool = sp.useSharedSpacecraftPool ?? true,
                        assignedSpacecraftIds = sp.assignedSpacecraftIds?.Where(id => id >= 0).Distinct().ToList() ?? new List<int>(),
                        assignedSpacecraftSettings = sp.assignedSpacecraftSettings?
                            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.typeName))
                            .Select(s => new ProviderSpacecraftSetting
                            {
                                typeName = s.typeName,
                                useFastestTransfer = s.useFastestTransfer,
                                minimumShipmentAmount = s.minimumShipmentAmount,
                                backhaul = s.backhaul,
                                useFuelProbe = s.useFuelProbe ?? true
                            })
                            .ToList() ?? new List<ProviderSpacecraftSetting>(),
                        isDirect = sp.isDirect,
                        directLinkedObjectId = sp.directLinkedObjectId
                    });
                }

                foreach (var sq in so.spacecraftQuota)
                    ld.spacecraftQuota.Add(new ShipQuotaEntry { typeName = sq.typeName, count = sq.count, useFastestTransfer = sq.useFastestTransfer, minimumShipmentAmount = sq.minimumShipmentAmount, backhaul = sq.backhaul, useFuelProbe = sq.useFuelProbe ?? true });

                foreach (var sq in so.launchVehicleQuota)
                    ld.launchVehicleQuota.Add(new ShipQuotaEntry { typeName = sq.typeName, count = sq.count, useFastestTransfer = sq.useFastestTransfer, minimumShipmentAmount = sq.minimumShipmentAmount, backhaul = sq.backhaul, useFuelProbe = sq.useFuelProbe ?? true });
            }

            LogisticsObserver.Log($"Loaded from {saveName}");
        }
        catch (Exception ex)
        {
            LogisticsObserver.LogError($"Load error: {ex}");
        }
    }
}
