using Game;
using HarmonyLib;
using Manager;
using SolarExpanseSdk.Core;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class LifecyclePatches
{
    private static bool _subscribedToDayTick;

    [HarmonyPatch(typeof(LoadSaveManager), "ExtractAllFromSaveData")]
    [HarmonyPrefix]
    private static void ExtractAllPrefix()
    {
        _subscribedToDayTick = false;
        SolarSdk.Events.RaiseSaveLoading();
    }

    [HarmonyPatch(typeof(LoadSaveManager), "ExtractAllFromSaveData")]
    [HarmonyPostfix]
    private static void ExtractAllPostfix()
    {
        var saveName = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance?.LastSaveName;
        SolarSdk.Events.RaiseSaveLoaded(saveName);
    }

    [HarmonyPatch(typeof(LoadSaveManager), "SaveToFile", new[] { typeof(string) })]
    [HarmonyPrefix]
    private static void SaveToFilePrefix(string saveName)
    {
        SolarSdk.Events.RaiseBeforeSave(saveName);
    }

    [HarmonyPatch(typeof(LoadSaveManager), "SaveToFile", new[] { typeof(string) })]
    [HarmonyPostfix]
    private static void SaveToFilePostfix(string saveName)
    {
        SolarSdk.Events.RaiseAfterSave(saveName);
    }

    [HarmonyPatch(typeof(TimeController), "Update")]
    [HarmonyPrefix]
    private static void TimeControllerUpdatePrefix(TimeController __instance)
    {
        if (__instance == null || _subscribedToDayTick)
            return;

        _subscribedToDayTick = true;
        __instance.onEachDayChange += SolarSdk.Events.RaiseDayTick;
    }
}
