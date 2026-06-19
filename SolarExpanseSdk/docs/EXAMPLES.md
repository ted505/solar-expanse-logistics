# SDK Examples

These examples show the intended way to use the SDK from another BepInEx mod. They favor small, explicit calls over hiding stock game behavior. If a call mutates `PMMissionParameter`, `CargoAll`, or mission state, the example says so.

## Minimal Plugin Setup

Reference `SolarExpanseSdk.dll`, add a BepInEx dependency on `com.solarexpanse.sdk`, then use `SolarSdk` after your plugin starts.

```csharp
using BepInEx;
using SolarExpanseSdk.Core;

[BepInPlugin("com.example.mod", "Example Mod", "1.0.0")]
[BepInDependency("com.solarexpanse.sdk")]
public sealed class ExamplePlugin : BaseUnityPlugin
{
    private void Awake()
    {
        SolarSdk.Log.Info("[Example] loaded");
        SolarSdk.Diagnostics.RegisterSnapshotProvider("Example", BuildSnapshot);
    }

    private object BuildSnapshot()
    {
        return new
        {
            reservations = SolarSdk.Fleet.GetReservationsSnapshot().Count,
            dispatches = SolarSdk.CyclicalMissions.GetTrackersSnapshot().Count
        };
    }
}
```

## Create And Validate A Mission Draft

`CreateDraft` creates an empty SDK draft with a stock `CargoAll.CreateCargoEmpty()` cargo object. `ToMissionParameter` mutates a new `PMMissionParameter` by calling stock `SetTab*`, `SetFuelNeed`, and related setters.

```csharp
var draft = SolarSdk.Missions.CreateDraft("example");
draft.Company = SolarSdk.Fleet.PlayerCompany;
draft.Start = sourceObjectInfo;
draft.Target = targetObjectInfo;
draft.Spacecraft = spacecraft;
draft.CargoAll = SolarSdk.MissionLoadout.CreateEmptyCargo();
draft.MissionName = "[EXAMPLE] supply run";

var validation = SolarSdk.Missions.Validate(draft);
if (!validation.Valid)
{
    foreach (var issue in validation.Issues)
        SolarSdk.Log.Warning("sdk.missionPlanning", $"{issue.Kind}: {issue.Message}");
    return;
}

var parameter = SolarSdk.Missions.ToMissionParameter(draft);
```

Validation runs structural SDK checks first. By default it also calls stock `CheckScheduleFly()` and `CheckCanPlanMission()`, so call it only when the relevant planner/game state is available.

## Validate An Existing Stock Parameter

Use this inside planner hooks or code-job callbacks when the game has already built the parameter.

```csharp
var result = SolarSdk.Missions.Validate(parameter);
if (!result.Valid && SolarSdk.Missions.IsRetryable(result))
{
    SolarSdk.Diagnostics.WriteSnapshot("mission-validation-retryable");
}
```

`SolarSdk.Missions.Explain(stockResult)` translates a raw `PMMissionParameter.EPlanMissionResult` flag set into SDK failure issues without rerunning stock validation.

## Clone And Adjust Cargo

Use clone helpers when you need to inspect or modify a candidate loadout without mutating the UI or an active mission parameter.

```csharp
var cargo = SolarSdk.MissionLoadout.CloneCargo(parameter.CargoAll);
SolarSdk.MissionLoadout.NormalizeCargo(cargo);
SolarSdk.MissionLoadout.SetResourceCargo(cargo, resourceDefinition, 25.0, parameter.Start);

var shortfalls = SolarSdk.MissionLoadout.GetResourceShortfalls(parameter.Start, parameter.FlyCompany, cargo);
if (shortfalls.Count > 0)
{
    foreach (var shortfall in shortfalls)
        SolarSdk.Log.Verbose("sdk.missionPlanning", $"cargo-shortfall resource={shortfall.Resource?.ID} shortfall={shortfall.Shortfall:0.##}");
}
```

`SetResourceCargo`, `AddResourceCargo`, `SetLoadedFuel`, and `NormalizeCargo` mutate the supplied `CargoAll`.

## Fuel Helpers

