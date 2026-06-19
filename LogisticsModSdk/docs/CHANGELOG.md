# Changelog

## 2026-06-19 -- LV Staging Hot-Path Optimization

- Added a per-planner-snapshot staged-route support cache for surface-to-orbit LV staging.
- Staged route candidate generation now reuses cached LV/container/final-carrier resolution and invalidates that cache after each recorded dispatch.
- Staged LV candidates now cap source-surface lift by actual launch-support payload capacity, matching export-to-orbit behavior.
- Added targeted `LV-STAGE` timing diagnostics around staged support resolution and final-carrier lookup.
- SDK cycle handoff now treats stock LOC "waiting for existing container mission" returns as deferred rather than failed, preventing duplicate surface-to-orbit staging retries.
- Stock spacecraft row markers now distinguish shared logistics pool ships from provider-assigned ships instead of labeling both as reserved.

---

## 2026-06-09 -- Moon-Case FAST Transfer Fix

- Fixed `IsMoonCaseRoute` sibling check matching interplanetary routes (Earth → Mars) as moon-case. The shared-parent check now requires the parent to be a `Planet` or `DwarfPlanet`, not a star. This was forcing all interplanetary FAST transfers to Optimal.

---

## 2026-06-01 -- Ship Assignment, Per-Provider Overrides, Ship Status, Relay Loop

Progression across 6/1 backups (06-01 → 06-01b → 06-01d → 06-01e → 06-01f):

### Relay Final-Leg Loop (06-01 → 06-01b)

- Relay final-leg dispatcher changed from single-dispatch-per-tick to a `while (remaining > 0 && stagedStock > 0)` loop, firing multiple final-leg deliveries per tick.
- Committed-stock lifetime changed from wall-clock expiry window to per-tick clear at the start of each `OnDayChange`.
- Removed serialized-wait guard that blocked final-leg when `stagedStock < usefulFinalLoad`.

### Per-Provider Spacecraft Settings (06-01d)

- Route candidate generation now iterates per-provider-rule via `GetMatchingProviderRules`.
- `AddDirectRouteCandidates`, `GetTransferTypeForSpacecraft`, `ShouldReserveReturnFuel`, `UseFuelProbeForSpacecraft`, `MeetsMinimumShipment`, `GetMinimumShipmentForSpacecraft` all gained a `providerRule` parameter so per-provider settings override quota-level defaults.
- New `ResolveRelayProviderRule` threads provider-rule context into relay dispatch.

### Ship Assignment Data (06-01e)

- `LogisticsProvider` gained `useSharedSpacecraftPool`, `assignedSpacecraftIds`, `assignedSpacecraftSettings` fields.
- New `ProviderSpacecraftSetting` class for per-ship-type overrides scoped to a provider.
- `ShipQuotaEntry` gained `useFuelProbe` field.
- `LogisticsNetwork` gained ship-assignment management methods: `FindProviderAssignedToSpacecraft`, `IsSpacecraftAssignedToProvider`, `IsSpacecraftAssignedToOtherProvider`. Assigned ships excluded from shared quota pool.
- Persistence extended for all new fields.

### Ship Status Tracking (06-01e)

- New `ShipState` enum and `QuotaShipStatus` struct for per-ship UI status.
- Query methods: `BuildShipStatus`, `GetShipStatusesForQuota`, `GetShipStatusesForAssignedIds`, `GetAllShipsForGetRequest`, `GetAllShipsForSendProvider`.
- `SetShipBlockedReason` bulk-sets blocked reason from planner failure callbacks.
- `AbortLogisticsCycle` gained optional `failureReason` parameter.
- `GetAccessibleFuelStock` for destination fuel accessibility checks.

### Ship Assignment UI & Status Panels (06-01f)

- Expandable inbound-ship status panels on GET rows; outbound-ship status panels on SEND rows.
- Provider edit panel gained "Use shared logistics pool" toggle and per-type spacecraft assignment picker with per-ship transfer/backhaul/fuel-probe controls.
- Fuel-probe toggle added to quota detail panel.
- New `SimpleTooltip` MonoBehaviour for hover tooltips on injected UI elements.
- Planner failure callback now propagates translated failure message to individual ships via `SetShipBlockedReason`.

---

## 2026-05-23 -- Backhaul, Export to Orbit, and UI Tweaks

### Backhaul on Return Trips

- `[LOGI-RETURN]` trips can now carry cargo back from the destination body.
- Per-spacecraft toggle (`Back` button) on quota rows controls whether a ship attempts backhaul.
- On return, scans SEND providers at the current body and matches against GET requests at the home body, sorted by priority descending.
- Can also use orbit-staged stock when the ship is in orbit and the parent surface SEND rule has `Export to Orbit` enabled.
- Single-resource planning: picks the best single resource, capped to min(surplus, need, capacity).
- Fastest return preference is honored before backhaul; backhaul cargo is conservatively capped and the ship falls back to an empty return if stock rejects the cargo.
- Mission names show both the outbound resource icon and the backhaul resource icon.
- Backhaul cargo counts as in-flight delivery, preventing overfilling requests.
- Backhaul stock is committed only after the return cycle accepts the cargo, avoiding stale reservations after failed handoff attempts.

