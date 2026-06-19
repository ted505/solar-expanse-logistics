# Current Issues

## Per-Tick Committed Stock Clear (Required but root cause unknown)

### Symptom

Without `_committedStock.Clear()` at the start of `OnDayChange`, relay final-leg dispatches get stuck in a "Waiting for prior shipment" serialized-wait loop. After a successful relay dispatch from EARTH [ORBIT] → MARS, subsequent ticks see stale committed stock (e.g. `committed=4935`) that exceeds `rawStagedStock`, making `stagedStock` negative and blocking all further dispatches indefinitely.

### Current Fix

`_committedStock.Clear()` at the top of `OnDayChange`, before planning begins. This ensures committed stock from tick N never carries into tick N+1.

### What We Know

- The working DLL (260KB, from `logisticsmod (3).zip`) does NOT have a per-tick clear — it uses only the wall-clock expiry (`ResetCommittedStockIfStale`, 1-second window). It works correctly.
- The current source (06-01f + fixes) REQUIRES the per-tick clear. Without it, the serialized-wait fires every tick and never recovers.
- Both versions have identical `CommitStock`, `GetCommittedStock`, `ResetCommittedStockIfStale`, and `CommittedStockWindowSeconds = 1.0` implementations.
- Both versions have `ClearPendingPlanningDelivery(requesterOI, rd)` when `inFlight > 0` (working: line 1378, current: line 1466), which can strip the `HasPendingPlanningDelivery` guard before the relay handler is reached.
- The serialized-wait guard (`committedFromOrbit > 0 && stagedStock < usefulFinalLoad`) is identical in both versions.

### What Changed (Unidentified)

Something between the working DLL and the current 06-01f source changed the timing or conditions such that committed stock from tick N now persists into tick N+1 and causes serialized-wait to fire. The working DLL does not exhibit this under the same game conditions. The specific change has not been identified. Candidates to investigate:

- Differences in how/when the planner snapshot is built or reused across ticks
- Changes to `GetInFlightDeliveryAmount` or snapshot indexing that cause `ClearPendingPlanningDelivery` to fire earlier than in the working DLL
- New code paths (per-provider rules, ship assignment) that re-enter the relay handler or modify committed stock state
- Changes to request evaluation ordering or throttling that allow more frequent relay handler entry

### Status

Per-tick clear is in place and working. Root cause investigation deferred.

---

## Multi-Dispatch Relay Loop (Reverted)

### Symptom

The 06-01b multi-dispatch relay loop (`while (remaining > 0 && stagedStock > 0)`) spun out of control when `TryCreateRelayFinalDelivery` failed (e.g. return fuel probe pending). The loop decremented `remaining` and logged "RELAY multi-final-leg" on each iteration even though no mission was actually created, because the failed dispatch path (return fuel shortfall → bootstrap) returned `true` without consuming staged stock. The loop ran until `remaining` was exhausted, flooding the log and wasting cycles.

### Fix Applied

Reverted to the 06-01 single-dispatch approach: one `TryCreateRelayFinalDelivery` call per tick per request, with the serialized-wait guard restored (`committedFromOrbit > 0 && stagedStock < usefulFinalLoad`).

### Status

Fixed. Single-dispatch is stable.

---

## Moon-Case Route Detection (Fixed)

### Symptom

`IsMoonCaseRoute` sibling check (`bodyA.parentObjectInfo == bodyB.parentObjectInfo`) matched interplanetary routes (e.g. EARTH → MARS) because both planets share the Sun as parent. This forced `transferType = ETransferType.Optimal` on all interplanetary FAST transfers, overriding the user's FAST setting.

### Fix Applied

Added parent body type check: sibling match only triggers when the shared parent is a `Planet` or `DwarfPlanet`, excluding star-parented bodies.

```csharp
if (bodyA.parentObjectInfo != null
    && bodyA.parentObjectInfo == bodyB.parentObjectInfo
    && (bodyA.parentObjectInfo.objectTypes == global::Data.EObjectTypes.Planet
        || bodyA.parentObjectInfo.objectTypes == global::Data.EObjectTypes.DwarfPlanet))
    return true;
```

### Status

Fixed. Verified in-game: relay dispatches now show `transfer=Fastest`.
