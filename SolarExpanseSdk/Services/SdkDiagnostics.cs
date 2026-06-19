using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Diagnostics snapshot service. Snapshots are JSON files containing SDK dispatches,
/// reservations, synthetic carriers, and optional mod-provided state.
/// </summary>
public sealed class SdkDiagnostics
{
    private readonly Dictionary<string, Func<object>> _snapshotProviders = new Dictionary<string, Func<object>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _automaticSnapshotLastWrite = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private SdkLogging _log;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Registers or replaces a named provider included in future diagnostics snapshots.
    /// </summary>
    public void RegisterSnapshotProvider(string name, Func<object> provider)
    {
        if (string.IsNullOrWhiteSpace(name) || provider == null)
            return;

        _snapshotProviders[name] = provider;
        _log?.Verbose("sdk.diagnostics", $"snapshot-provider registered name={name}");
    }

    /// <summary>
    /// Writes a manual diagnostics snapshot and returns the written file path.
    /// </summary>
    public string WriteSnapshot(string reason)
    {
        return WriteSnapshotInternal(reason, automatic: false, key: null);
    }

    /// <summary>
    /// Writes an automatic diagnostics snapshot once per reason/key/cooldown window.
    /// </summary>
    public string WriteSnapshotOnce(string reason, string key = null, double cooldownSeconds = 300)
    {
        var snapshotKey = $"{reason ?? "unknown"}:{key ?? "global"}";
        var now = DateTime.UtcNow;
        if (_automaticSnapshotLastWrite.TryGetValue(snapshotKey, out var last)
            && (now - last).TotalSeconds < cooldownSeconds)
        {
            _log?.Verbose("sdk.diagnostics", $"snapshot skipped reason={reason ?? "unknown"} key={key ?? "global"} cooldownSeconds={cooldownSeconds:0}");
            return null;
        }

        _automaticSnapshotLastWrite[snapshotKey] = now;
        return WriteSnapshotInternal(reason, automatic: true, key: key);
    }

    private string WriteSnapshotInternal(string reason, bool automatic, string key)
    {
        var safeReason = SanitizeFilePart(string.IsNullOrWhiteSpace(reason) ? "manual" : reason);
        var safeKey = SanitizeFilePart(string.IsNullOrWhiteSpace(key) ? null : key);
        var dir = Path.Combine(Application.dataPath, "..", "BepInEx", "SolarExpanseSdk", "Diagnostics");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var fileStem = string.IsNullOrEmpty(safeKey)
            ? $"{stamp}-{safeReason}"
            : $"{stamp}-{safeReason}-{safeKey}";
        var path = GetUniquePath(dir, fileStem);

        var snapshot = new Dictionary<string, object>
        {
            ["createdUtc"] = DateTime.UtcNow,
            ["reason"] = reason,
            ["key"] = key,
            ["automatic"] = automatic,
            ["sdk"] = new
            {
                dispatches = Core.SolarSdk.CyclicalMissions.GetTrackersSnapshot(),
                reservations = Core.SolarSdk.Fleet.GetReservationsSnapshot(),
                syntheticCarriers = Core.SolarSdk.Fleet.GetSyntheticCarrierSnapshot()
            },
            ["providers"] = new Dictionary<string, object>()
        };

        var providers = (Dictionary<string, object>)snapshot["providers"];
        foreach (var pair in _snapshotProviders)
        {
            try
            {
                providers[pair.Key] = pair.Value();
            }
            catch (Exception ex)
            {
                providers[pair.Key] = new { error = ex.ToString() };
                _log?.Warning("sdk.diagnostics", $"snapshot provider failed name={pair.Key} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
        _log?.Info($"SDK diagnostics snapshot written: {path}");
        return path;
    }

    private static string SanitizeFilePart(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '_');
        if (text.Length > 80)
            text = text.Substring(0, 80);
        return text;
    }

    private static string GetUniquePath(string dir, string fileStem)
    {
        var path = Path.Combine(dir, $"{fileStem}.json");
        if (!File.Exists(path))
            return path;

        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(dir, $"{fileStem}-{i:000}.json");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(dir, $"{fileStem}-{Guid.NewGuid():N}.json");
    }
}
