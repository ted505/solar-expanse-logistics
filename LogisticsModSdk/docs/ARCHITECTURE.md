# Architecture

## Overview

LogisticsMod is a BepInEx 5 / Harmony mod for Solar Expanse. It adds automated resource logistics between planetary bodies and orbits. The mod injects into the stock Object Info window, hooks into the game's daily tick and mission systems, and uses the stock async planner (CodeJobSystem) to create real cyclical missions.

## Layer Diagram

```
  Player UI (LogisticsUI)
       |
       v
  Data Layer (LogisticsNetwork, LogisticsTypes)
       |
       v
  Planner (LogisticsObserver.OnDayChange)
       |
       v
  Harmony Patches --> Stock Game Systems
       |                    |
       v                    v
  CodeJobSystem       CycleMissionManager
  (async planning)    (mission lifecycle)
```

## Data Layer

### LogisticsTypes.cs

Pure data classes, all `[Serializable]` for JSON persistence:

- **LogisticsRequest** -- a GET rule. Fields: resource, target amount, minimum amount, one-shot flag, priority, auto-buy settings, status, relay stage tracking.
- **LogisticsProvider** -- a SEND rule. Fields: resource, minimum reserve, priority, auto-sell settings (mode, price, monthly cap).
- **ShipQuotaEntry** -- spacecraft or LV quota. Fields: type name, count, transfer preference (fastest vs optimal), minimum shipment amount, fuel probe toggle.
- **ProviderSpacecraftSetting** -- per-ship-type overrides scoped to a single SEND provider. Fields: type name, transfer preference, minimum shipment, backhaul, fuel probe. Allows provider-level settings to override quota-level defaults.
- **LogisticsProvider** -- now includes `useSharedSpacecraftPool` (default true), `assignedSpacecraftIds`, and `assignedSpacecraftSettings` for provider-scoped ship assignment.
- **LogisticsObjectData** -- per-body container holding lists of requests, providers, and both quota types.

### LogisticsNetwork.cs

Static in-memory store keyed by `ObjectInfo.id`. Provides CRUD for rules and quotas, ship-type counting, network resource availability queries, and quota matching with case-insensitive migration support. Cleared on save/load boundaries to prevent cross-save contamination.

Ship-assignment management methods: `GetOrCreateProviderSpacecraftSetting`, `FindProviderAssignedToSpacecraft`, `IsSpacecraftAssignedToProvider`, `IsSpacecraftAssignedToOtherProvider`. Ships assigned to a specific provider are excluded from the shared quota pool via `GetReadySpacecraftCountForQuota`.

### LogisticsPersistence.cs

JSON serialization to `BepInEx/saves/<saveName>/LogisticsData.json` using Newtonsoft.Json. Hooks into stock `LoadSaveManager` via `SaveLoadPatches`. Handles stale object cleanup on save and resource definition resolution on load.

## Planner (LogisticsObserver)

The core logic runs on the stock `TimeController.onEachDayChange` event. Each daily tick:

1. **Idle gate** -- skips if no GET requests or auto-sell rules exist.
2. **Snapshot build** -- collects all player spacecraft, active cycles, committed ship IDs, LV counts, and in-flight deliveries into an indexed snapshot for O(1) lookups.
3. **Auto-sell/auto-buy** -- processes market automation rules using stock `Offer.FullFill()`.
4. **Request loop** -- for each GET request on each body:
   - Checks current stock vs target/minimum thresholds.
   - Skips if an active cycle or pending plan already covers the request.
   - Accounts for in-flight deliveries.
   - Calls `TryCreateDeliveries` which:
     - Builds ranked route candidates (local orbit > remote orbit > surface sources).
     - Finds an idle spacecraft matching a quota.
     - Resolves launch vehicle requirements (including self-launch for low-gravity bodies).
     - Handles source-side staging (surface-to-orbit relay via LOC).
     - Creates a stock `CycleMissionsData` with `[LOGI]` naming.
     - Submits the async planning job to the stock CodeJobSystem.
5. **Return handling** -- tracks ships sent away and creates `[LOGI-RETURN]` cycles when they arrive at their destination. Includes cooldowns, escalation, and LV resolution for the return leg.
6. **Cleanup** -- removes completed trajectories, stale cycles, and orphaned planning state.