Fuel in stock mission planning is split between mission requirements on `PMMissionParameter` and the special `CargoAll.cargoFuel` item.

```csharp
var requiredFuel = SolarSdk.MissionLoadout.GetRequiredFuel(parameter);
var loadedFuel = SolarSdk.MissionLoadout.GetLoadedFuel(parameter);

if (loadedFuel < requiredFuel)
{
    SolarSdk.MissionLoadout.SetLoadedFuel(parameter, requiredFuel);
    SolarSdk.MissionLoadout.CapFuelToPotential(parameter);
}
```

`SetLoadedFuel` and `CapFuelToPotential` mutate `parameter.CargoAll.cargoFuel`. They do not buy or remove resources from an object. Stock resource removal happens later in the `PMTabSchedule.CreateFly` flow.

## Payload Checks

Use payload helpers before creating launch-vehicle or synthetic-carrier missions.

```csharp
var payloadOk = SolarSdk.MissionLoadout.CheckLaunchVehiclePayload(
    launchVehicle,
    parameter.CargoAll,
    parameter.SC,
    parameter.Start,
    parameter.FlyCompany);

if (!payloadOk)
{
    SolarSdk.Log.Warning("sdk.missionPlanning", "Launch vehicle payload check failed.");
    return;
}
```

The SDK first checks `LaunchVehicleType.MaxPayloadOnThisObject(start, company)` when available, then falls back to stock `LaunchVehicleType.CheckMaximumPayload(cargo, spacecraft)`.

## Dispatch IDs And Cycles

Dispatch IDs let logs, snapshots, cycle records, code-job callbacks, mission info, and fleet reservations all refer to the same attempted mission.

```csharp
var dispatchId = SolarSdk.CyclicalMissions.CreateDispatchId("example");

CycleMissionManager.Instance.AddCycleMission(cycleData, company);
SolarSdk.CyclicalMissions.RegisterPlannedCycle(
    dispatchId,
    "example",
    cycleData,
    spacecraft,
    $"{cycleData.A?.ObjectName}->{cycleData.B?.ObjectName}");

SolarSdk.Fleet.ReserveSpacecraft(
    spacecraft.ID,
    "example",
    "cycle-dispatch",
    dispatchId,
    cycleData.A?.id ?? -1,
    cycleData.B?.id ?? -1);
```

If the stock planner later exposes a `PMMissionParameter`, register it immediately:

```csharp
SolarSdk.CyclicalMissions.RegisterMissionParameter(dispatchId, parameter, "code-job-callback");
```

## Synthetic Carriers

Surface-to-orbit logistics uses fake spacecraft such as an orbital payload container. Track those separately from real fleet reservations.

```csharp
SolarSdk.CyclicalMissions.RegisterCarrier(dispatchId, syntheticCarrier, "lv-container");
SolarSdk.Fleet.TrackSyntheticCarrier(
    dispatchId,
    "logistics",
    syntheticCarrier,
    "surface-to-orbit-container",
    sourceObjectInfo.id,
    targetObjectInfo.id);
```

Synthetic carriers are keyed by dispatch ID and Unity instance identity. Do not reserve them through `ReserveSpacecraft`, because negative IDs are not durable real fleet IDs.

## Mission Naming

Register a prefix for quick detection, then register a resolver for names that need to survive stock mission creation.

```csharp
SolarSdk.MissionTags.RegisterMissionPrefix("[EXAMPLE]");
SolarSdk.MissionTags.RegisterNameResolver("example", context =>
{
    if (context.ExistingName != null && context.ExistingName.StartsWith("[EXAMPLE]"))
        return context.ExistingName;
    return null;
});
```

The SDK applies resolved names in mission-planning patches and re-registers created `MissionInfo` objects with the cycle tracker when a dispatch ID can be resolved.

## Manual Snapshot

Snapshots are JSON files under `BepInEx\SolarExpanseSdk\Diagnostics`.

```csharp
SolarSdk.Diagnostics.WriteSnapshot("example-debug");
```

Snapshots include SDK patch/capability state, dispatch trackers, real reservations, synthetic carriers, and any mod-provided snapshot providers.

