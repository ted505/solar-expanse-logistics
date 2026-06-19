# SDK Harmony Patches

This document records the Harmony patches owned by `SolarExpanseSdk`.

The SDK is intended to own common, brittle integration points so consumer mods can subscribe to stable events/services instead of patching the same game methods independently.

## Plugin

- Project: `C:\Users\parft\Documents\SolarExpanseMods\SolarExpanseSdk`
- Assembly: `SolarExpanseSdk`
- Plugin ID: `com.solarexpanse.sdk`
- Deploy folder: `BepInEx\plugins\solarexpansesdk`

## Verbose SDK Logging

SDK verbose logging is controlled by `SolarExpanseSdk` config:

- `Diagnostics.VerboseLogging` defaults to `false`.
- `Diagnostics.EnabledChannels` defaults to `*`.
- `Diagnostics.TimingLogging` defaults to `false`.
- `Diagnostics.TimingThresholdMs` defaults to `5.0`.
- `Diagnostics.ManualSnapshotHotkey` defaults to blank/disabled. Set it to a Unity `KeyCode` name such as `F10` to write a manual diagnostics snapshot while in game.

Current SDK channels:

- `sdk.lifecycle`
- `sdk.save`
- `sdk.objectInfoUi`
- `sdk.fleet`
- `sdk.cycles`
- `sdk.missionLoadout`
- `sdk.missionPlanning`
- `sdk.market`
- `sdk.patches`
- `sdk.events`
- `sdk.diagnostics`

Warnings and errors are logged regardless of verbose settings. Noisy event, cycle, reservation, and object-info logs require verbose logging. High-volume `sdk.events` and `sdk.objectInfoUi` verbose entries are throttled and emit compact `suppressed=<count>` summaries; dispatch, fleet, mission-planning, save, and patch logs are not throttled.

## Patch Ownership Rules

- SDK patches should raise events or call service registries.
- SDK patches should not directly call `LogisticsModSdk` or any consumer mod.
- Consumer mods should avoid patching SDK-owned lifecycle/UI methods unless they are intentionally providing a compatibility shim.
- Remaining mission-planning parity patches in `LogisticsModSdk` should be migrated one behavior at a time, with SDK ownership documented here before duplicate local hooks are removed.

## LifecyclePatches

File: `Patches\LifecyclePatches.cs`

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `LoadSaveManager.ExtractAllFromSaveData` | Prefix | Raises `SolarSdk.Events.SaveLoading`; clears SDK post-load tick state. | Yes | Mods may leak runtime state across saves. |
| `LoadSaveManager.ExtractAllFromSaveData` | Postfix | Reads `LastSaveName` and raises `SolarSdk.Events.SaveLoaded(saveName)`. | Yes | Per-save mod data may not load/reconcile. |
| `LoadSaveManager.SaveToFile(string)` | Prefix | Raises `SolarSdk.Events.BeforeSave(saveName)`. | Yes | Mods may miss their save write window. |
| `LoadSaveManager.SaveToFile(string)` | Postfix | Raises `SolarSdk.Events.AfterSave(saveName)`. | No | Post-save diagnostics/hooks may not fire. |
| `TimeController.Update` | Prefix | Subscribes once to `onEachDayChange`; raises `DayTick(days)` through the stock event. | Yes | Daily planners do not run. |

`PostLoadFirstTick` is not a direct game patch. It is emitted by `SdkEvents` after the first `DayTick` following `SaveLoaded`.

## ObjectInfoUiPatches

File: `Patches\ObjectInfoUiPatches.cs`

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `ObjectInfoWindow.Awake` | Postfix | Attaches registered window components and raises `ObjectInfoWindowReady(window)`. | Yes for UI mods | Registered UI components will not be injected. |
| `ObjectInfoWindow.SetData(ObjectInfoData, bool)` | Postfix | Raises `ObjectInfoChanged(window, data, fromObjectName)`. | Yes for UI refresh | UI may show stale body/location data. |
| `ObjectInfoWindow.RebuildLayout` | Postfix | Raises `ObjectInfoRebuild(window)`. | Yes for injected sections | Injected sections may not relayout correctly. |
| `UIRowRocket.SetData` | Postfix | Appends marker text returned by registered rocket-row decorators. | No | Row badges/markers do not show. |

