# LogisticsModSdk Integration

This document explains how `LogisticsModSdk` consumes `SolarExpanseSdk`.

The goal is side-by-side stability work: keep the original `LogisticsMod` untouched, while building a new SDK-backed plugin that can eventually replace it after parity testing.

## Projects

SDK:

- Path: `C:\Users\parft\Documents\SolarExpanseMods\SolarExpanseSdk`
- Plugin ID: `com.solarexpanse.sdk`
- Deploy folder: `BepInEx\plugins\solarexpansesdk`

Consumer:

- Path: `C:\Users\parft\Documents\SolarExpanseMods\LogisticsModSdk`
- Plugin ID: `com.logisticsmod.sdk`
- Deploy folder: `BepInEx\plugins\logisticsmodsdk`
- Depends on: `com.solarexpanse.sdk`

## Startup Behavior

`LogisticsModSdk.Plugin` checks BepInEx `Chainloader.PluginInfos` for the original plugin ID:

```csharp
com.logisticsmod
```

If the original mod is loaded, `LogisticsModSdk` logs a warning and disables itself. This prevents duplicate daily planners, duplicate UI injection, duplicate save writes, and duplicate mission dispatch.

To test `LogisticsModSdk`, move or remove the original `BepInEx\plugins\logisticsmod` plugin first.

## SDK-Owned Integration

`LogisticsModSdk\SdkIntegration.cs` is the bridge between the SDK and the logistics implementation.

It registers:

```csharp
SolarSdk.ObjectInfoUi.RegisterWindowComponent<UI.LogisticsUI>();
SolarSdk.ObjectInfoUi.RegisterRocketRowDecorator(BuildLogisticsReservationMarker);
SolarSdk.Events.SaveLoading += ResetLoadState;
SolarSdk.Events.BeforeSave += saveName => LogisticsPersistence.Save(saveName);
SolarSdk.Events.SaveLoaded += OnSaveLoaded;
SolarSdk.Events.DayTick += LogisticsObserver.OnDayChange;
SolarSdk.Events.PostLoadFirstTick += () => LogisticsObserver.OnDayChange(0);
SolarSdk.Events.ObjectInfoChanged += OnObjectInfoChanged;
SolarSdk.Events.ObjectInfoRebuild += OnObjectInfoRebuild;
SolarSdk.Diagnostics.RegisterSnapshotProvider("logistics", LogisticsObserver.BuildSdkDebugSnapshot);
SolarSdk.MissionTags.RegisterMissionPrefix("[LOGI");
SolarSdk.MissionTags.RegisterNameResolver("logisticsmodsdk", ResolveLogisticsMissionName);
```

This replaces the old copied patch responsibilities from:

- `Patches\SaveLoadPatches.cs`
- `Patches\TimeControllerPatches.cs`
- `Patches\ObjectInfoWindowPatches.cs`

Those files remain in the source tree for reference, but `LogisticsModSdk.csproj` excludes them from compilation.

## Logistics-Owned Behavior

The new mod still owns logistics policy and UI content:

- GET/SEND rules
- provider rules
- spacecraft quotas
- launch vehicle quotas
- provider-assigned spacecraft
- direct links
- relay state
- auto-buy and auto-sell
- return fuel logic
- backhaul and fuel-probe settings
- route ranking and dispatch policy
- status text and logistics UI sections

The SDK should not decide which route to choose, how much cargo to ship, which provider wins, or when a logistics request is satisfied. Those are LogisticsModSdk responsibilities.

## Save Data

New save file:

```text
BepInEx\saves\<saveName>\LogisticsSdkData.json
```

Legacy import file:

```text
BepInEx\saves\<saveName>\LogisticsData.json
```

`LogisticsPersistence.Load(saveName)` first looks for `LogisticsSdkData.json`. If it does not exist, it reads legacy `LogisticsData.json` if present and logs the import. Future saves write only `LogisticsSdkData.json`; the old file is never modified.

