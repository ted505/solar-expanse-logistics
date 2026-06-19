using System;
using System.Collections.Generic;
using Game.Info;
using ScriptableObjectScripts;
using UnityEngine;

namespace LogisticsModSdk.Data;

[Serializable]
public class LogisticsRequest
{
    public ResourceDefinitionIDSave resourceDef;
    public double requestedAmount;
    public double minimumAmount;
    public bool useMinimumAmount;
    public bool reorderActive;
    public bool oneShot;
    public double dispatchedAmount;
    public int priority;
    public bool autoBuy;
    public double autoBuyMaxPrice;
    public int networkId;
    public LogisticsRequestStatus status;
    public RelayStage relayStage;
    public int relaySourceObjectId = -1;
    public int relayOrbitObjectId = -1;
    public int relayFinalTargetObjectId = -1;
    public bool isDirect;
    public int directLinkedObjectId = -1;

    [NonSerialized]
    public ResourceDefinition ResourceDefinition;

    [NonSerialized]
    public string statusNote;
}

public enum RelayStage
{
    None,
    WaitingForSourceOrbitStock,
    WaitingForFinalLeg
}

public enum LogisticsRequestStatus
{
    Pending,
    InProgress,
    Satisfied,
    Failed
}

[Serializable]
public class LogisticsProvider
{
    public ResourceDefinitionIDSave resourceDef;
    public double minimumKeep;
    public bool isActive;
    public int priority;
    public bool autoSell;
    public AutoSellMode autoSellMode;
    public double autoSellMaxPerMonth;
    public double autoSellMinPrice;
    public string autoSellMonthKey;
    public double autoSellSoldThisMonth;
    public int networkId;
    public bool exportToOrbit;
    public double minimumShipmentAmount;
    public double exportOrbitMaxStock;
    public bool useSharedSpacecraftPool = true;
    public List<int> assignedSpacecraftIds = new List<int>();
    public List<ProviderSpacecraftSetting> assignedSpacecraftSettings = new List<ProviderSpacecraftSetting>();
    public bool isDirect;
    public int directLinkedObjectId = -1;

    [NonSerialized]
    public ResourceDefinition ResourceDefinition;
}

[Serializable]
public class ProviderSpacecraftSetting
{
    public string typeName;
    public bool useFastestTransfer;
    public double minimumShipmentAmount;
    public bool backhaul;
    public bool useFuelProbe = true;
}

public enum AutoSellMode
{
    Continuous,
    PerMonth
}

[Serializable]
public class ShipQuotaEntry
{
    public string typeName;
    public int count;
    public bool useFastestTransfer;
    public double minimumShipmentAmount;
    public bool backhaul;
    public bool useFuelProbe = true;
}

[Serializable]
public class LogisticsObjectData
{
    public string objectInfoSaveId;
    public List<LogisticsRequest> requests = new List<LogisticsRequest>();
    public List<LogisticsProvider> providers = new List<LogisticsProvider>();
    public List<ShipQuotaEntry> spacecraftQuota = new List<ShipQuotaEntry>();
    public List<ShipQuotaEntry> launchVehicleQuota = new List<ShipQuotaEntry>();

    [NonSerialized]
    public object ObjectInfo;
}
