# Mission Planner SDK Surface

This document is the working checklist for turning Solar Expanse mission planning into SDK-owned APIs. The goal is not to replace the stock planner. The goal is to give mods stable, observable calls for every subsystem the stock planner mutates: mission drafts, validation, route selection, fuel, cargo, crew, supply, resource transactions, vehicles, trajectory creation, mission info, cyclical missions, UI display, notifications, and diagnostics.

## Design Rule

Consumer mods should express mission intent through SDK objects, then let the SDK convert that intent into stock `PMMissionParameter`, `CargoAll`, code-job, and `MissionInfo` calls.

The SDK should keep policy out of the core. LogisticsModSdk can decide what to ship and when. FleetManager or Teddit AI can decide which vehicle to use. The SDK should provide the safe calls, validation, normalization, event hooks, and diagnostics.

## Service Layout

| Service | Purpose |
| --- | --- |
| `SolarSdk.Missions` | Drafts, validation, queueing, mission creation, stock result translation, and mission lifecycle observation. |
| `SolarSdk.MissionLoadout` | Cargo, fuel, supply, crew, resource transaction, and payload helper APIs. |
| `SolarSdk.MissionPlanning` | Low-level event hooks around stock planner surfaces. Already exists; expand rather than duplicate. |
| `SolarSdk.MissionTags` | Tagged mission name/display correlation. Already exists. |
| `SolarSdk.CyclicalMissions` | Dispatch/cycle correlation. Already exists. |
| `SolarSdk.Fleet` | Real reservation and synthetic-carrier tracking. Already exists. |

## Status Legend

- `Implemented`: the API exists in source and is documented in `API_REFERENCE.md`.
- `Partial`: a first useful slice exists, but this document lists additional planned calls.
- `Planned`: roadmap only. Do not call these APIs until they are added to source.

## Implementation Order

### 1. Mission Drafts - Partial

Public calls:

```csharp
SolarSdk.Missions.CreateDraft(ownerId)
SolarSdk.Missions.FromParameter(ownerId, PMMissionParameter parameter)
SolarSdk.Missions.FromCycle(ownerId, CycleMissionsData cycle, Spacecraft carrier = null) // Planned
SolarSdk.Missions.ToMissionParameter(SdkMissionDraft draft)
SolarSdk.Missions.CloneParameter(PMMissionParameter parameter)
```

`SdkMissionDraft` fields:

```csharp
OwnerId
DispatchId
MissionName
Company
Start
Target
Spacecraft
SpacecraftList
LaunchVehicle
LaunchVehicleList
CargoAll
MissionCreator
CostType
MissionId
DepartureTime
ArrivalTime
DeltaV1
DeltaV2
AllFuelNeed
OptimalFuelNeed
LeftOverFuel
MinFuelCost
FlightCost
LaunchVehicleFuelNeed
LoadingFromSave
LoadingFromSaveAndLaunch
ForCyclicalMission
Fast
AllowSyntheticCarrier
Tags
Metadata
```

Stock surfaces:

- `PMMissionParameter.SetCompany`
- `PMMissionParameter.SetTabDestination`
- `PMMissionParameter.SetTabSC`
- `PMMissionParameter.SetTabLV`
- `PMMissionParameter.SetTabCargo`
- `PMMissionParameter.ChangeMissionName`
- `PMMissionParameter.SetMissionOrigin`
- `PMMissionParameter.SetCostType`
- `PMMissionParameter.SetMissionID`
- `PMMissionParameter.SetTabDateFromPorkchope`
- `PMMissionParameter.SetDeltaV`
- `PMMissionParameter.SetFuelNeed`
- `PMMissionParameter.SetOptimalFuelNeed`

### 2. Validation - Partial

Public calls:

```csharp
SolarSdk.Missions.Validate(SdkMissionDraft draft, SdkMissionValidationOptions options = null)
SolarSdk.Missions.Validate(PMMissionParameter parameter, SdkMissionValidationOptions options = null)
SolarSdk.Missions.Explain(PMMissionParameter.EPlanMissionResult result)
SolarSdk.Missions.TranslateFailureKind(PMMissionParameter.EPlanMissionResult result)
SolarSdk.Missions.IsRetryable(SdkMissionValidationResult result)
SolarSdk.Missions.RequiresPlayerAction(SdkMissionValidationResult result)
```