This is intentionally simple and compatible with the current save model. The SDK `SaveStore` exists, but `LogisticsModSdk` still uses its existing serializer so full parity can be preserved during the first rewrite phase.

## UI Integration

The SDK attaches `LogisticsUI` to `ObjectInfoWindow` through:

```csharp
SolarSdk.ObjectInfoUi.RegisterWindowComponent<UI.LogisticsUI>();
```

The SDK then raises object-info refresh events. `SdkIntegration` forwards those to:

- `LogisticsUI.RefreshData(data)`
- `LogisticsUI.RebuildLayout()`

Rocket-row markers are registered as a decorator. The marker logic was moved out of the old object-info patch and into `SdkIntegration.BuildLogisticsReservationMarker`.

## Mission Integration Status

The old complex mission parity file is retained as reference code only:

```text
LogisticsModSdk\Patches\SpaceCraftCyclicalMissionControllerPatches.cs
```

It no longer registers Harmony patch methods. All stock mission, cycle, loadout, tag, and market integration points that LogisticsModSdk currently needs are SDK-owned.

The SDK now owns several formerly local mission hooks:

- `GameManager.SetPMParameterForCodeJobSystem` callback wrapping through `SolarSdk.MissionPlanning`.
- Logistics mission-name restoration through `SolarSdk.MissionTags`.
- Code-job planner preparation, callback-before cargo/name work, and callback-after diagnostics through `SdkIntegration` subscribers.
- Arrival notification suppression through `SolarSdk.MissionPlanning.SuppressArrivalNotification`.
- Self-launch override through `SolarSdk.MissionPlanning.CheckSelfLaunchOverride`.
- Fastest-route delta-V correction through `SolarSdk.MissionPlanning.BeforeFastestSearch` and `ApplyCodeFastestDeltaVCorrection`, including protected reserve fuel so full-tank Fastest searches do not spend fuel reserved for the return leg.
- Create-fly guard/logging through `SolarSdk.MissionPlanning.BeforeCreateFly` and `AfterCreateFly`.
- Preview trajectory suppression through `SolarSdk.MissionPlanning.SuppressPreviewTrajectory`.
- Mission completion cleanup through `SolarSdk.MissionPlanning.MissionCompleted`.
- Cycle replan suppression through `SolarSdk.CyclicalMissions.CheckCycleReplan`.
- Cycle planning failure notification translation/suppression through `SolarSdk.CyclicalMissions.CyclePlanNotification`.
- Stock cycle handoff through `SolarSdk.CyclicalMissions.HandOffToStockPlanner`.
- Stock cycle construction/add/register through `SolarSdk.CyclicalMissions.CreateAndAddCycle` and `SdkCycleDraft`.
- Stock cycle cargo-created diagnostics through `SolarSdk.MissionLoadout.CargoCreatedForCycle`.
- Market offer notification policy through `SolarSdk.Market.ShouldSuppressOfferNotification`.

Mission name/display preservation is SDK-backed. `LogisticsModSdk` registers a `SolarSdk.MissionTags` resolver that maps stock mission contexts back to `[LOGI]` / `[LOGI-RETURN]` cycle names. The SDK applies those names during `PMMissionParameter` changes, `MissionInfoManager.CreateMissionInfo`, code-created mission scheduling, map/flight labels, mission rows, and tagged mission-label cleanup.

## Dispatch Tracking And Reservations

`LogisticsModSdk` now builds stock cycles through `SdkCycleDraft` and `SolarSdk.CyclicalMissions.CreateAndAddCycle(...)` in:

- direct delivery creation
- LV/container delivery creation
- return-home creation

The SDK assigns dispatch IDs such as:

```text
logi-20260617-0001
```

Those IDs are tracked through:

- cycle registration
- carrier instance registration
- mission-parameter registration
- handoff to stock planner
- code-job callback start/completion/failure
- mission info registration
- mission completion
- cleanup/removal