### Route Selection

Routes are ranked by tier:
1. Same-body orbit (cheapest)
2. Local orbital source
3. Remote orbital source
4. Surface source requiring launch

Within tiers, routes are scored by surplus amount, launch infrastructure quality, and distance.

Route candidate generation now iterates per-provider-rule via `GetMatchingProviderRules`, so provider-level spacecraft settings (transfer preference, backhaul, minimum shipment, fuel probe) can override quota-level defaults on a per-route basis.

### Moon-Case Detection

`IsMoonCaseRoute(a, b)` identifies transfers within a local planet-moon system (planet ↔ moon, moon ↔ sibling moon). Moon-case routes use a slider instead of a porkchop plot, so FAST transfer is forced to Optimal for them. The sibling check requires the shared parent to be a `Planet` or `DwarfPlanet` — two planets orbiting the Sun are not considered a moon case.

### Per-Ship Status Tracking

`ShipState` enum (`Idle`, `InTransit`, `Pending`, `Blocked`) and `QuotaShipStatus` struct provide per-ship status for UI display. Key query methods:
- `BuildShipStatus(sc, home)` — extracts status for a single ship.
- `GetShipStatusesForQuota(...)` — status list for all ships matching a quota entry.
- `GetShipStatusesForAssignedIds(shipIds, home)` — status list for explicitly assigned ship IDs.
- `GetAllShipsForGetRequest(target, rd)` — all inbound ships for a GET request.
- `GetAllShipsForSendProvider(source, rd, provider)` — all outbound ships for a SEND provider, combining assigned and shared-pool ships.
- `SetShipBlockedReason(ships, reason)` — bulk-sets blocked reason, propagated from planner failure callbacks.

### Return Fuel

The mod probes the stock planner asynchronously to estimate return fuel requirements. When the destination lacks sufficient trusted fuel stockpile, return fuel is manifested as cargo on the outbound trip, displacing requested cargo proportionally. A configurable safety multiplier (`ReturnFuelSafetyMultiplier`) scales the reserve. `GetAccessibleFuelStock` accounts for fuel accessibility at the destination when deciding whether to reserve. Per-quota `useFuelProbe` toggle controls whether the async fuel probe is used for that ship type.

### Relay Staging

For surface-to-orbit-to-destination routes, the mod uses a two-phase relay:
1. **Phase 1**: Lift cargo from source surface to source orbit using a launch vehicle or LOC (low-orbit container).
2. **Phase 2**: Ship cargo from source orbit to final destination using a spacecraft.

Relay progress is tracked per-request via `RelayStage` enum. The relay final-leg dispatcher runs in a loop (`while remaining > 0 && stagedStock > 0`), firing multiple final-leg deliveries per tick instead of at most one. Committed stock is cleared per-tick rather than via a wall-clock expiry window.

## Patches

### ObjectInfoWindowPatches

- **Awake**: Injects `LogisticsUI` component onto `ObjectInfoWindow`.
- **SetData**: Syncs logistics UI when the player switches bodies.
- **RebuildLayout**: Triggers logistics layout rebuild.
- **UIRowRocket.SetData**: Appends `[LOGI X reserved]` / `[LOGI X return]` markers to stock spacecraft rows.

### SpaceCraftCyclicalMissionControllerPatches

This file is retained as reference code only. It no longer registers Harmony patch methods. Former responsibilities are SDK-backed and wired from `SdkIntegration.cs`:

- **Code-job setup/callbacks**: Migrated to `SolarSdk.MissionPlanning.BeforeCodeJobPlan`, `BeforeCodeJobCallback`, and `AfterCodeJobCallback`. Logistics handlers still inject cargo capping, name restoration, precalculate caching, fastest-transfer flags, and failure state.
- **Cycle handoff**: `LogisticsObserver.HandOffCycleToStockPlanner` delegates stock controller setup and `TryPlanCycleMission` calling to `SolarSdk.CyclicalMissions.HandOffToStockPlanner`.
- **TryPlanCycleMission replan suppression**: Migrated to `SolarSdk.CyclicalMissions.CheckCycleReplan`.
- **Cycle failure notification translation/suppression**: Migrated to `SolarSdk.CyclicalMissions.CyclePlanNotification`.
- **Fastest route correction**: Migrated to `SolarSdk.MissionPlanning.BeforeFastestSearch` and `ApplyCodeFastestDeltaVCorrection`.
- **CreateFly guards/logging**: Migrated to `SolarSdk.MissionPlanning.BeforeCreateFly` and `AfterCreateFly`.
- **Preview trajectory suppression**: Migrated to `SolarSdk.MissionPlanning.SuppressPreviewTrajectory`.
- **MissionInfo lifecycle**: Migrated to `SolarSdk.MissionTags` and `SolarSdk.MissionPlanning.MissionCompleted`.
- **RemoveMissions safe cleanup**: Migrated to tagged cleanup in `SolarSdk.MissionTags`.
- **Cargo-created diagnostics**: Migrated to `SolarSdk.MissionLoadout.CargoCreatedForCycle`.
- **Market offer notification policy**: Migrated to `SolarSdk.Market.ShouldSuppressOfferNotification`.
- **Self-launch override**: Migrated to `SolarSdk.MissionPlanning.CheckSelfLaunchOverride`.
- **Arrival notification suppression**: Migrated to `SolarSdk.MissionPlanning.SuppressArrivalNotification`.

### SaveLoadPatches

- **Pre-extract**: Clears all runtime state (network, observer, time controller flags).
- **Post-extract**: Loads persisted data, reconciles active LOGI cycles with requests, cleans completed trajectories, sets post-load trigger.
- **Save**: Persists current network state to JSON.

### TimeControllerPatches

- Subscribes `LogisticsObserver.OnDayChange` to the stock daily tick on first `Update`.
- Fires a one-time post-load `OnDayChange(0)` after save restoration completes.

## UI (LogisticsUI)

A `MonoBehaviour` attached to `ObjectInfoWindow`. Uses `SimpleTooltip` for hover tooltips on injected buttons (delegates to stock `ToolTipManager`). Builds four collapsible sections:

1. **GET** -- Import rules. Each row shows resource name (bold white), amount (compact format), mode flags, status, and a color-coded priority badge. Edit and remove buttons on the right. Expandable inbound-ship status panel shows per-ship state via `BuildGetInboundStatusPanel`.
2. **SEND** -- Export rules. Same layout with reserve amount and auto-sell details. Expandable outbound-ship status panel. Provider edit panel includes a "Use shared logistics pool" toggle and an "Assign spacecraft" picker for per-provider ship assignment with per-type transfer/backhaul/fuel-probe overrides.
3. **SPACECRAFT** -- Spacecraft quotas with available/assigned counts, transfer preference, minimum shipment settings, and fuel probe toggle.
4. **LAUNCH VEHICLE** -- LV quotas as toggle-style availability switches.

### UI Components

- **LogisticsSection**: Reusable collapsible panel with stock-matched header styling, arrow icon, and content area with vertical layout.
- **Resource picker**: Shows available resources, network resources, and market-only resources (with `[MARKET]` label). Filters unavailable resources.
- **Amount input**: Numeric entry with increment/decrement buttons, target/minimum toggle, and compact display.

### Styling

- Font sampled from stock `NotificationManager` UI (matching FleetTracker's approach).
- Buttons use plain rectangles (`sprite = null`), explicit `ColorBlock` with `fadeDuration = 0f` to prevent flash on rebuild.
- Color palette: grayed backgrounds, tinted buttons (accent blue, confirm green, remove red), `#A8A8A8` for muted text.
- Number formatting: `FormatCompactAmount` produces `1.2MT`, `5.0KT`, `800T`, `9.5T`.
- Priority badges: color-coded (Low=blue-grey, Normal=grey, High=amber, Critical=red), displayed right-aligned before the Edit button.

## Save Format

```
BepInEx/saves/<saveName>/LogisticsData.json
```

JSON structure:
```json
{
  "objects": [
    {
      "objectId": 123,
      "requests": [...],
      "providers": [...],
      "spacecraftQuota": [...],
      "launchVehicleQuota": [...]
    }
  ]
}
```

Logistics runtime state (return-home tracking, pending plans, route caches) is not persisted -- it is reconstructed from active game state on load via `ReconcileAfterLoad`.