Subsystem checks:

- Null company/start/target.
- Null spacecraft.
- Non-player or foreign-company spacecraft.
- Real spacecraft busy phase.
- Synthetic carriers such as LV payload containers.
- Missing LV when stock requires LV.
- Invalid LV payload capacity.
- Invalid or null cargo.
- Fuel resource missing from cargo.
- Negative cargo/fuel/supply quantities.
- Stock `CheckScheduleFly`.
- Stock `CheckCanPlanMission`.
- `PMMissionParameter.EPlanMissionResult` flags:
  - `WrongSC`
  - `WrongLV`
  - `NoFuelCantBuy`
  - `WrongThrust`
  - `WrongLifeSupport`
  - `WrongResourcesCargoStartHaveResource`
  - `WrongResourcesCargoLoadLimit`
  - `WrongRemoveFuel`
  - `WrongMaxCapacityFuelOk`
  - `WrongScNoLVFuelOk`
  - `WrongSolarDistanceOk`
  - `WrongCheckLvOk`
  - `WrongAsteroidImpactEndGameOK`
  - `WrongTransferLambertOK`

### 3. Loadout: Cargo - Partial

Public calls:

```csharp
SolarSdk.MissionLoadout.CreateEmptyCargo()
SolarSdk.MissionLoadout.CloneCargo(CargoAll cargo)
SolarSdk.MissionLoadout.CloneCargoItem(Cargo cargo, CargoAll owner)
SolarSdk.MissionLoadout.NormalizeCargo(CargoAll cargo)
SolarSdk.MissionLoadout.GetCargoMass(CargoAll cargo)
SolarSdk.MissionLoadout.GetCargoFuelMass(CargoAll cargo)
SolarSdk.MissionLoadout.GetCargoLifeSupport(CargoAll cargo)
SolarSdk.MissionLoadout.GetCrewCount(CargoAll cargo)
SolarSdk.MissionLoadout.GetResourceMass(CargoAll cargo, ResourceDefinition resource)
SolarSdk.MissionLoadout.AddResourceCargo(CargoAll cargo, ResourceDefinition resource, double mass, ObjectInfo source = null)
SolarSdk.MissionLoadout.SetResourceCargo(CargoAll cargo, ResourceDefinition resource, double mass, ObjectInfo source = null)
SolarSdk.MissionLoadout.RemoveInvalidCargo(CargoAll cargo)
SolarSdk.MissionLoadout.CapCargoToMass(CargoAll cargo, double maxMass)
```

Stock surfaces:

- `CargoAll.CreateCargoEmpty`
- `CargoAll.CargoCurrent`
- `CargoAll.CargoCurrentFuel`
- `CargoAll.HowMuchCrew`
- `CargoAll.GetLifeSupport`
- `CargoAll.GetSupplyFromCargo`
- `CargoAll.GetLifeSupportFromCargoSupply`
- `CargoAll.CheckResourcesInCargo`
- `CargoAll.ChangeResourcesMassToLimit`
- `Cargo.SetValueCargoMass`

### 4. Loadout: Fuel - Partial

Public calls:

```csharp
SolarSdk.MissionLoadout.GetRequiredFuel(PMMissionParameter parameter)
SolarSdk.MissionLoadout.GetOptimalFuel(PMMissionParameter parameter)
SolarSdk.MissionLoadout.GetLoadedFuel(PMMissionParameter parameter)
SolarSdk.MissionLoadout.GetPotentialFuel(PMMissionParameter parameter)
SolarSdk.MissionLoadout.SetLoadedFuel(PMMissionParameter parameter, double amount)
SolarSdk.MissionLoadout.SetPotentialFuel(PMMissionParameter parameter, double amount)
SolarSdk.MissionLoadout.EnsureMinimumFuel(PMMissionParameter parameter, double amount)
SolarSdk.MissionLoadout.CapFuelToPotential(PMMissionParameter parameter)
SolarSdk.MissionLoadout.GetFuelShortfall(PMMissionParameter parameter)
SolarSdk.MissionLoadout.EstimateReturnFuel(PMMissionParameter outbound, ObjectInfo returnTarget) // Planned
SolarSdk.MissionLoadout.StageFuelAsCargo(PMMissionParameter parameter, double amount)
```

