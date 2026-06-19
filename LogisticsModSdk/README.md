# Logistics Tab - Solar Expanse Mod

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **Solar Expanse** that adds a Factorio-style logistics tab to object info windows. Configure bodies to **SEND** resources, configure other bodies to **GET** resources, and the mod plans recurring logistics shipments using your existing spacecraft, launch vehicles, orbital payload containers, and launch-assist infrastructure.

## Features

- **GET / SEND rules**: set target stockpiles (GET) and export reserves (SEND) per resource per body. The mod handles everything else.
- **One-shot requests**: dispatch a fixed total amount from the network without maintaining a standing order.
- **Reorder mode**: optional minimum threshold triggers resupply only when stock drops below a level, then fills to target.
- **Priority system**: Low / Normal / High / Critical. Higher priority requests are fulfilled first across all bodies.
- **Logistics networks**: isolate supply chains with numbered networks (1-10), keep routes local with **Local System** (same planet + orbit + moons), or leave on **Any** for global access.
- **Auto-buy**: automatically purchase from local market sell offers up to a configurable max price.
- **Auto-sell**: automatically sell surplus to local market buy offers, with continuous or per-month caps and minimum price floors.
- **Export-to-orbit**: surface providers can stage surplus to low orbit automatically for spacecraft pickup.
- **Spacecraft quotas**: assign ship types to logistics duty with configurable counts. Ships not assigned to logistics remain available to the vanilla planner.
- **Launch vehicle support**: toggle which LVs and launch-assist facilities (space elevators, magnetic rails, spin launchers) are available for logistics surface-to-orbit lifts.
- **Ranked route selection**: the planner scores all feasible routes (direct, surface launch, staged relay) and dispatches the best option.
- **Staged relay routing**: for surface bodies, cargo is automatically lifted to orbit via LV, then carried onward by spacecraft. Multiple ships can dispatch in parallel when enough staged stock is available.
- **Return fuel staging**: outbound manifests reserve cargo capacity for return trip fuel, with configurable safety margins. Fuel availability is checked at the ship's actual operating location (orbit for orbit-only ships).
- **Return-home cycles**: ships return to their source after delivery. Backhaul mode can carry resources on the return trip.
- **Per-ship status tracking**: expandable detail panels on GET, SEND, and spacecraft quota rows show each assigned ship's status (idle, in transit, pending, blocked) with colored indicators and arrival ETAs.
- **Informative blockers**: when a shipment can't be dispatched, the request shows the specific reason (insufficient fuel, no LV, no surplus, etc.) instead of a generic error.
- **Stock UI integration**: logistics ships are marked in stock spacecraft rows, mission names are preserved, and arrival/market notifications are suppressed to reduce spam.
- **Per-save persistence**: all rules and state save per save file. Runtime state resets cleanly on load.

## Installation

1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx) for Solar Expanse.
2. Copy the `logisticsmod` folder into `BepInEx/plugins/`.
3. Launch the game.
4. Open any object's info window. The logistics sections appear below the stock sections.

## Quick Start

1. On a source body, open the info window and expand **SEND**. Add an export rule for a resource and set a reserve amount.
2. Expand **SPACECRAFT** and assign a ship type quota.
3. If the source is a surface body, expand **LAUNCH VEHICLE** and enable an LV or launch-assist facility.
4. On the destination body, expand **GET** and add an import rule for the same resource.
5. The mod will begin planning and dispatching shipments on the next daily tick.

## UI Sections

| Section | Purpose |
| --- | --- |
| **GET** | Resources this body wants delivered. Shows status, blocker reasons, transit info, and per-ship details. |
| **SEND** | Resources this body exports. Configurable reserve, auto-sell, export-to-orbit, and per-ship details. |
| **SPACECRAFT** | Quotas for spacecraft types logistics may use. Expandable per-type panels with ship status. |
| **LAUNCH VEHICLE** | Toggle LVs and launch-assist infrastructure for surface-to-orbit staging. |

## Routing

The planner supports three route shapes:

1. **Direct spacecraft** delivery (orbit-to-orbit or self-launch capable ships).
2. **Surface launch** via LV + spacecraft/container to a destination.
3. **Staged relay**: surface -> orbit via LV, then orbit -> destination via spacecraft.

Sources are ranked by proximity and cost: destination orbit is preferred, then local system bodies, then external sources. Within a tier, routes avoiding launch vehicles, with fewer hops, and more available stock are preferred.

## Configuration

Settings are in `BepInEx/plugins/logisticsmodsdk/LogisticsModSdk.cfg`, created on first launch.

| Setting | Default | Description |
|---------|---------|-------------|
| Return Fuel Enabled | `true` | Reserve return fuel in outbound manifests |
| Safety Multiplier | `1.1` | Multiplier on estimated return fuel |
| Reserve Cargo First | `true` | Prioritize return fuel over logistics cargo |
| Blocked Retry Cooldown | `30 days` | Wait before retrying a blocked dispatch |
| Verbose Logging | `false` | Detailed route and dispatch diagnostics |
| Verbose Log Coalesce | `5 seconds` | Collapse repeated cooldown/shortfall diagnostics |
| Log Flush Interval | `2 seconds` | Buffer verbose log writes to reduce disk I/O |

Logs are written to `BepInEx/LogisticsMod_1.log`.

## Building

```
dotnet build LogisticsMod.csproj -c Release
```

Output deploys directly to the BepInEx plugins folder.

## Known Limitations

- Only one internal orbital relay hop is supported (surface -> orbit -> destination). No multi-hop graph search.
- Destination-side relay staging is not implemented.
- The stock cyclical mission system handles actual flight planning, so stock planner limitations still apply.
- This is an actively developed mod. Keep backup saves.
