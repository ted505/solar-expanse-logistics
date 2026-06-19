# Solar Expanse SDK API Reference

This reference is written for future Codex sessions and mod authors. It focuses on what each SDK call does, whether it mutates stock game objects, and which stock subsystem it wraps.

## Safety Legend

| Label | Meaning |
| --- | --- |
| Read-only | Reads SDK or stock state without intentional mutation. |
| SDK mutation | Mutates SDK ledgers, trackers, diagnostics, or callbacks only. |
| Stock mutation | Mutates stock game objects such as `PMMissionParameter`, `CargoAll`, `MissionInfo`, UI components, or live spacecraft. |
| Stock call | Calls stock game methods that may have side effects or depend on loaded game state. |
| Requires loaded game | Should be used after managers/resources exist. Avoid in static constructors and before BepInEx plugin `Awake`/SDK initialization. |

## `SolarSdk.Missions`

High-level mission draft and validation service. This service is the preferred entry point for new mod code that wants to describe or validate mission intent without directly assembling fragile stock planner objects.

### `CreateDraft(string ownerId)`

Creates an empty `SdkMissionDraft` with a normalized `OwnerId` and a fresh `CargoAll` from `CargoAll.CreateCargoEmpty`.

- Mutates: no existing state; allocates a new SDK draft and stock `CargoAll`.
- Stock calls: `CargoAll.CreateCargoEmpty`.
- Requires loaded game: yes, because `CreateCargoEmpty` resolves `id_resource_fuel` through `AllScriptableObjectManager`.
- Use when: starting a new mission intent from mod policy.

### `FromParameter(string ownerId, PMMissionParameter parameter)`

Copies visible stock planner state into an SDK draft. Cargo is cloned through `SolarSdk.MissionLoadout.CloneCargo`, while spacecraft, launch vehicle, company, start, and target remain references to stock objects.

- Mutates: no.
- Stock calls: property getters on `PMMissionParameter`.
- Requires loaded game: yes if cargo cloning needs fuel/resource references.
- Notes: this is not a full serialization format. It is a working draft snapshot for planning and validation.

### `CloneParameter(PMMissionParameter parameter)`

Creates a draft from an existing parameter, then converts the draft back to a new `PMMissionParameter`.

- Mutates: creates and mutates a new stock `PMMissionParameter`.
- Stock calls: all calls used by `FromParameter` and `ToMissionParameter`.
- Requires loaded game: yes.
- Notes: cloned parameters preserve stock object references. They do not duplicate spacecraft, companies, or bodies.

### `ToMissionParameter(SdkMissionDraft draft)`

Builds a stock `PMMissionParameter` from a draft using stock setters such as `SetCompany`, `SetTabDestination`, `SetTabSC`, `SetTabLV`, `SetTabCargo`, `ChangeMissionName`, `SetMissionOrigin`, `SetCostType`, `SetTabDateFromPorkchope`, `SetDeltaV`, `SetFuelNeed`, and `SetOptimalFuelNeed`.

- Mutates: creates and mutates a new stock `PMMissionParameter`.
- Stock calls: many `PMMissionParameter` setters; `SetTabDestination` may recalculate route/body internals.
- Requires loaded game: yes.
- Diagnostics: registers dispatch correlation when `draft.DispatchId` is set.
- Caution: this is not a stock scheduling call. It creates planner input only.

### `Validate(SdkMissionDraft draft, SdkMissionValidationOptions options = null)`

Runs SDK structural validation on the draft and, by default, converts the draft into a `PMMissionParameter` and runs stock validation.

- Mutates: creates and mutates a temporary stock `PMMissionParameter` when stock validation is enabled.
- Stock calls: `ToMissionParameter`, `PMMissionParameter.CheckScheduleFly`, `PMMissionParameter.CheckCanPlanMission`.
- Requires loaded game: yes for stock validation.
- Caution: stock validation may update internal planner fields such as fuel values and route result state on the temporary parameter.
- Disable stock validation with `new SdkMissionValidationOptions { RunStockValidation = false }` for pure structural checks.

### `Validate(PMMissionParameter parameter, SdkMissionValidationOptions options = null)`

Validates an existing stock planner parameter.

- Mutates: stock planner may mutate its own internal fields during validation.
- Stock calls: `CheckScheduleFly`, `CheckCanPlanMission`.
- Requires loaded game: yes.
- Use when: a mod already has a stock parameter and needs translated failure details.

### `Explain(PMMissionParameter.EPlanMissionResult result)`

