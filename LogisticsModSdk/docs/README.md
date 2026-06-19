# LogisticsMod

Automated inter-body resource logistics for Solar Expanse. Adds a **Logistics** tab to the Object Info window where players configure import (GET) and export (SEND) rules, assign spacecraft and launch vehicle quotas, and let the mod handle mission planning, dispatch, and return trips automatically.

## Requirements

- **Solar Expanse** (Steam)
- **BepInEx 5.4.23.5** (net471)

## Installation

Copy the built `LogisticsMod.dll` into:

```
<Steam>/steamapps/common/Solar Expanse/BepInEx/plugins/logisticsmod/
```

Configuration lives alongside the DLL at:

```
<Steam>/steamapps/common/Solar Expanse/BepInEx/plugins/logisticsmod/LogisticsMod.cfg
```

## Build

```
dotnet build LogisticsMod.csproj -c Release
```

Output deploys directly to the BepInEx plugins folder via the project's post-build target. A copy is also placed at the repository root (`Documents/SolarExpanseMods/LogisticsMod.dll`).

## Project Structure

```
LogisticsMod/
  Plugin.cs                          Entry point, config bindings, Harmony patching
  Data/
    LogisticsTypes.cs                Data model: requests, providers, quotas, enums
    LogisticsNetwork.cs              In-memory rule store and query helpers
    LogisticsPersistence.cs          JSON save/load to BepInEx/saves/<saveName>/
  Logic/
    LogisticsObserver.cs             Core planner: daily tick, routing, dispatch, return
    LogisticsStrings.cs              Localization-ready user-facing string templates
  Patches/
    ObjectInfoWindowPatches.cs       UI injection, SetData sync, stock row markers
    SpaceCraftCyclicalMissionControllerPatches.cs
                                     Mission planning, naming, cargo, trajectory patches
    SaveLoadPatches.cs               Save/load hooks, state reset, post-load reconciliation
    TimeControllerPatches.cs         Daily tick subscription, post-load trigger
  UI/
    LogisticsUI.cs                   Main UI: sections, buttons, pickers, amount input
    LogisticsSection.cs              Collapsible section component with stock-style headers
  docs/
    README.md                        This file
    ARCHITECTURE.md                  System architecture and data flow
    CONFIGURATION.md                 All config entries and their effects
    UI_GUIDE.md                      UI features and player-facing behavior
    CHANGELOG.md                     Consolidated changelog
```
