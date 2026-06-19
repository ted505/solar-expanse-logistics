using BepInEx.Logging;
using SolarExpanseSdk.Services;

namespace SolarExpanseSdk.Core;

/// <summary>
/// Static entry point for all Solar Expanse SDK services. Consumer mods should access SDK
/// functionality through this class after depending on the SDK plugin.
/// </summary>
public static class SolarSdk
{
    /// <summary>Lifecycle, save, day-tick, and object-info events raised by SDK patches.</summary>
    public static SdkEvents Events { get; } = new SdkEvents();
    /// <summary>SDK logging and verbose/timing channel helpers.</summary>
    public static SdkLogging Log { get; } = new SdkLogging();
    /// <summary>Patch application status and capability diagnostics.</summary>
    public static SdkPatches Patches { get; } = new SdkPatches();
    /// <summary>Per-save JSON storage helpers for consumer mods.</summary>
    public static SdkSaveStore SaveStore { get; } = new SdkSaveStore();
    /// <summary>Object-info window component and rocket-row decorator extension points.</summary>
    public static SdkObjectInfoUi ObjectInfoUi { get; } = new SdkObjectInfoUi();
    /// <summary>Fleet query helpers, real reservations, and synthetic carrier tracking.</summary>
    public static SdkFleet Fleet { get; } = new SdkFleet();
    /// <summary>Cyclical mission dispatch correlation and diagnostics.</summary>
    public static SdkCyclicalMissions CyclicalMissions { get; } = new SdkCyclicalMissions();
    /// <summary>Mission drafts, stock parameter conversion, and validation helpers.</summary>
    public static SdkMissions Missions { get; } = new SdkMissions();
    /// <summary>Cargo, fuel, supply, resource, and payload helpers for mission loadouts.</summary>
    public static SdkMissionLoadout MissionLoadout { get; } = new SdkMissionLoadout();
    /// <summary>Low-level mission planner patch events and overrides.</summary>
    public static SdkMissionPlanning MissionPlanning { get; } = new SdkMissionPlanning();
    /// <summary>Mission tag and display-name preservation helpers.</summary>
    public static SdkMissionTags MissionTags { get; } = new SdkMissionTags();
    /// <summary>Market offer hooks and notification policy helpers.</summary>
    public static SdkMarket Market { get; } = new SdkMarket();
    /// <summary>Diagnostics snapshot registration and writing.</summary>
    public static SdkDiagnostics Diagnostics { get; } = new SdkDiagnostics();

    /// <summary>True after the SDK services have been initialized by the plugin.</summary>
    public static bool IsInitialized { get; private set; }
    /// <summary>SDK plugin folder path supplied by BepInEx.</summary>
    public static string PluginLocation { get; private set; }

    /// <summary>
    /// Initializes all SDK services. This is called by the SDK plugin and is idempotent.
    /// </summary>
    public static void Initialize(ManualLogSource logger, string pluginLocation)
    {
        if (IsInitialized)
            return;

        PluginLocation = pluginLocation;
        Log.Initialize(logger);
        SaveStore.Initialize(Log);
        Patches.Initialize(Log);
        ObjectInfoUi.Initialize(Log);
        Fleet.Initialize(Log);
        CyclicalMissions.Initialize(Log);
        Missions.Initialize(Log);
        MissionLoadout.Initialize(Log);
        MissionPlanning.Initialize(Log);
        MissionTags.Initialize(Log);
        Market.Initialize(Log);
        Diagnostics.Initialize(Log);
        IsInitialized = true;
    }
}