Translates stock planner flags into SDK validation issues without touching game state.

- Mutates: no.
- Stock calls: no.
- Requires loaded game: no.

### `TranslateFailureKind(PMMissionParameter.EPlanMissionResult result)`

Returns a single high-level `SdkMissionFailureKind` for a stock result. When multiple flags exist, the first priority match wins.

- Mutates: no.
- Stock calls: no.
- Requires loaded game: no.
- Use when: logging or status UI wants a compact failure category.

### `IsRetryable(SdkMissionValidationResult result)` / `RequiresPlayerAction(SdkMissionValidationResult result)`

Convenience queries over SDK validation issues.

- Mutates: no.
- Stock calls: no.
- Requires loaded game: no.

## `SolarSdk.MissionLoadout`

Helper service for planner cargo, fuel, crew, supply, resource availability, and vehicle payload checks. These wrappers keep all mods using the same interpretation of stock loadout fields.

### Cargo Creation And Cloning

`CreateEmptyCargo()` calls `CargoAll.CreateCargoEmpty`, including stock fuel resource setup.

`CloneCargo(CargoAll cargo)` creates a new `CargoAll` and clones cargo entries into that new owner. It preserves stock references for resource definitions, module descriptors, source modules, object info, and transaction references.

`CloneCargoItem(Cargo cargo, CargoAll owner)` clones one cargo entry into the supplied owner.

- Mutates: new cargo objects only.
- Stock calls: `CargoAll.CreateCargoEmpty`.
- Requires loaded game: yes for fresh fuel resource lookup.
- Caution: this is a structural clone, not a save-file clone. Referenced resources/modules/objects are reused.

### Cargo Normalization

`NormalizeCargo(CargoAll cargo)` ensures cargo lists and fuel cargo exist, then removes null or non-positive cargo rows from the normal, to-orbit, and gravity-assist cargo lists.

- Mutates: the supplied `CargoAll`.
- Stock calls: may resolve `id_resource_fuel`.
- Requires loaded game: yes if fuel cargo must be created.

`RemoveInvalidCargo(CargoAll cargo)` returns how many rows normalization removed.

`CapCargoToMass(CargoAll cargo, double maxMass)` calls stock `CargoAll.ChangeResourcesMassToLimit`.

- Mutates: the supplied cargo.
- Stock calls: `CargoAll.ChangeResourcesMassToLimit`.
- Caution: stock capping only reduces resource cargo; modules/crew may still keep mass above the target.

### Cargo Inspection

Read-only helpers:

```csharp
GetCargoMass(CargoAll cargo)
GetCargoFuelMass(CargoAll cargo)
GetCargoPotentialFuelMass(CargoAll cargo)
GetCargoLifeSupport(CargoAll cargo)
GetCrewCount(CargoAll cargo)
GetSupplyMass(CargoAll cargo)
GetLifeSupportFromSupply(CargoAll cargo)
GetResourceMass(CargoAll cargo, ResourceDefinition resource)
GetRegularResourceCargoItems(CargoAll cargo, bool includeToOrbit = true)
FindRegularResourceCargo(CargoAll cargo, ResourceDefinition resource)
GetRegularResourceMass(CargoAll cargo, ResourceDefinition resource, bool includeToOrbit = true)
ContainsRegularResource(CargoAll cargo, ResourceDefinition resource, bool includeToOrbit = true)
FormatCargo(CargoAll cargo)
```

- Mutates: no.
- Stock calls: `CargoAll` property/method reads.
- Requires loaded game: only when stock getters depend on manager state for module mass bonuses.

### Cargo Mutation

`AddResourceCargo(CargoAll cargo, ResourceDefinition resource, double mass, ObjectInfo source = null)` appends a normal resource cargo row.

`SetResourceCargo(CargoAll cargo, ResourceDefinition resource, double mass, ObjectInfo source = null)` finds or creates a normal resource cargo row and sets its mass.

`AddOrIncreaseResourceCargo(CargoAll cargo, ResourceDefinition resource, double amount, ObjectInfo source = null)` finds or creates a normal resource cargo row and increases its mass.

`ReduceNonFuelResourceCargo(CargoAll cargo, ResourceDefinition fuelResource, double amountToRemove)` removes regular non-fuel resource cargo from the normal cargo list.

- Mutates: supplied `CargoAll`.
- Stock calls: no except `NormalizeCargo`.
- Requires loaded game: yes if normalization must create fuel cargo.