### Export to Orbit

- New `Export to Orbit` checkbox on surface SEND rules.
- Automatically lifts surplus above minimum reserve to local orbit using launch vehicles.
- Smart batching by launch infrastructure: cheap launchers (space elevator, spinlaunch, magrails) launch any amount; standard rockets require ≥50% fill; other facility-backed launchers require ≥10% fill.
- Creates `[LOGI-ORBIT]` missions using stock LV + LOC resolution.
- Uses the same LOC dispatch path as standard surface SEND -> orbit GET requests.
- Treats the stock low-orbit container as a shared logistics carrier; parallelism is limited by LV support, stock, and active mission state rather than by a fake container identity.
- Exported orbit inventory is indexed as a valid provider source for later GET planning, inheriting priority from the parent surface SEND rule.
- Uses `CommitStock` as natural throttle — no artificial cooldowns.

### UI Changes

- Remove button for GET/SEND rules is now visibly red (`0.55, 0.15, 0.15`).
- Fixed X button rendering: added `preferredHeight`, `Image.Type.Simple`, and `raycastTarget`.
- Backhaul toggle button uses purple when active (`0.6, 0.45, 0.82`), muted when off.
- Export to Orbit toggle shown only on surface bodies (`NeedVehicleToLaunch()`).
- SEND row labels show `export-to-orbit` suffix when enabled.

### Persistence

- Added `backhaul` field to spacecraft/LV quota save data.
- Added `exportToOrbit` field to provider save data.
- Both default to `false`, safe for old saves.

---

## 2026-05-22 -- UI Polish Pass

### UI Styling

- Fixed broken button sprites across all UI elements. Buttons now use plain rectangles (`sprite = null`) instead of sampling a stock sprite that rendered incorrectly.
- Added hover and press feedback to all buttons using explicit `ColorBlock` with `btn.targetGraphic = bg`, matching FleetTracker's `AddIconButton` pattern.
- Fixed button flashing on UI refresh. When sections rebuild (ClearContent + recreate), new buttons appeared under the cursor and Unity triggered a normal-to-highlighted fade transition. Fixed by setting `fadeDuration = 0f` in all ColorBlocks.
- Grayed out row backgrounds while keeping tinted button colors (accent blue, confirm green, remove red, etc.).
- Deduplicated toggle button creation with `MakeToggleButtonGo` helper (minimum, one-shot, auto-buy, auto-sell toggles).

### Number Formatting

- Adopted FleetTracker-style compact notation with unit suffixes: `1.2MT`, `5KT`, `800T`, `9.5T`.
- Applied `FormatCompactAmount` to all amount displays: GET targets/minimums, SEND reserves, auto-sell per-month, amount input display, minimum shipment quotas.

### Priority Display

- Priority is now shown as a color-coded badge to the right of each GET/SEND summary row, just left of the EDIT button.
- Colors: Low = `#7799AA` (blue-grey), Normal = `#A8A8A8` (grey), High = `#DDAA44` (amber), Critical = `#DD5544` (red).
- Removed the inline "priority X" text from the label string. All priority levels (including Normal) display a badge.

### Resource Names

- Resource names in GET and SEND summary rows are now bold white (`<b><color=#EEEEF0>`) for visual hierarchy.

### Market Resources

- Resources not in the logistics network but with local sell offers now show a `[MARKET]` label in light blue in the GET resource picker, instead of being marked unavailable.

### Font Source

- Font resolution now matches FleetTracker's approach: samples from the stock `NotificationManager` notification history panel first, falls back to the notification UI prefab, then any active TMP element.

---

## 2026-05-19 -- Routing and Launch Support