Consumers register with:

```csharp
SolarSdk.ObjectInfoUi.RegisterWindowComponent<MyComponent>();
SolarSdk.ObjectInfoUi.RegisterRocketRowDecorator(row => "...marker...");
```

The SDK catches decorator exceptions and skips bad markers rather than breaking the stock row.

## MissionPlanningPatches

File: `Patches\MissionPlanningPatches.cs`

These are provisional v1 hooks. They expose mission-planning interception points, and the SDK now owns the `GameManager.SetPMParameterForCodeJobSystem` callback wrapper so consumer mods can prepare/cap/cache code-job missions without patching that stock method directly.

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `GameManager.SetPMParameterForCodeJobSystem` | Prefix with callback wrapper | Raises `MissionPlanning.BeforeCodeJobPlan(parameter, context)`, then wraps the stock callback to raise `BeforeCodeJobCallback` and `AfterCodeJobCallback`. | No in v1 | Consumers cannot observe or prepare code-created plans through SDK. |
| `PMTabSchedule.ButtonFastestClickButton` | Prefix/Postfix | Raises `MissionPlanning.BeforeFastestSearch(schedule, parameter, context)` before stock fastest search and `AfterFastestSearch(parameter, context)` after. The SDK also exposes `ApplyCodeFastestDeltaVCorrection`. | No in v1 | Consumers cannot correct or observe fastest-search behavior through SDK. |
| `PMTabSchedule.CreateFly` | Postfix plus MissionTag prefix | `MissionTags` restores tagged names and asks `MissionPlanning.BeforeCreateFly` whether to suppress launch creation; `MissionPlanning.AfterCreateFly` runs after stock returns. | No in v1 | Consumers must patch create-fly guards locally. |
| `PMTabSchedule.CreatedTrajectory` | Prefix | Lets subscribers suppress stock preview trajectory creation. | No in v1 | Consumers must patch preview-trajectory suppression locally. |
| `MissionInfo.Complete` | Postfix | Marks correlated dispatch complete and raises `MissionPlanning.MissionCompleted`. | No in v1 | Consumers must patch mission completion cleanup locally. |
| `PMMissionParameter.CheckLVFullListOrNone` | Prefix | Lets subscribers override the boolean result. | No in v1 | Self-launch overrides must remain in consumer patches. |
| `Spacecraft.ShowNotificationLand` | Prefix | Lets subscribers suppress arrival notifications. | No in v1 | Consumer patches may still be needed for suppression parity. |
| `Spacecraft.ShowNotificationAsteroidImpact` | Prefix | Same as above. | No in v1 | Same as above. |
| `Spacecraft.ShowNotificationSolarSystem` | Prefix | Same as above. | No in v1 | Same as above. |
| `Spacecraft.ShowNotificationAsteroidPull` | Prefix | Same as above. | No in v1 | Same as above. |

Consumers use:

```csharp
SolarSdk.MissionPlanning.BeforeCodeJobPlan += (pmp, ctx) => { ... };
SolarSdk.MissionPlanning.BeforeCodeJobCallback += (pmp, ctx) => { ... };
SolarSdk.MissionPlanning.AfterCodeJobCallback += (pmp, ctx) => { ... };
SolarSdk.MissionPlanning.BeforeFastestSearch += (schedule, pmp, ctx) => { ... };
SolarSdk.MissionPlanning.BeforeCreateFly += ctx => true;
SolarSdk.MissionPlanning.SuppressPreviewTrajectory += (schedule, pmp, ctx) => true;
SolarSdk.MissionPlanning.CheckSelfLaunchOverride += pmp => true;
SolarSdk.MissionPlanning.SuppressArrivalNotification += (sc, context) => true;
```

`BeforeCodeJobPlan` runs before stock begins planning and is the right place to set planner flags, register dispatch correlation, or apply cached route data. `BeforeCodeJobCallback` runs immediately before the stock callback creates the mission and is the right place to restore names or cap cargo. `AfterCodeJobCallback` runs after the stock callback returns and is the right place to cache generated planner data or log trajectory diagnostics.

## CyclicalMissionPatches