### Fuel Helpers

Read-only helpers:

```csharp
GetRequiredFuel(PMMissionParameter parameter)
GetOptimalFuel(PMMissionParameter parameter)
GetLoadedFuel(PMMissionParameter parameter)
GetPotentialFuel(PMMissionParameter parameter)
GetFuelShortfall(PMMissionParameter parameter)
GetFuelResource()
```

Mutation helpers:

```csharp
SetLoadedFuel(PMMissionParameter parameter, double amount)
SetPotentialFuel(PMMissionParameter parameter, double amount)
ConfigureReservePropellant(PMMissionParameter parameter, ResourceDefinition fuelResource, double targetPropellant, bool disableReduceFuelToMinimum = true)
EnsureMinimumFuel(PMMissionParameter parameter, double amount)
CapFuelToPotential(PMMissionParameter parameter)
StageFuelAsCargo(PMMissionParameter parameter, double amount)
```

- Mutates: `parameter.CargoAll.cargoFuel` or `parameter.CargoAll.listCargo`.
- Stock calls: fuel resource lookup for `GetFuelResource`/fuel creation.
- Requires loaded game: yes.
- Caution: these helpers do not rerun stock planner validation automatically.

### Crew, Supply, Life Support

`ConvertSupplyToLifeSupport` and `ConvertLifeSupportToSupply` use `GameManager.Instance.Economic.SupplyToLifeSupportMultiplayer`.

- Mutates: no.
- Stock calls: `GameManager.Economic`.
- Requires loaded game: yes for meaningful values.

### Resource Availability

`CheckCargoAvailable(ObjectInfo source, Company company, CargoAll cargo)` wraps `source.GetObjectInfoData(company).CheckResources(cargo)`.

`GetAvailableResource(ObjectInfo source, Company company, ResourceDefinition resource)` wraps `ObjectInfoData.CheckResources(resource)`.

`GetResourceShortfalls(ObjectInfo source, Company company, CargoAll cargo)` compares cargo resource demand to source availability.

- Mutates: no intended mutation.
- Stock calls: `ObjectInfo.GetObjectInfoData`, `ObjectInfoData.CheckResources`.
- Requires loaded game: yes.
- Caution: demand is summed across cargo lists and fuel cargo. It does not apply stock buy rules.

### Vehicle Payload

```csharp
GetSpacecraftDryMass(ISpacecraftInfo spacecraft)
GetSpacecraftCargoCapacity(ISpacecraftInfo spacecraft, Company company)
GetLaunchVehiclePayload(ILaunchVehicleInfo launchVehicle, ObjectInfo start, Company company)
CheckLaunchVehiclePayload(ILaunchVehicleInfo launchVehicle, CargoAll cargo, ISpacecraftInfo spacecraft, ObjectInfo start, Company company)
```

- Mutates: no.
- Stock calls: `ISpacecraftInfo.GetMass`, `SpacecraftType.GetCargoCapacity`, `LaunchVehicleType.MaxPayloadOnThisObject`, `LaunchVehicleType.CheckMaximumPayload`.
- Requires loaded game: yes.

### Cycle Cargo Event

`CargoCreatedForCycle` is raised after stock `ObjectInfoData.CreatedCargoToTakeNormal` creates cargo for a cyclical mission planning attempt.

- Mutates: no SDK mutation; subscribers may mutate their own diagnostics/state.
- Stock calls: no direct SDK stock calls in the event dispatcher.
- Requires loaded game: yes, because it is raised from live stock cycle planning.
- Use when: a mod needs cargo-created diagnostics or wants to inspect stock cargo generation without patching `ObjectInfoData`.

## `SolarSdk.MissionPlanning`

Low-level event service raised by SDK Harmony patches. Use this when a mod needs to observe or influence stock planner timing rather than create a draft.

Events:

```csharp
BeforeCodeJobPlan
BeforeFastestSearch
AfterFastestSearch
BeforeCreateFly
AfterCreateFly
SuppressPreviewTrajectory
MissionCompleted
CheckSelfLaunchOverride
SuppressArrivalNotification
MissionDisplayNameOverride
```

Important behavior:

