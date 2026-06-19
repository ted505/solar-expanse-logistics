using System;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;

namespace SolarExpanseSdk.Services;

/// <summary>
/// SDK logging facade with verbose channels, throttled verbose logs, per-mod prefixes,
/// and timing scopes controlled by SDK configuration.
/// </summary>
public sealed class SdkLogging
{
    private readonly object _throttleLock = new object();
    private readonly System.Collections.Generic.Dictionary<string, ThrottleState> _throttles = new System.Collections.Generic.Dictionary<string, ThrottleState>();
    private ManualLogSource _log;
    private Func<bool> _verboseEnabled = () => false;
    private Func<string> _enabledChannels = () => "*";
    private Func<bool> _timingEnabled = () => false;
    private Func<double> _timingThresholdMs = () => 5.0;

    /// <summary>
    /// Connects the SDK facade to the BepInEx log source.
    /// </summary>
    public void Initialize(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// Configures verbose and timing gates from plugin config entries.
    /// </summary>
    public void Configure(Func<bool> verboseEnabled, Func<string> enabledChannels, Func<bool> timingEnabled, Func<double> timingThresholdMs)
    {
        _verboseEnabled = verboseEnabled ?? (() => false);
        _enabledChannels = enabledChannels ?? (() => "*");
        _timingEnabled = timingEnabled ?? (() => false);
        _timingThresholdMs = timingThresholdMs ?? (() => 5.0);
    }

    /// <summary>
    /// Returns a mod-prefixed logger facade for consumer mods.
    /// </summary>
    public ModLogger ForMod(string modId) => new ModLogger(this, modId);

    /// <summary>Writes an info log line.</summary>
    public void Info(string message) => _log?.LogInfo(message);
    /// <summary>Writes a warning log line.</summary>
    public void Warning(string message) => _log?.LogWarning(message);
    /// <summary>Writes an error log line.</summary>
    public void Error(string message) => _log?.LogError(message);

    /// <summary>
    /// Writes a verbose line when SDK verbose logging is enabled for the channel.
    /// </summary>
    public void Verbose(string channel, string message)
    {
        if (!IsVerboseEnabled(channel))
            return;
        _log?.LogInfo($"[{channel}] {message}");
    }

    /// <summary>
    /// Writes a verbose line with rate limiting and emits a suppression summary when the window expires.
    /// </summary>
    public void VerboseThrottled(string channel, string key, string message, double windowSeconds = 2.0)
    {
        if (!IsVerboseEnabled(channel))
            return;

        var throttleKey = $"{channel}:{key ?? message}";
        var now = DateTime.UtcNow;
        lock (_throttleLock)
        {
            if (!_throttles.TryGetValue(throttleKey, out var state))
            {
                _throttles[throttleKey] = new ThrottleState { LastLogUtc = now };
                _log?.LogInfo($"[{channel}] {message}");
                return;
            }

            if ((now - state.LastLogUtc).TotalSeconds < windowSeconds)
            {
                state.Suppressed++;
                return;
            }

            if (state.Suppressed > 0)
            {
                _log?.LogInfo($"[{channel}] suppressed={state.Suppressed} key={key ?? "none"}");
                state.Suppressed = 0;
            }

            state.LastLogUtc = now;
            _log?.LogInfo($"[{channel}] {message}");
        }
    }

    /// <summary>Writes a channel-prefixed warning regardless of verbose settings.</summary>
    public void Warning(string channel, string message) => _log?.LogWarning($"[{channel}] {message}");
    /// <summary>Writes a channel-prefixed error regardless of verbose settings.</summary>
    public void Error(string channel, string message) => _log?.LogError($"[{channel}] {message}");

    /// <summary>
    /// Creates a timing scope that always logs through the base info channel when elapsed time exceeds the threshold.
    /// </summary>
    public IDisposable TimeScope(string label, double thresholdMs = 0) => new TimingScope(this, label, thresholdMs);
    /// <summary>
    /// Creates a timing scope that logs only when timing and verbose logging are enabled for the channel.
    /// </summary>
    public IDisposable TimeScope(string channel, string label, double? thresholdMs = null)
    {
        if (!_timingEnabled() || !IsVerboseEnabled(channel))
            return NullScope.Instance;
        return new TimingScope(this, $"[{channel}] {label}", thresholdMs ?? Math.Max(0, _timingThresholdMs()));
    }

    /// <summary>
    /// Returns true when verbose logging is currently enabled for the supplied channel.
    /// </summary>
    public bool IsVerboseEnabled(string channel)
    {
        if (!_verboseEnabled())
            return false;

        var configured = _enabledChannels() ?? "*";
        if (configured.Trim() == "*")
            return true;

        return configured
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mod-prefixed logger wrapper returned by <see cref="ForMod"/>.
    /// </summary>
    public sealed class ModLogger
    {
        private readonly SdkLogging _owner;
        private readonly string _modId;

        internal ModLogger(SdkLogging owner, string modId)
        {
            _owner = owner;
            _modId = string.IsNullOrWhiteSpace(modId) ? "mod" : modId;
        }

        /// <summary>Writes a mod-prefixed info log line.</summary>
        public void Info(string message) => _owner.Info($"[{_modId}] {message}");
        /// <summary>Writes a mod-prefixed warning log line.</summary>
        public void Warning(string message) => _owner.Warning($"[{_modId}] {message}");
        /// <summary>Writes a mod-prefixed error log line.</summary>
        public void Error(string message) => _owner.Error($"[{_modId}] {message}");
        /// <summary>Writes a mod-prefixed verbose line when the channel is enabled.</summary>
        public void Verbose(string channel, string message) => _owner.Verbose(channel, $"[{_modId}] {message}");
        /// <summary>Writes a mod-prefixed channel warning.</summary>
        public void Warning(string channel, string message) => _owner.Warning(channel, $"[{_modId}] {message}");
        /// <summary>Writes a mod-prefixed channel error.</summary>
        public void Error(string channel, string message) => _owner.Error(channel, $"[{_modId}] {message}");
        /// <summary>Creates a mod-prefixed timing scope on the base info channel.</summary>
        public IDisposable TimeScope(string label, double thresholdMs = 0) => _owner.TimeScope($"[{_modId}] {label}", thresholdMs);
        /// <summary>Creates a mod-prefixed timing scope for a verbose channel.</summary>
        public IDisposable TimeScope(string channel, string label, double? thresholdMs = null) => _owner.TimeScope(channel, $"[{_modId}] {label}", thresholdMs);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();
        public void Dispose()
        {
        }
    }

    private sealed class ThrottleState
    {
        public DateTime LastLogUtc;
        public int Suppressed;
    }

    private sealed class TimingScope : IDisposable
    {
        private readonly SdkLogging _owner;
        private readonly string _label;
        private readonly double _thresholdMs;
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public TimingScope(SdkLogging owner, string label, double thresholdMs)
        {
            _owner = owner;
            _label = label;
            _thresholdMs = thresholdMs;
        }

        public void Dispose()
        {
            _watch.Stop();
            if (_watch.Elapsed.TotalMilliseconds >= _thresholdMs)
                _owner.Info($"{_label}: {_watch.Elapsed.TotalMilliseconds:0.###}ms");
        }
    }
}
