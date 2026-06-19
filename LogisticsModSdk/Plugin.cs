using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using LogisticsModSdk.Logic;
using SolarExpanseSdk.Core;
using System.IO;

namespace LogisticsModSdk;

[BepInPlugin(PluginGuid, "Logistics Tab SDK", "0.1.0")]
[BepInDependency(SolarExpanseSdk.Plugin.PluginGuid)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.logisticsmod.sdk";

    public static Plugin Instance { get; private set; }
    public static ConfigEntry<bool> ReturnFuelEnabled { get; private set; }
    public static ConfigEntry<double> ReturnFuelSafetyMultiplier { get; private set; }
    public static ConfigEntry<bool> ReturnFuelReserveCargoFirst { get; private set; }
    public static ConfigEntry<bool> ReturnFuelTrustDomesticOnlyAfterStockpile { get; private set; }
    public static ConfigEntry<int> ReturnFuelMinimumDomesticReserveDays { get; private set; }
    public static ConfigEntry<double> CyclePlanningGraceDays { get; private set; }
    public static ConfigEntry<double> BlockedMissionRetryCooldownDays { get; private set; }
    public static ConfigEntry<bool> VerboseLogging { get; private set; }
    public static ConfigEntry<double> VerboseLogCoalesceSeconds { get; private set; }
    public static ConfigEntry<double> LogFlushIntervalSeconds { get; private set; }
    public static ConfigEntry<bool> TimingLogging { get; private set; }
    public static ConfigEntry<double> TimingLogThresholdMs { get; private set; }
    private static ConfigFile _pluginConfig;

    private void Awake()
    {
        if (Chainloader.PluginInfos.ContainsKey("com.logisticsmod"))
        {
            Logger.LogWarning("The original LogisticsMod plugin is loaded; disabling LogisticsModSdk to prevent duplicate dispatch.");
            enabled = false;
            return;
        }

        Instance = this;
        var pluginConfigPath = Path.Combine(Paths.PluginPath, "logisticsmodsdk", "LogisticsModSdk.cfg");
        _pluginConfig = new ConfigFile(pluginConfigPath, saveOnInit: true);
        // Keep player-tunable safety/performance controls in the plugin folder instead of
        // BepInEx's shared config root so release zips can include sane defaults.
        ReturnFuelEnabled = _pluginConfig.Bind("ReturnFuel", "Enabled", true,
            "When enabled, logistics missions try to stage enough fuel at the destination for the logistics vessel to return.");
        ReturnFuelSafetyMultiplier = _pluginConfig.Bind("ReturnFuel", "SafetyMultiplier", 1.1,
            "Multiplier applied to the estimated return fuel reserve.");
        ReturnFuelReserveCargoFirst = _pluginConfig.Bind("ReturnFuel", "ReserveCargoFirst", true,
            "When enabled, return-fuel reserve cargo is prioritized over the requested logistics cargo.");
        ReturnFuelTrustDomesticOnlyAfterStockpile = _pluginConfig.Bind("ReturnFuel", "TrustDomesticOnlyAfterStockpile", true,
            "When enabled, local/domestic fuel production is trusted only after the destination already has the estimated reserve stockpile.");
        ReturnFuelMinimumDomesticReserveDays = _pluginConfig.Bind("ReturnFuel", "MinimumDomesticReserveDays", 0,
            "Reserved for a later production-rate policy. The current first pass uses stockpile only.");
        CyclePlanningGraceDays = _pluginConfig.Bind("Diagnostics", "CyclePlanningGraceDays", 3.0,
            "In-game days a freshly created LOGI cycle is considered 'still being planned' before being treated as stale. The async code job system normally fires inside this window; raise if you see spurious CLEANUP warnings under heavy time acceleration.");
        BlockedMissionRetryCooldownDays = _pluginConfig.Bind("Diagnostics", "BlockedMissionRetryCooldownDays", 30.0,
            "In-game days to wait before retrying the same blocked or stale logistics dispatch attempt.");
        VerboseLogging = _pluginConfig.Bind("Diagnostics", "VerboseLogging", false,
            "When enabled, per-request route and dispatch diagnostics are written to BepInEx/LogisticsMod_*.log.");
        VerboseLogCoalesceSeconds = _pluginConfig.Bind("Diagnostics", "VerboseLogCoalesceSeconds", 5.0,
            "Wall-clock seconds to coalesce identical high-volume verbose diagnostics. Set to 0 to log every line.");
        LogFlushIntervalSeconds = _pluginConfig.Bind("Diagnostics", "LogFlushIntervalSeconds", 2.0,
            "Wall-clock seconds between buffered verbose log flushes. Warnings/errors still flush immediately.");
        TimingLogging = _pluginConfig.Bind("Diagnostics", "TimingLogging", true,
            "When enabled, targeted logistics timing diagnostics are written to BepInEx/LogisticsMod_*.log.");
        TimingLogThresholdMs = _pluginConfig.Bind("Diagnostics", "TimingLogThresholdMs", 5.0,
            "Only timing spans at or above this duration are logged.");
        _pluginConfig.Save();

        SdkIntegration.Register();
        Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);
        LogisticsObserver.Log($"Plugin loaded! build=sdk-0.1.0 source=Documents/SolarExpanseMods/LogisticsModSdk config={pluginConfigPath} returnFuel={ReturnFuelEnabled.Value} margin={ReturnFuelSafetyMultiplier.Value:0.##} sdkLifecycle={SolarSdk.Patches.LifecycleAvailable}");
    }
}
