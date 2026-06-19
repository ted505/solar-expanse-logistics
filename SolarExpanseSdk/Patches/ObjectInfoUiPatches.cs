using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using SolarExpanseSdk.Core;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class ObjectInfoUiPatches
{
    [HarmonyPatch(typeof(ObjectInfoWindow), "Awake")]
    [HarmonyPostfix]
    private static void AwakePostfix(ObjectInfoWindow __instance)
    {
        SolarSdk.ObjectInfoUi.AttachRegisteredComponents(__instance);
        SolarSdk.Events.RaiseObjectInfoWindowReady(__instance);
    }

    [HarmonyPatch(typeof(ObjectInfoWindow), "SetData", new[] { typeof(ObjectInfoData), typeof(bool) })]
    [HarmonyPostfix]
    private static void SetDataPostfix(ObjectInfoWindow __instance, ObjectInfoData objectInfoData, bool fromObjectName)
    {
        SolarSdk.Events.RaiseObjectInfoChanged(__instance, objectInfoData, fromObjectName);
    }

    [HarmonyPatch(typeof(ObjectInfoWindow), "RebuildLayout")]
    [HarmonyPostfix]
    private static void RebuildLayoutPostfix(ObjectInfoWindow __instance)
    {
        SolarSdk.Events.RaiseObjectInfoRebuild(__instance);
    }

    [HarmonyPatch(typeof(UIRowRocket), "SetData")]
    [HarmonyPostfix]
    private static void UIRowRocketSetDataPostfix(UIRowRocket __instance)
    {
        var marker = SolarSdk.ObjectInfoUi.BuildRocketRowMarker(__instance);
        if (!string.IsNullOrEmpty(marker) && __instance?.rocketNameTextMeshPro != null)
            __instance.rocketNameTextMeshPro.text = $"{__instance.rocketNameTextMeshPro.text} {marker}";
    }
}