Stock surfaces:

- `PMMissionParameter.AllFuelNeed`
- `PMMissionParameter.OptimalFuelNeed`
- `PMMissionParameter.FuelValueToRemove`
- `PMMissionParameter.LeftOverFuel`
- `PMMissionParameter.MINFuelCost`
- `PMMissionParameter.CargoAll.cargoFuel`
- `PMMissionParameter.CheckCanPlanMission`
- `ObjectInfoData.CheckResources`

### 5. Loadout: Crew, Supply, Life Support - Partial

Public calls:

```csharp
SolarSdk.MissionLoadout.GetCrewCount(CargoAll cargo)
SolarSdk.MissionLoadout.GetSupplyMass(CargoAll cargo)
SolarSdk.MissionLoadout.GetLifeSupportValue(CargoAll cargo) // Implemented as GetCargoLifeSupport
SolarSdk.MissionLoadout.ConvertSupplyToLifeSupport(double supplyMass)
SolarSdk.MissionLoadout.ConvertLifeSupportToSupply(double lifeSupport)
SolarSdk.MissionLoadout.EnsureSupplyForMission(PMMissionParameter parameter, TimeSpan duration) // Planned
SolarSdk.MissionLoadout.RemoveSupplyUsedInFlight(CargoAll cargo, double lifeSupport) // Planned
```

Stock surfaces:

- `CargoAll.HowMuchCrew`
- `CargoAll.GetSupplyFromCargo`
- `CargoAll.GetLifeSupport`
- `CargoAll.SetMassUseInFlyCargoSupply`
- `CargoAll.RemoveLifeSupportFromCargoSupply`
- `GameManager.Economic.SupplyToLifeSupportMultiplayer`

### 6. Resource Availability And Transactions - Partial

Public calls:

```csharp
SolarSdk.MissionLoadout.CheckCargoAvailable(ObjectInfo source, Company company, CargoAll cargo)
SolarSdk.MissionLoadout.GetAvailableResource(ObjectInfo source, Company company, ResourceDefinition resource)
SolarSdk.MissionLoadout.GetResourceShortfalls(ObjectInfo source, Company company, CargoAll cargo)
SolarSdk.MissionLoadout.BuildRemovalTransactions(PMMissionParameter parameter) // Planned
SolarSdk.MissionLoadout.DescribeTransactions(ObjectInfoData.TransactionRemoveResource[] transactions) // Planned
```

Stock surfaces:

- `ObjectInfo.GetObjectInfoData(company)`
- `ObjectInfoData.CheckResources(CargoAll)`
- `ObjectInfoData.CheckResources(ResourceDefinition)`
- `PMMissionParameter.Transaction`
- `PMTabSchedule.CreateFly` removal flow.

### 7. Vehicles And Payload - Partial

Public calls:

```csharp
SolarSdk.Missions.ValidateSpacecraft(PMMissionParameter parameter) // Planned
SolarSdk.Missions.ValidateLaunchVehicle(PMMissionParameter parameter) // Planned
SolarSdk.MissionLoadout.GetSpacecraftDryMass(ISpacecraftInfo spacecraft)
SolarSdk.MissionLoadout.GetSpacecraftCargoCapacity(ISpacecraftInfo spacecraft, Company company)
SolarSdk.MissionLoadout.GetLaunchVehiclePayload(ILaunchVehicleInfo launchVehicle, ObjectInfo start, Company company)
SolarSdk.MissionLoadout.CheckLaunchVehiclePayload(ILaunchVehicleInfo launchVehicle, CargoAll cargo, ISpacecraftInfo spacecraft, ObjectInfo start, Company company)
SolarSdk.Missions.RequiresLaunchVehicle(PMMissionParameter parameter) // Planned
```

Stock surfaces:

- `ISpacecraftInfo.GetTypeSpaceCraft`
- `ISpacecraftInfo.GetMass`
- `SpacecraftType.GetCargoCapacity`
- `ILaunchVehicleInfo.GetLaunchVehicleType`
- `LaunchVehicleType.MaxPayloadOnThisObject`
- `LaunchVehicleType.CheckMaximumPayload`
- `PMMissionParameter.CheckLVFullListOrNone`

