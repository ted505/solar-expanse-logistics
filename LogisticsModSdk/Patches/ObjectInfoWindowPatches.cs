using System;
using CustomUpdate;
using Game;
using Game.Info;
using Game.UI.Windows;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.PlanMissionElements;
using HarmonyLib;
using Game.UI.Windows.Windows;
using LogisticsModSdk.Data;
using LogisticsModSdk.Logic;
using Manager;
using UnityEngine;

namespace LogisticsModSdk.Patches;

[HarmonyPatch]
internal static class ObjectInfoWindowPatches
{
    [HarmonyPatch(typeof(ObjectInfoWindow), "Awake")]
    [HarmonyPostfix]
    private static void AwakePostfix(ObjectInfoWindow __instance)
    {
        if (__instance == null) return;
        if (__instance.GetComponent<UI.LogisticsUI>() != null) return;
        __instance.gameObject.AddComponent<UI.LogisticsUI>();
    }

    [HarmonyPatch(typeof(ObjectInfoWindow), "SetData", new[] { typeof(Game.ObjectInfoDataScripts.ObjectInfoData), typeof(bool) })]
    [HarmonyPostfix]
    private static void SetDataPostfix(ObjectInfoWindow __instance, Game.ObjectInfoDataScripts.ObjectInfoData objectInfoData, bool fromObjectName)
    {
        var oi = objectInfoData?.ObjectInfo;
        if (LogisticsObserver.VerboseLoggingEnabled)
        {
            var nameStr = oi?.ObjectName ?? "NULL";
            var idStr = oi?.id ?? -1;
            LogisticsObserver.LogVerbose($"DIAG SetData: OIW={__instance.GetInstanceID()} obj=\"{nameStr}\" id={idStr} fromObjectName={fromObjectName}");
        }

        var l = __instance.GetComponent<UI.LogisticsUI>();
        if (l != null && l.isActiveAndEnabled)
            l.RefreshData(objectInfoData);
        else if (LogisticsObserver.VerboseLoggingEnabled)
            LogisticsObserver.LogWarning($"DIAG SetData: LogisticsUI null or disabled on OIW={__instance.GetInstanceID()}");
    }

    [HarmonyPatch(typeof(ObjectInfoWindow), "RebuildLayout")]
    [HarmonyPostfix]
    private static void RebuildLayoutPostfix(ObjectInfoWindow __instance)
    {
        var l = __instance.GetComponent<UI.LogisticsUI>();
        if (l != null && l.isActiveAndEnabled)
            l.RebuildLayout();
    }

    [HarmonyPatch(typeof(UIRowRocket), "SetData")]
    [HarmonyPostfix]
    private static void UIRowRocketSetDataPostfix(UIRowRocket __instance)
    {
        try
        {
            var marker = BuildLogisticsReservationMarker(__instance);
            if (string.IsNullOrEmpty(marker) || __instance?.rocketNameTextMeshPro == null)
                return;

            __instance.rocketNameTextMeshPro.text = $"{__instance.rocketNameTextMeshPro.text} {marker}";
        }
        catch (Exception ex)
        {
            if (LogisticsObserver.VerboseLoggingEnabled)
                LogisticsObserver.LogWarning($"LOGI stock-row marker failed: {ex.Message}");
        }
    }

    private static string BuildLogisticsReservationMarker(UIRowRocket row)
    {
        var stack = row?.CurrentStackedRowRocketData;
        var first = stack?[0];
        var firstShip = first?.spacecraft;
        var type = firstShip?.spacecraftType;
        var location = firstShip?.CurrentlyOnThisObject;
        if (type == null || location == null || firstShip.GetCompany() == null)
            return null;

        var quota = LogisticsNetwork.GetQuotaEntry(location, type.ID, true)
            ?? LogisticsNetwork.GetQuotaEntry(location, type.NameRocketType ?? "SC", true);
        var markers = new System.Collections.Generic.List<string>();

        var reservedInRow = new System.Collections.Generic.HashSet<int>();

        if (quota != null && quota.count > 0)
        {
            var awayAssigned = LogisticsObserver.GetAwayLogisticsSpacecraftCountForQuota(location, quota);
            var localReserved = Math.Max(0, quota.count - awayAssigned);
            if (localReserved > 0)
            {
                var presentInRow = GetReadyShipIdsInStack(stack, location, type.ID, type.NameRocketType ?? "SC", excludeProviderAssigned: true);
                for (var i = 0; i < presentInRow.Count && i < localReserved; i++)
                {
                    var shipId = presentInRow[i];
                    reservedInRow.Add(shipId);
                }
            }
        }

        foreach (var shipId in GetProviderAssignedShipIdsInStack(stack, location, type.ID, type.NameRocketType ?? "SC"))
            reservedInRow.Add(shipId);

        if (reservedInRow.Count > 0)
            markers.Add($"<color=#7EC8FF>[LOGI {reservedInRow.Count} reserved]</color>");

        var returnReserved = LogisticsObserver.GetReturnReservedSpacecraftCountAt(location, type.ID, type.NameRocketType ?? "SC");
        if (returnReserved > 0)
            markers.Add($"<color=#FFB86C>[LOGI {returnReserved} return]</color>");

        return markers.Count == 0 ? null : string.Join(" ", markers);
    }

    private static System.Collections.Generic.List<int> GetReadyShipIdsInStack(StackedRowRocketData stack, ObjectInfo location, string typeId, string fallbackName, bool excludeProviderAssigned)
    {
        var result = new System.Collections.Generic.List<int>();
        if (stack == null || location == null)
            return result;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;

        for (var i = 0; i < stack.Count; i++)
        {
            var sc = stack[i]?.spacecraft;
            if (sc?.spacecraftType == null)
                continue;
            if (sc.ID < 0)
                continue;
            if (sc.CurrentlyOnThisObject != location)
                continue;
            if (!LogisticsNetwork.IsSpacecraftReadyForLogistics(sc, player, cm))
                continue;
            if (excludeProviderAssigned && LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID) != null)
                continue;
            if (!LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = typeId }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName)
                && !LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = fallbackName }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName))
                continue;

            result.Add(sc.ID);
        }

        return result;
    }

    private static System.Collections.Generic.List<int> GetProviderAssignedShipIdsInStack(StackedRowRocketData stack, ObjectInfo location, string typeId, string fallbackName)
    {
        var result = new System.Collections.Generic.List<int>();
        if (stack == null || location == null)
            return result;

        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;

        for (var i = 0; i < stack.Count; i++)
        {
            var sc = stack[i]?.spacecraft;
            if (sc?.spacecraftType == null || sc.ID < 0)
                continue;
            if (sc.GetCompany() != player)
                continue;
            if (sc.CurrentlyOnThisObject != location)
                continue;
            if (LogisticsNetwork.FindProviderAssignedToSpacecraft(sc.ID) == null)
                continue;
            if (!LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = typeId }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName)
                && !LogisticsNetwork.QuotaMatches(new ShipQuotaEntry { typeName = fallbackName }, sc.spacecraftType.ID, sc.spacecraftType.NameRocketType ?? fallbackName))
                continue;

            result.Add(sc.ID);
        }

        return result;
    }
}
