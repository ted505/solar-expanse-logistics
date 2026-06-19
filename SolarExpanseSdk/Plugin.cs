using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SolarExpanseSdk.Core;
using System;
using UnityEngine;

namespace SolarExpanseSdk;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.solarexpanse.sdk";
    public const string PluginName = "Solar Expanse SDK";
    public const string PluginVersion = "0.1.0";

    private static ConfigEntry<bool> _verboseLogging;
    private static ConfigEntry<string> _enabledChannels;
    private static ConfigEntry<bool> _timingLogging;
    private static ConfigEntry<double> _timingThresholdMs;
    private static ConfigEntry<string> _snapshotHotkey;

    private void Awake()
    {
        _verboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enables verbose SDK integration logging. Warnings and errors are always logged.");
        _enabledChannels = Config.Bind("Diagnostics", "EnabledChannels", "*",
            "Comma-separated SDK verbose channels to log when VerboseLogging is enabled. Use * for all.");
        _timingLogging = Config.Bind("Diagnostics", "TimingLogging", false,
            "Enables SDK timing scope logs when VerboseLogging is enabled.");
        _timingThresholdMs = Config.Bind("Diagnostics", "TimingThresholdMs", 5.0,
            "Minimum elapsed milliseconds for SDK timing scope logs.");
        _snapshotHotkey = Config.Bind("Diagnostics", "ManualSnapshotHotkey", "",
            "Optional Unity KeyCode name that writes an SDK diagnostics snapshot when pressed. Leave blank to disable.");

        SolarSdk.Initialize(Logger, Info.Location);
        SolarSdk.Log.Configure(
            () => _verboseLogging?.Value ?? false,
            () => _enabledChannels?.Value ?? "*",
            () => _timingLogging?.Value ?? false,
            () => _timingThresholdMs?.Value ?? 5.0);

        var harmony = new Harmony(PluginGuid);
        SolarSdk.Patches.ApplyAll(harmony, typeof(Plugin).Assembly);
        SolarSdk.Log.Info("SDK loaded.");
        SolarSdk.Patches.LogSummary();
    }

    private void Update()
    {
        var configured = _snapshotHotkey?.Value;
        if (string.IsNullOrWhiteSpace(configured))
            return;

        if (!Enum.TryParse(configured.Trim(), ignoreCase: true, out KeyCode keyCode))
            return;

        if (keyCode != KeyCode.None && Input.GetKeyDown(keyCode))
            SolarSdk.Diagnostics.WriteSnapshot("manual-hotkey");
    }
}
