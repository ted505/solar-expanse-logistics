# Configuration

All settings are in `BepInEx/plugins/logisticsmodsdk/LogisticsModSdk.cfg`. The file is created on first launch with defaults.

## [ReturnFuel]

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | When enabled, outbound logistics missions stage return fuel at the destination as cargo. Return-fuel cargo may use fuel physically present at the outbound loading point even without a SEND rule. |
| `SafetyMultiplier` | `1.1` | Multiplier on estimated return fuel. Higher values waste more cargo space but reduce the chance of stranding a ship. |
| `ReserveCargoFirst` | `true` | When enabled, return fuel cargo is allocated before requested logistics cargo, guaranteeing the ship can come home even if it means delivering less. |
| `TrustDomesticOnlyAfterStockpile` | `true` | Local fuel production at the destination is only trusted after the estimated reserve stockpile already exists there. Prevents relying on production that hasn't built up yet. |
| `MinimumDomesticReserveDays` | `0` | Reserved for a future production-rate policy. Currently unused; the stockpile check is the only trust gate. |

## [Diagnostics]

| Key | Default | Description |
|-----|---------|-------------|
| `CyclePlanningGraceDays` | `3.0` | In-game days a freshly created LOGI cycle is considered "still being planned" before the cleanup pass treats it as stale. Raise if you see spurious CLEANUP warnings under heavy time acceleration. |
| `BlockedMissionRetryCooldownDays` | `30.0` | In-game days to wait before retrying the same blocked or stale logistics dispatch attempt. |
| `MaxNewDispatchesPerDay` | `3` | Maximum new outbound logistics dispatches created during one daily planner pass. Existing status, stock, in-flight, return, and relay checks still run. |
| `DispatchCreationCooldownMs` | `100.0` | Minimum wall-clock milliseconds between new outbound logistics dispatch creations. This smooths stock mission-planner spikes during high time acceleration without blocking later parallel launches. |
| `VerboseLogging` | `false` | Enables per-request route diagnostics, dispatch traces, naming traces, and stock callback details. Writes to `BepInEx/LogisticsMod_*.log`. |
| `VerboseLogCoalesceSeconds` | `5.0` | Wall-clock window for coalescing repeated high-volume verbose lines such as cooldowns and return-fuel shortfalls. Suppressed counts are appended to the next emitted line. Set to `0` to write every line. |
| `LogFlushIntervalSeconds` | `2.0` | Wall-clock interval for flushing buffered verbose logs to disk. Warnings and errors flush immediately. Lower values are safer for crash forensics; higher values reduce disk I/O. |
| `TimingLogging` | `true` | Enables targeted timing diagnostics for daily planning, snapshot construction, mission setup, and stock callback performance. |
| `TimingLogThresholdMs` | `5.0` | Only timing spans at or above this wall-clock duration (ms) are logged. Lower to see finer-grained timings; raise to reduce log volume. |

## Log Output

Logs are written to a dedicated file, not the main BepInEx log:

```
BepInEx/LogisticsMod_1.log
```

The file rotates on plugin load (session counter in the filename). Timing entries are prefixed with `[TIME]`, verbose entries with the relevant subsystem tag (`LOGI-CODEJOB`, `PLAN`, `NAMING TRACE`, etc.).