For real spacecraft dispatches, `LogisticsModSdk` reserves the primary ship through `SolarSdk.Fleet` and releases that reservation during runtime reset, mission cleanup, and cycle removal. For LV/container dispatches, the `Orbital Payload Container` is a synthetic carrier, so LogisticsModSdk tracks it in the SDK synthetic-carrier ledger instead of reserving it as a real spacecraft.

The SDK resolves dispatch IDs in mission-planning logs by explicit `PMMissionParameter` registration, carrier instance, real spacecraft ID, and active cycle lookup. `[LOGI]` code-job logs should therefore show the same dispatch ID across `sdk.cycles`, `sdk.missionPlanning`, LogisticsModSdk cycle logs, launch logs, mission info, and cleanup.

`LogisticsObserver.HandOffCycleToStockPlanner` delegates controller setup and stock `TryPlanCycleMission` calling to:

```csharp
SolarSdk.CyclicalMissions.HandOffToStockPlanner(...)
```

LogisticsModSdk still owns the policy callbacks passed to that SDK wrapper: releasing route locks, clearing pending planning delivery, removing one-shot cycles, and deciding whether planner-not-started dispatches should be removed.

## Diagnostics Snapshot

`LogisticsObserver.BuildSdkDebugSnapshot()` is registered as the SDK `logistics` snapshot provider. It returns:

- logistics object summaries
- active `[LOGI]` cycle summaries
- runtime state counts
- SDK reservation snapshot
- SDK synthetic carrier snapshot
- SDK dispatch tracker snapshot

Snapshots are manual/API-triggered through:

```csharp
SolarSdk.Diagnostics.WriteSnapshot("manual");
```

They can also be triggered in game by setting `Diagnostics.ManualSnapshotHotkey` in `com.solarexpanse.sdk.cfg` to a Unity `KeyCode` name such as `F10`.

The SDK also writes rate-limited automatic snapshots for important diagnostics failures: planner-not-started/cycle failure phases, missing dispatch correlation for `[LOGI]` code jobs, fleet reservation owner conflicts, synthetic-carrier owner conflicts, and SDK event handler exceptions.

Snapshot filenames include UTC milliseconds and the automatic reason/key, so several failed dispatches in the same second produce separate files instead of overwriting each other.

The SDK writes snapshots under:

```text
BepInEx\SolarExpanseSdk\Diagnostics
```

## What To Move Next

Recommended next refactor order:

1. Verify all migrated SDK hooks in game, then delete the old reference parity file.
2. Move additional reusable policy-neutral helpers into SDK only when another mod needs them.
3. Keep route selection, request evaluation, provider scoring, relay policy, return-fuel policy, and UI business logic in LogisticsModSdk.

Do not move route selection, request evaluation, provider scoring, relay policy, return-fuel policy, or UI business logic into the SDK.

## Current Integration Target

The active migration target is to reduce duplicate Harmony ownership without changing logistics policy:

- `LogisticsModSdk` registers arrival notification suppression through `SolarSdk.MissionPlanning.SuppressArrivalNotification`.
- `LogisticsModSdk` registers logistics self-launch override through `SolarSdk.MissionPlanning.CheckSelfLaunchOverride`.
- `LogisticsModSdk` registers code-job setup, callback-before, and callback-after handlers through `SolarSdk.MissionPlanning`; the SDK owns the Harmony callback wrapper for `GameManager.SetPMParameterForCodeJobSystem`.
- Code-job handlers still execute LogisticsMod policy: dispatch correlation, moon-case fastest fallback, cached precalculation, mission-name restore, cargo capping, and trajectory logging.
- `LogisticsModSdk` registers fastest-search, create-fly, preview trajectory, mission completion, cargo-created, and market-offer handlers through SDK services.
- `LogisticsModSdk` registers cycle replan and cycle failure-notification handlers through `SolarSdk.CyclicalMissions`; the SDK owns the Harmony prefixes for `TryPlanCycleMission` and `ShowNotification`.
- `LogisticsModSdk` creates stock cycles through `SolarSdk.CyclicalMissions.CreateAndAddCycle` and hands them to `SolarSdk.CyclicalMissions.HandOffToStockPlanner`; Logistics callbacks still own cleanup and one-shot removal policy.
- The local parity shim remains compiled as reference code only until an in-game verification pass proves it can be deleted.