### 8. Route And Trajectory - Planned

Public calls:

```csharp
SolarSdk.Missions.ApplyFastestRoute(PMMissionParameter parameter)
SolarSdk.Missions.ApplyOptimalRoute(PMMissionParameter parameter)
SolarSdk.Missions.GetRouteSummary(PMMissionParameter parameter)
SolarSdk.Missions.GetRouteKey(PMMissionParameter parameter)
SolarSdk.Missions.CachePrecalculateData(string routeKey, PMMissionParameter.PrecalculateDataToShortFly data)
SolarSdk.Missions.TryGetPrecalculateData(string routeKey, out PMMissionParameter.PrecalculateDataToShortFly data)
SolarSdk.Missions.ClonePrecalculateData(PMMissionParameter.PrecalculateDataToShortFly data)
SolarSdk.Missions.SpawnTrajectory(PMMissionParameter parameter)
```

Stock surfaces:

- `PMTabSchedule.ButtonFastestClickButton`
- `PMTabSchedule.CreatedTrajectory`
- `PMTabSchedule.CreateFly`
- `TrajectoryManager.SpawnTrajectoryMission`
- `PMMissionParameter.SetTabDateFromPorkchope`
- `PMMissionParameter.SetDeltaV`
- `PMMissionParameter.SetMoonCase`

### 9. Code Jobs And Scheduling - Planned

Public calls:

```csharp
SolarSdk.Missions.QueueCodeJob(SdkMissionDraft draft, Action<SdkMissionPlanResult> callback = null)
SolarSdk.Missions.QueueCodeJob(PMMissionParameter parameter, SdkMissionContext context = null)
SolarSdk.Missions.TryGetQueuedPlan(dispatchId)
SolarSdk.Missions.CancelQueuedPlan(dispatchId)
SolarSdk.Missions.RegisterCodeJobCallback(dispatchId, Action callback)
```

Stock surfaces:

- `GameManager.SetPMParameterForCodeJobSystem`
- `GameManager.UpdateJobSystem`
- `PlanMissionWindow.SetPMParameterForCode`
- `PMTabSchedule.ActiveTabPublic`
- `PMTabSchedule.OnClickScheduleButtonForCode`

### 10. MissionInfo Creation And Persistence - Planned

Public calls:

```csharp
SolarSdk.Missions.CreateMissionInfo(SdkMissionDraft draft)
SolarSdk.Missions.CreateMissionInfo(PMMissionParameter parameter)
SolarSdk.Missions.RegisterCreatedMission(dispatchId, MissionInfo missionInfo)
SolarSdk.Missions.FindMissionInfo(dispatchId)
SolarSdk.Missions.RemoveMissionInfoSafe(MissionInfo missionInfo)
SolarSdk.Missions.CompleteMissionSafe(MissionInfo missionInfo)
```

Stock surfaces:

- `MissionInfoManager.CreateMissionInfo`
- `MissionInfoManager.AddMissionInfo`
- `MissionInfoManager.RemoveMissionInfo`
- `MissionInfo.Complete`
- `MissionInfoManager.AddPMMissionParameterDataSave`

### 11. Cyclical Mission Planning - Partial

Public calls:

```csharp
new SdkCycleDraft { ... }
SolarSdk.CyclicalMissions.CreateCycle(draft)
SolarSdk.CyclicalMissions.CreateAndAddCycle(draft, primaryShip, ownerTag, reservationOwnerId, context)
SolarSdk.CyclicalMissions.AddAndRegisterCycle(cycle, primaryShip, spacecraft, ownerTag, reservationOwnerId, context)
SolarSdk.CyclicalMissions.CreateResourceCount(resource, amount)
SolarSdk.CyclicalMissions.CreateResourceCountFromCargo(cargo, fallbackResource, fallbackAmount)
SolarSdk.CyclicalMissions.ReconcileCycles(...) // Implemented as LogReconcile plus tracker APIs
SolarSdk.CyclicalMissions.CreateDispatchId(ownerTag)
SolarSdk.CyclicalMissions.RegisterPlannedCycle(dispatchId, ownerTag, cycle, primaryShip, routeSummary)
SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, context)
SolarSdk.CyclicalMissions.RegisterCarrier(dispatchId, carrier, context)
SolarSdk.CyclicalMissions.RegisterMissionInfo(dispatchId, missionInfo)
SolarSdk.CyclicalMissions.MarkCodeJobStarted/Completed/Failed(dispatchId, ...)
SolarSdk.CyclicalMissions.FindDispatchId(...)
SolarSdk.CyclicalMissions.HandOffToStockPlanner(spacecraft, cycle, context, afterPlanned, onNotStarted)
SolarSdk.CyclicalMissions.CheckCycleReplan += ctx => ...
SolarSdk.CyclicalMissions.CyclePlanNotification += ctx => ...
```