File: `Patches\CyclicalMissionPatches.cs`

These patches keep consumer mods out of the most fragile stock cycle controller entry points. Consumer mods still create their own `CycleMissionsData` for now, but SDK owns the controller hook, dispatch correlation, and notification surface.

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `SpaceCraftCyclicalMissionController.TryPlanCycleMission` | Prefix | Raises `CyclicalMissions.CheckCycleReplan` and lets subscribers suppress duplicate/owned replans. | No in v1 | Consumer mods must patch stock cycle planning to suppress duplicate attempts. |
| `SpaceCraftCyclicalMissionController.ShowNotification` | Prefix | Raises `CyclicalMissions.CyclePlanNotification` and lets subscribers translate/suppress owned failure notifications. | No in v1 | Consumer mods must patch stock cycle notification failures directly. |

Consumers use:

```csharp
SolarSdk.CyclicalMissions.CheckCycleReplan += ctx => ctx.PlanFlyWas ? true : null;
SolarSdk.CyclicalMissions.CyclePlanNotification += ctx => { ctx.SuppressNotification = true; };
```

Consumer mods should prefer `SolarSdk.CyclicalMissions.HandOffToStockPlanner(...)` when handing a registered cycle to stock. That wrapper sets up `SpaceCraftCyclicalMissionController`, registers carrier and mission-parameter correlation, marks code-job start/completion/failure, detects planner-not-started cases, and writes SDK diagnostics.

## MissionTagPatches

File: `Patches\MissionTagPatches.cs`

These patches preserve tagged mission names across stock mission creation and display surfaces. Consumers register tag prefixes and name resolvers through `SolarSdk.MissionTags`; the SDK applies the resolved name without knowing consumer policy.

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `PMMissionParameter.ChangeMissionName(string, bool)` | Prefix | Replaces stock cyclical placeholder names with a resolved tagged name. | No in v1 | Code-created tagged missions may lose their display name before launch. |
| `PMTabDestination.ChangeMissionName()` | Prefix | Preserves resolved tagged mission names when stock destination UI would regenerate names. | No in v1 | Stock UI may overwrite tagged names. |
| `PMTabSchedule.CreateFly` | Prefix | Restores resolved tagged mission names immediately before stock launch creation. | No in v1 | MissionInfo creation may receive a stock/generated name. |
| `PMTabSchedule.OnClickScheduleButtonForCode` | Postfix | Applies resolved names to code-created `MissionInfo` objects. | No in v1 | Code-created missions may not retain tag/display metadata. |
| `MissionInfoManager.CreateMissionInfo` | Prefix/Postfix | Resolves name before creation and reapplies/registers after creation. | No in v1 | MissionInfo may not be correlated to SDK tags/dispatch. |
| `MissionsLabelsMainUI.SetData` | Postfix | Rewrites flight/map label text to the resolved tagged name. | No in v1 | Map labels may show stock cyclical names. |
| `MissionRow.SetMissionInfo` | Postfix | Rewrites old mission-row name text. | No in v1 | Mission list rows may show stock names. |
| `MissionRowNew.SetMissionInfo` | Postfix | Reapplies name metadata for new mission rows. | No in v1 | New mission list rows may miss tagged metadata. |
| `MissionsLabelsMainUIManager.RemoveMissions` | Prefix | Safely removes tagged mission labels and trajectory objects before completing the mission. | No in v1 | Consumers must patch null/destroyed label cleanup locally. |

Consumers register with:

```csharp
SolarSdk.MissionTags.RegisterMissionPrefix("[LOGI");
SolarSdk.MissionTags.RegisterNameResolver("mod-id", ctx => "...name...");
SolarSdk.MissionTags.MissionInfoNameApplied += (missionInfo, name) => { ... };
```

## MissionLoadoutPatches

File: `Patches\MissionLoadoutPatches.cs`

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `ObjectInfoData.CreatedCargoToTakeNormal` | Postfix | Raises `MissionLoadout.CargoCreatedForCycle` with stock-created cargo, cycle, source, carrier, LV, and load flags. | No in v1 | Consumers must patch cargo-created diagnostics locally. |

## MarketPatches

File: `Patches\MarketPatches.cs`