- `BeforeCodeJobPlan` is raised from `GameManager.SetPMParameterForCodeJobSystem` prefix.
- `BeforeFastestSearch` and `AfterFastestSearch` are raised around `PMTabSchedule.ButtonFastestClickButton`.
- `ApplyCodeFastestDeltaVCorrection` fixes stock ForCode fastest-search delta-V initialization.
- `BeforeCreateFly` can suppress stock launch creation for invalid tagged plans.
- `SuppressPreviewTrajectory` can skip stock preview trajectory creation.
- `MissionCompleted` runs after `MissionInfo.Complete`.
- `CheckSelfLaunchOverride` can replace `PMMissionParameter.CheckLVFullListOrNone` result.
- `SuppressArrivalNotification` can skip stock spacecraft notification methods.
- Handler exceptions are caught and logged; they also trigger rate-limited diagnostics snapshots.

## `SolarSdk.MissionTags`

Tagged mission name correlation and display preservation. This service is intentionally generic; it does not know about LogisticsMod beyond the prefix/resolver registered by LogisticsModSdk.

### `RegisterMissionPrefix(string prefix)`

Registers a mission-name prefix such as `[LOGI`.

- Mutates: SDK prefix list.
- Stock calls: no.

### `RegisterNameResolver(string ownerId, Func<SdkMissionNameContext, string> resolver)`

Registers or replaces one resolver for `ownerId`. Resolvers map stock mission contexts to display names.

- Mutates: SDK resolver list.
- Stock calls: no directly.
- Failure behavior: resolver exceptions are caught, logged, and rate-limited snapshots are written.

### `ResolveName(...)`

Overloads resolve by `PMMissionParameter`, `MissionInfo`, or explicit `SdkMissionNameContext`.

- Mutates: no.
- Stock calls: no direct stock method calls, but resolver code may call stock APIs.

### `ApplyMissionParameterName(PMMissionParameter parameter, string context)`

Resolves a tagged name and calls `parameter.ChangeMissionName(name, _manualChangeName: true)`.

- Mutates: stock `PMMissionParameter`.
- Stock calls: `PMMissionParameter.ChangeMissionName`.

### `ApplyMissionInfoName(MissionInfo missionInfo, string name, string context)`

Sets `missionInfo.missionName`, marks `fromCyclicalMission`, registers dispatch correlation when available, and raises `MissionInfoNameApplied`.

- Mutates: stock `MissionInfo` and SDK cycle tracker.
- Stock calls: no stock method call; direct field/property writes.

## `SolarSdk.CyclicalMissions`

Dispatch and cycle correlation ledger. It tracks how stock `CycleMissionsData`, `PMMissionParameter`, carrier `Spacecraft`, and `MissionInfo` relate to an SDK dispatch ID.

Common calls:

```csharp
CreateDispatchId(ownerTag)
RegisterPlannedCycle(dispatchId, ownerTag, cycle, primaryShip, routeSummary)
RegisterMissionParameter(dispatchId, parameter, context)
RegisterCarrier(dispatchId, carrier, context)
RegisterMissionInfo(dispatchId, missionInfo)
MarkCodeJobStarted / Completed / Failed
FindDispatchId(...)
HandOffToStockPlanner(spacecraft, cycle, context, afterPlanned, onNotStarted)
CreateCycle(draft)
CreateAndAddCycle(draft, primaryShip, ownerTag, reservationOwnerId, context)
AddAndRegisterCycle(cycle, primaryShip, spacecraft, ownerTag, reservationOwnerId, context)
CreateResourceCount(...)
CreateResourceCountFromCargo(...)
CheckCycleReplan
CyclePlanNotification
UnregisterCycle(...)
GetTrackersSnapshot()
ClearOwner(ownerTag)
```

- Mutates: SDK tracker dictionaries; cycle creation helpers allocate stock `CycleMissionsData`; add/register helpers mutate `CycleMissionManager` and optional fleet ledgers.
- Stock calls: `CycleMissionManager.GetAllCycleMission`, `CycleMissionManager.AddCycleMission`, `CycleMissionsData` constructor.
- Requires loaded game: query and create/add helpers require managers and loaded object data; pure registration helpers do not.
- Caution: SDK creates/adds stock cycle objects, but consumer mods still own route, cargo, and cleanup policy.

### `HandOffToStockPlanner(...)`

Sets up or creates a `SpaceCraftCyclicalMissionController` on the supplied spacecraft, calls `SetSC`, resets `CycleMissionPlanFlyWas`, and invokes stock `TryPlanCycleMission`. It registers carrier and mission-parameter correlation, marks code-job start/completion/failure, detects planner-not-started cases, and returns `SdkCycleHandoffResult`.