Stock surfaces:

- `CycleMissionsData`
- `CycleMissionManager.AddCycleMission`
- `CycleMissionManager.GetAllCycleMission`
- `SpaceCraftCyclicalMissionController.TryPlanCycleMission`
- `SpaceCraftCyclicalMissionController.ShowNotification`

Current scope:

- Mods describe cycle intent with `SdkCycleDraft`; the SDK builds/adds/registers stock `CycleMissionsData`.
- SDK tracks dispatch IDs across cycles, carriers, mission parameters, and mission info.
- SDK owns controller handoff, duplicate replan suppression hooks, and cycle failure notification hooks.
- LogisticsModSdk still owns route selection, cargo manifest policy, cleanup policy, relay behavior, and return fuel behavior.

### 12. Display, Notifications, And UX - Partial

Public calls:

```csharp
SolarSdk.MissionTags.RegisterMissionPrefix(...)
SolarSdk.MissionTags.RegisterNameResolver(...)
SolarSdk.MissionPlanning.BeforeCodeJobPlan += ...
SolarSdk.MissionPlanning.BeforeCodeJobCallback += ...
SolarSdk.MissionPlanning.AfterCodeJobCallback += ...
SolarSdk.MissionPlanning.SuppressArrivalNotification += ...
SolarSdk.MissionPlanning.CheckSelfLaunchOverride += ...
SolarSdk.Missions.RegisterNotificationPolicy(...) // Planned
SolarSdk.Missions.SetMissionRowDecorator(...) // Planned
```

Stock surfaces:

- `PMMissionParameter.ChangeMissionName`
- `MissionInfoManager.CreateMissionInfo`
- `MissionsLabelsMainUI.SetData`
- `MissionRow.SetMissionInfo`
- `MissionRowNew.SetMissionInfo`
- `Spacecraft.ShowNotificationLand`
- `Spacecraft.ShowNotificationAsteroidImpact`
- `Spacecraft.ShowNotificationSolarSystem`
- `Spacecraft.ShowNotificationAsteroidPull`

Code-job callback order:

1. SDK prefix on `GameManager.SetPMParameterForCodeJobSystem` raises `BeforeCodeJobPlan`.
2. SDK wraps the stock callback action.
3. Wrapped callback raises `BeforeCodeJobCallback`.
4. Stock callback runs and creates/schedules the mission.
5. Wrapped callback raises `AfterCodeJobCallback`.

Use `BeforeCodeJobPlan` for mutable planner flags and dispatch registration, `BeforeCodeJobCallback` for last-second cargo/name adjustments, and `AfterCodeJobCallback` for caching generated planner data and diagnostics.

## Current Pass

This pass implements the first SDK slice:

1. `SolarSdk.Missions` service.
2. Mission draft creation and `PMMissionParameter` conversion.
3. Basic validation and stock planner result translation.
4. `SolarSdk.MissionLoadout` read/write helpers for cargo, fuel, crew, supply, resource availability, and payload checks.
5. SDK mission-planning hooks for code-job observation/callback preparation, fastest-search observation, self-launch override, arrival notification suppression, and mission display-name override.
6. LogisticsModSdk dispatch-boundary structural validation before direct, LV/container, and return-home stock cycle registration.
7. LogisticsModSdk cargo/fuel mechanics delegated to SDK loadout helpers where policy does not need to live in the mod.
8. SDK-owned cyclical mission handoff and notification/replan hooks, with LogisticsModSdk policy wired through SDK subscribers.

Later passes should migrate LogisticsModSdk behavior from the parity shim into these APIs in small pieces.