| Target | Type | SDK behavior | Required | Failure impact |
| --- | --- | --- | --- | --- |
| `MarketOfferManager.AddOffer` | Prefix | Lets subscribers suppress stock offer notifications without replacing offer creation. | No in v1 | Consumers must patch market offer notification policy locally. |

## Patch Status Reporting

`Plugin.Awake` calls:

```csharp
SolarSdk.Patches.ApplyAll(harmony, typeof(Plugin).Assembly);
SolarSdk.Patches.LogSummary();
```

Current status is coarse-grained: if `PatchAll` succeeds, lifecycle/UI/mission-planning capabilities are marked available. A future SDK pass should register each patch individually so missing or signature-changed methods can downgrade only the affected capability.

In addition to `PatchAll`, startup now validates known target methods and logs `target-found` / `target-missing` records under `sdk.patches` when verbose logging is enabled. Missing targets are also recorded in the patch summary.

## Dispatch And Reservation Diagnostics

The SDK now provides correlation tracking for consumer-created cycles:

- `SolarSdk.CyclicalMissions.CreateDispatchId(ownerTag)`
- `RegisterPlannedCycle(...)`
- `RegisterMissionParameter(...)`
- `RegisterCarrier(...)`
- `MarkCodeJobStarted/Completed/Failed(...)`
- `FindDispatchId(...)`
- `HandOffToStockPlanner(...)`
- `CheckCycleReplan`
- `CyclePlanNotification`
- `GetTrackersSnapshot()`

`LogisticsModSdk` now describes stock cycles with `SdkCycleDraft` and calls `SolarSdk.CyclicalMissions.CreateAndAddCycle(...)`. Dispatch IDs appear in verbose SDK cycle logs and in LogisticsModSdk cycle logs. The SDK resolves dispatch IDs by explicit mission-parameter mapping first, then by carrier instance, real spacecraft ID, and active cycle lookup.

The SDK fleet reservation ledger logs under `sdk.fleet`:

- reservation success
- duplicate/foreign-owner reservation failures
- release success/failure
- owner-wide reservation clears
- synthetic carrier track/release events for LV container dispatches

Real spacecraft reservations and synthetic carriers are deliberately separate. Synthetic carriers such as `Orbital Payload Container#-3` are session-only diagnostic records keyed by dispatch ID and runtime carrier instance; they are never treated as durable real spacecraft reservations. This ledger is diagnostic and coordination-oriented. It does not yet enforce route policy inside LogisticsModSdk.

## Diagnostics Snapshots

The SDK exposes:

```csharp
SolarSdk.Diagnostics.RegisterSnapshotProvider(name, provider);
SolarSdk.Diagnostics.WriteSnapshot(reason);
```

Snapshots write to:

```text
BepInEx\SolarExpanseSdk\Diagnostics\<timestamp-ms>-<reason>-<key>.json
```

`LogisticsModSdk` registers a `logistics` provider containing compact logistics object summaries, active `[LOGI]` cycles, runtime state counts, SDK reservations, and SDK dispatch trackers.

Snapshots also include built-in SDK state: dispatch trackers, real fleet reservations, and synthetic carrier entries. Automatic snapshots are rate-limited by reason/key and are written for important diagnostics failures such as cycle planning failures, missing dispatch correlation for `[LOGI]` code jobs, reservation owner conflicts, and SDK event handler exceptions. Snapshot filenames include milliseconds and the automatic key to avoid overwriting multiple diagnostics emitted in the same second.

## Known Duplicate-Hook Risks

- `LogisticsModSdk\Patches\SpaceCraftCyclicalMissionControllerPatches.cs` is retained as reference code only and no longer registers Harmony patch methods.
- Do not re-enable reference parity hooks unless the matching SDK hook is proven insufficient.
- The original `LogisticsMod` plugin also patches save/load/time/UI/mission methods. `LogisticsModSdk` disables itself if `com.logisticsmod` is loaded, but the SDK itself remains loaded.

## Next Patch Refactor Targets

Remaining work is no longer Harmony-boundary migration. The next useful refactors are deleting old reference patch files after in-game verification, broadening SDK docs/examples, and adding higher-level cycle policy helpers only if another mod needs them.
