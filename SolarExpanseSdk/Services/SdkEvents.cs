using System;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Windows;

namespace SolarExpanseSdk.Services;

/// <summary>
/// SDK event bus raised by Harmony patches at stable game-integration points.
/// Handler exceptions are logged and converted into rate-limited diagnostics snapshots.
/// </summary>
public sealed class SdkEvents
{
    /// <summary>Raised before stock save extraction begins.</summary>
    public event Action SaveLoading;
    /// <summary>Raised after a save has loaded. Argument is the save name when known.</summary>
    public event Action<string> SaveLoaded;
    /// <summary>Raised before stock save writes. Argument is the save name when known.</summary>
    public event Action<string> BeforeSave;
    /// <summary>Raised after stock save writes. Argument is the save name when known.</summary>
    public event Action<string> AfterSave;
    /// <summary>Raised once per stock day tick with elapsed days.</summary>
    public event Action<double> DayTick;
    /// <summary>Raised on the first day tick after a save load completes.</summary>
    public event Action PostLoadFirstTick;
    /// <summary>Raised when an object-info window is ready for SDK components.</summary>
    public event Action<ObjectInfoWindow> ObjectInfoWindowReady;
    /// <summary>Raised when an object-info window changes displayed object data.</summary>
    public event Action<ObjectInfoWindow, ObjectInfoData, bool> ObjectInfoChanged;
    /// <summary>Raised when an object-info window rebuilds its layout.</summary>
    public event Action<ObjectInfoWindow> ObjectInfoRebuild;

    private bool _postLoadFirstTickPending;

    /// <summary>Raises <see cref="SaveLoading"/> and resets post-load-first-tick state.</summary>
    public void RaiseSaveLoading()
    {
        _postLoadFirstTickPending = false;
        SafeInvoke("SaveLoading", "save=loading", SaveLoading);
    }

    /// <summary>Raises <see cref="SaveLoaded"/> and schedules <see cref="PostLoadFirstTick"/>.</summary>
    public void RaiseSaveLoaded(string saveName)
    {
        _postLoadFirstTickPending = true;
        SafeInvoke("SaveLoaded", $"save={saveName ?? "null"}", SaveLoaded, saveName);
    }

    /// <summary>Raises <see cref="BeforeSave"/>.</summary>
    public void RaiseBeforeSave(string saveName) => SafeInvoke("BeforeSave", $"save={saveName ?? "null"}", BeforeSave, saveName);
    /// <summary>Raises <see cref="AfterSave"/>.</summary>
    public void RaiseAfterSave(string saveName) => SafeInvoke("AfterSave", $"save={saveName ?? "null"}", AfterSave, saveName);

    /// <summary>Raises <see cref="DayTick"/> and, when pending, <see cref="PostLoadFirstTick"/>.</summary>
    public void RaiseDayTick(double days)
    {
        SafeInvoke("DayTick", $"days={days:0.###}", DayTick, days);
        if (!_postLoadFirstTickPending)
            return;

        _postLoadFirstTickPending = false;
        SafeInvoke("PostLoadFirstTick", "post-load=true", PostLoadFirstTick);
    }

    /// <summary>Raises <see cref="ObjectInfoWindowReady"/>.</summary>
    public void RaiseObjectInfoWindowReady(ObjectInfoWindow window) => SafeInvoke("ObjectInfoWindowReady", $"window={window?.GetInstanceID() ?? -1}", ObjectInfoWindowReady, window);
    /// <summary>Raises <see cref="ObjectInfoChanged"/>.</summary>
    public void RaiseObjectInfoChanged(ObjectInfoWindow window, ObjectInfoData data, bool fromObjectName) => SafeInvoke("ObjectInfoChanged", $"window={window?.GetInstanceID() ?? -1} object={data?.ObjectInfo?.ObjectName ?? "null"} id={data?.ObjectInfo?.id ?? -1} fromObjectName={fromObjectName}", ObjectInfoChanged, window, data, fromObjectName);
    /// <summary>Raises <see cref="ObjectInfoRebuild"/>.</summary>
    public void RaiseObjectInfoRebuild(ObjectInfoWindow window) => SafeInvoke("ObjectInfoRebuild", $"window={window?.GetInstanceID() ?? -1}", ObjectInfoRebuild, window);

    private static void SafeInvoke(string eventName, string context, Action action)
    {
        Core.SolarSdk.Log.VerboseThrottled("sdk.events", eventName, $"dispatch name={eventName} {context} subscribers={action?.GetInvocationList().Length ?? 0}");
        if (action == null)
            return;

        foreach (Action handler in action.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Core.SolarSdk.Log.Error("sdk.events", $"handler failed name={eventName} error={ex}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("event-handler-error", eventName);
            }
        }
    }

    private static void SafeInvoke<T>(string eventName, string context, Action<T> action, T arg)
    {
        Core.SolarSdk.Log.VerboseThrottled("sdk.events", eventName, $"dispatch name={eventName} {context} subscribers={action?.GetInvocationList().Length ?? 0}");
        if (action == null)
            return;

        foreach (Action<T> handler in action.GetInvocationList())
        {
            try
            {
                handler(arg);
            }
            catch (Exception ex)
            {
                Core.SolarSdk.Log.Error("sdk.events", $"handler failed name={eventName} error={ex}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("event-handler-error", eventName);
            }
        }
    }

    private static void SafeInvoke<T1, T2, T3>(string eventName, string context, Action<T1, T2, T3> action, T1 arg1, T2 arg2, T3 arg3)
    {
        Core.SolarSdk.Log.VerboseThrottled("sdk.events", eventName, $"dispatch name={eventName} {context} subscribers={action?.GetInvocationList().Length ?? 0}");
        if (action == null)
            return;

        foreach (Action<T1, T2, T3> handler in action.GetInvocationList())
        {
            try
            {
                handler(arg1, arg2, arg3);
            }
            catch (Exception ex)
            {
                Core.SolarSdk.Log.Error("sdk.events", $"handler failed name={eventName} error={ex}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("event-handler-error", eventName);
            }
        }
    }
}