- Fixed facility-backed LV quota handling so Space Elevator and similar facility LVs are counted through stock `GetListLaunchVehicle(...)`.
- Made LV quota matching case/whitespace tolerant.
- Prefer facility launch support over normal rockets when both match a quota.
- Added self-launch checks for low-gravity bodies (e.g., Stratos from Luna) based on stock thrust/gravity behavior.
- Added logistics override for `CheckLVFullListOrNone` to distinguish true LV requirements from valid self-launches.
- Restored and hardened `[LOGI]`/`[LOGI-RETURN]` mission naming with stock cyclical mission flag coordination.
- Added cached LOGI cycle name lookup by spacecraft ID and route.
- Suppressed preview trajectory creation for code-created logistics plans.
- Blocked empty outbound `[LOGI]` flights before stock `CreateFly`.
- For code-created cyclical logistics missions, force fastest-transfer flags onto the mission parameter when the quota requests it.
- Fixed stock bug where `EffectiveDeltaVOld` stays at 0 for code-created plans, causing fastest-route search to fail. Computed actual max delta-V via Tsiolkovsky equation.
- Kept return-fuel reserve in cargo space rather than relying on spacecraft tanks.
- Added fallback reserve when stock fuel probe reports zero.
- Reduced invalid-cycle churn by blocking dispatch when fuel cannot be manifested.
- Added per-day planner snapshot fields for active counts, committed IDs. Reused snapshot data instead of repeated full scans.
- Updated snapshot counts immediately after dispatch for same-day accuracy.
- Reduced orphan trajectory cleanup to periodic 30-day maintenance.
- Quieted most non-error logging behind verbose flag.

---

## 2026-05-18 -- Routing, Return Fuel, Naming, and Diagnostics

- Added ranked route selection replacing first-valid-provider behavior.
- Added source-side orbital relay hop (surface -> source orbit -> destination).
- Added source ranking: local orbital > remote orbital > surface sources.
- Added launch-support scoring for magnetic rails, space elevators, spin launchers.
- Added route-level planning locks (one async job per route at a time).
- Reworked spacecraft quota to count actual ships and report active/available usage.
- Added numeric LV quota entries.
- Added quota checks excluding ships in active flights, return-home state, or stock cycles.
- Added return-home cooldowns and escalation for repeated failures.
- Added cleanup for completed trajectories and stale unlaunched missions.
- Added async return-fuel probing through stock planner.
- Added return-fuel reserve cargo to outbound manifests.
- Fixed normalCargo accounting after fuel displacement.
- Blocked fuel-only `[LOGI]` deliveries.
- Updated SEND UI wording, allowed zero-reserve providers.
- Added request status text for blocked states.
- Added GET in-transit vehicle/arrival info.
- Added naming patches across stock mission creation, row display, and map labels.
- Added stock `RemoveMissions` safe prefix guarding against null/destroyed trajectories.
- Added comprehensive diagnostics: route scoring, fuel probes, cargo caps, manifests, schedule, launch attempts.
- Added stronger runtime-state reset for save/load boundaries.
- Extended post-load reconciliation for active logistics missions.

---

## 2026-05-12 -- Planner Race Fix, Localization, and Dead Code Cleanup

- Fixed planner race where freshly-dispatched cycles were cleaned up as "stale" before the async CodeJobSystem callback fired, causing duplicate dispatches under heavy time acceleration.
- Added `IsCycleWaitingOrPlanned` carrier-still-home check as a hard guard independent of grace window.
- Added `CyclePlanningGraceDays` and `VerboseLogging` as config entries (previously hardcoded constants).
- Fixed `ClampToOutstandingRequest` to subtract in-flight deliveries, preventing double-counting on relay re-entry.
- Fixed sticky "in transit" status by demoting request to Pending before `TryCreateDeliveries`, so failed dispatch correctly shows the right state.
- Centralized all user-visible strings in `LogisticsStrings.cs` with `LEManager.Get()` localization support. Keys namespaced under `logisticsmod.status.*`, `logisticsmod.note.*`, `logisticsmod.blocker.*`, `logisticsmod.transit.*`.
- Added GET-row text wrapping with `ContentSizeFitter` and explicit `LayoutElement` to prevent long status notes from clipping.
- Removed dead code: `LogisticsRequest.takeFuelFromTarget`, `.homeProvider`, `.activeMissionId`, `LogisticsObserver.ResolveReturnHome()`.

---

## Earlier -- Core Feature Development

### Auto-Buy and Auto-Sell

- GET Auto-Buy: optional automatic purchase from local sell offers with max price cap.
- SEND Auto-Sell: continuous or per-month selling to local buy offers with min price floor.
- Uses stock `Offer.FullFill()` for money/resource movement.
- Suppresses new-offer notifications at bodies where logistics automation just transacted.

### One-Shot Requests

- GET requests can be marked one-shot; the rule removes itself after fulfillment.

### Priority System

- Four-level priority (Low / Normal / High / Critical) on both GET and SEND rules.
- Higher priority requests are fulfilled first in the daily planning loop.

### Minimum Shipment Size

- Per spacecraft-type minimum useful cargo load, configured on quota entries.
- Ships won't dispatch with less than the minimum.

### Transfer Preference

- Per spacecraft-type toggle between Fastest and Optimal trajectory planning.
- Fastest mode forces the stock porkchop search with corrected delta-V.

### Save/Load Safety

- JSON persistence per save file.
- Runtime state cleared on save boundaries.
- Post-load reconciliation matches active LOGI cycles to requests.
- Stale object cleanup on save.
