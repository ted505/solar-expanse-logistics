using HarmonyLib;
using Manager;

namespace LogisticsModSdk.Patches;

[HarmonyPatch(typeof(TimeController), "Update")]
internal static class TimeControllerPatches
{
    private static bool _subscribed;
    private static bool _postLoadFired;

    public static void ResetRuntimeFlags()
    {
        _subscribed = false;
        _postLoadFired = false;
    }

    [HarmonyPrefix]
    private static void Prefix(TimeController __instance)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            __instance.onEachDayChange += Logic.LogisticsObserver.OnDayChange;
        }

        if (SaveLoadPatches.PendingPostLoadTrigger && !_postLoadFired)
        {
            _postLoadFired = true;
            SaveLoadPatches.PendingPostLoadTrigger = false;
            Logic.LogisticsObserver.OnDayChange(0);
        }
    }
}