- Mutates: stock spacecraft game object may receive a controller component; stock controller and cycle planning flags are mutated; SDK dispatch tracker is mutated.
- Stock calls: `SpaceCraftCyclicalMissionController.SetSC`, `TryPlanCycleMission`.
- Requires loaded game: yes.
- Use when: a mod has already added a `CycleMissionsData` and wants stock to plan it without duplicating controller boilerplate.
- Caution: callbacks are policy hooks. LogisticsModSdk uses them to release route locks and remove one-shot cycles; the SDK does not decide whether a logistics request is fulfilled.

### `CheckCycleReplan`

Event raised from the SDK prefix on `SpaceCraftCyclicalMissionController.TryPlanCycleMission`.

- Mutates: no direct mutation unless subscribers mutate the context or stock objects.
- Stock calls: no.
- Requires loaded game: yes, because the stock controller is live.
- Return behavior: subscribers return `true` to suppress the stock planning call, `false` to explicitly allow it, or `null` when they do not own the cycle.
- Use when: a mod owns tagged cycles and needs to suppress duplicate replans after stock has already scheduled a mission.

### `CyclePlanNotification`

Event raised from the SDK prefix on `SpaceCraftCyclicalMissionController.ShowNotification`.

- Mutates: SDK dispatch tracker when `FailureReason` is set; subscribers may mutate mod state.
- Stock calls: no direct SDK stock validation, but subscribers often inspect `PMMissionParameter.CheckCanPlanMission` or `CheckScheduleFly`.
- Requires loaded game: yes.
- Use when: a mod wants to translate cycle planner failures, annotate request/ship status, or suppress repeat stock notifications.
- Caution: set `SuppressNotification = true` to skip stock notification display. Leave it false when the first stock notification should still appear.

## `SolarSdk.Fleet`

Fleet query, real-spacecraft reservation, and synthetic-carrier tracking service.

Real reservations:

```csharp
ReserveSpacecraft
ReleaseSpacecraft
GetReservation
IsReserved
ClearReservations
GetReservationsSnapshot
```

Synthetic carrier tracking:

```csharp
TrackSyntheticCarrier
ReleaseSyntheticCarrier
HasSyntheticCarrier
GetSyntheticCarrierSnapshot
```

- Mutates: SDK reservation ledgers only.
- Stock calls: fleet query helpers read `GameManager.Player` and spacecraft state.
- Requires loaded game: query helpers require game managers; reservation helpers can be called whenever IDs are known.
- Caution: real reservations are keyed by positive ship ID. Synthetic carriers are tracked separately by dispatch/runtime identity and must never be treated as real reservable spacecraft.

## `SolarSdk.Market`

Market offer notification policy service.

```csharp
ShouldSuppressOfferNotification += ctx => true;
```

- Mutates: only the `suppresssNotification` argument passed to stock `MarketOfferManager.AddOffer`.
- Stock calls: no direct stock calls; subscribers inspect the stock `Offer`.
- Requires loaded game: yes, because offers refer to loaded object/resource state.
- Use when: a mod wants to hide stock offer notifications while still allowing the offer to be created.

## `SolarSdk.Diagnostics`

Manual and rate-limited JSON snapshots.

```csharp
RegisterSnapshotProvider(name, Func<object>)
WriteSnapshot(reason)
WriteSnapshotOnce(reason, key = null, cooldownSeconds = 300)
```

- Mutates: writes files under `BepInEx\SolarExpanseSdk\Diagnostics`.
- Stock calls: only what registered providers call.
- Requires loaded game: providers may require game state.
- Filename behavior: snapshot names include UTC milliseconds plus the reason and automatic key when supplied, so multiple snapshots in the same second do not overwrite each other.

## `SolarSdk.Events`

Lifecycle and UI events raised by SDK patches:

```csharp
SaveLoading
SaveLoaded
BeforeSave
AfterSave
DayTick
PostLoadFirstTick
ObjectInfoWindowReady
ObjectInfoChanged
ObjectInfoRebuild
```

- Mutates: no direct mutation; subscribers can mutate.
- Failure behavior: subscriber exceptions are caught, logged, and trigger rate-limited snapshots.
- High-volume event logs are throttled by `SdkLogging`.

## Logging Channels

Use these channel names with `SolarSdk.Log.Verbose(channel, message)`:

```text
sdk.lifecycle
sdk.save
sdk.objectInfoUi
sdk.fleet
sdk.cycles
sdk.missionPlanning
sdk.patches
sdk.events
```

Verbose logging is off by default. Warnings and errors are always emitted.