Diagnostics cleanup keeps repeated correlation and cleanup paths readable:

- repeated `RegisterMissionInfo` calls are counted on the dispatch tracker and throttled in verbose logs,
- repeated identical phase updates are throttled,
- cleanup checks the synthetic-carrier and real-reservation ledgers before releasing,
- harmless missing-release attempts are throttled by the SDK fleet service.

Cargo/fuel integration now uses SDK loadout primitives for reusable mechanics:

- regular resource cargo lookup and amount calculation,
- add-or-increase resource cargo,
- non-fuel cargo reduction when return fuel displaces payload,
- reserve propellant configuration on `PMMissionParameter`,
- compact cargo manifest formatting.

The intent is that any new LogisticsModSdk fuel/cargo behavior uses `SolarSdk.MissionLoadout` first. LogisticsModSdk should only keep code locally when it is logistics policy, such as return-fuel reserve amounts, backhaul priority, or provider/request thresholds.

Return fuel reserves should use `SolarSdk.MissionLoadout.ConfigureProtectedReservePropellant` rather than ordinary resource cargo when the fuel is intended to ride in the spacecraft tank. For Optimal routes, the SDK asks stock validation for a loaded fuel amount that leaves the desired reserve. For Fastest routes, LogisticsModSdk passes the reserve to `ApplyCodeFastestDeltaVCorrection` so the porkchop search sees full-tank mass but only uses delta-v down to dry mass plus reserve.

Dispatch boundary validation now runs through `SolarSdk.Missions.Validate(..., RunStockValidation = false)` before direct, LV/container, and return-home stock cycles are registered. This catches missing company/source/target/carrier/cargo, invalid synthetic carrier use, busy real ships, and obvious carrier cargo-capacity overflow while leaving stock route/fuel planning to the existing code-job path.

## Build Commands

```powershell
cd C:\Users\parft\Documents\SolarExpanseMods\SolarExpanseSdk
dotnet build -c Release

cd C:\Users\parft\Documents\SolarExpanseMods\LogisticsModSdk
dotnet build -c Release
```

Successful builds deploy to the game plugin folders and also mirror DLL/PDB files into each project's `plugins` folder.

Do not run both release builds in parallel: `LogisticsModSdk` builds `SolarExpanseSdk` as a project reference, so parallel builds can contend for the same SDK output DLL.

## Smoke Test Checklist

- SDK-only load:
  - `SolarExpanseSdk.dll` loads.
  - SDK logs capabilities summary.

- SDK + LogisticsModSdk load:
  - `LogisticsModSdk.dll` loads if original `com.logisticsmod` is absent.
  - `LogisticsModSdk` disables itself if original `com.logisticsmod` is present.
  - Object info window gets logistics sections.
  - Switching bodies refreshes logistics data.
  - Daily tick runs `LogisticsObserver.OnDayChange`.
  - Save writes `LogisticsSdkData.json`.
  - Legacy save imports from `LogisticsData.json` when SDK save is missing.

## Current Limitations

- SDK patch capability reporting is coarse-grained.
- `LogisticsModSdk` still uses its existing persistence code rather than `SolarSdk.SaveStore`.
- The SDK creates/adds/registers stock cycles from drafts, but LogisticsModSdk still owns the policy that decides when and where to create those cycles.
- SDK dispatch tracking observes and diagnoses stock cycles; it does not own route/request policy.
- Synthetic carrier records are session diagnostics only and are not durable save data.
