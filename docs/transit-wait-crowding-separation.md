# Transit Wait/Crowding Separation

This branch is a local experimental cleanup for upstream Issue #7. It is not written as upstream PR copy. The goal is to make transit wait semantics explicit inside this fork before attempting any deeper route-choice integration.

## Why This Exists

The previous transit wait path mixed several different concepts into one value inside `RPFRouteUtils.StripTransportSegments(...)`:

- operational wait at the stop
- crowd discomfort
- overload delay
- transfer burden
- scheduled-mode adjustment
- stop-history feedback via `WaitingPassengers.m_ConcludedAccumulation`

That made `WaitingPassengers.m_AverageWaitingTime` hard to interpret. A larger value could mean a genuinely slow stop, but it could also mean that the route involved a transfer or a mode-specific preference multiplier. Those are not the same thing.

## Current Branch Goal

Phase 1 in this branch is to keep stop-history feedback operational-only:

- operational wait remains a stop-level signal
- overload delay remains a stop-level signal
- transfer, scheduled-mode, and crowd discomfort stay out of stop history

Phase 2 is intentionally blocked unless a writable perceived-cost hook is found beyond the current `WaitTimeEstimate(...) -> m_ConcludedAccumulation` path.

## Current-State Walkthrough

Before this cleanup, the branch effectively did the following:

1. Read boarding time.
2. Added scaled `WaitingPassengers.m_AverageWaitingTime`.
3. Applied crowding, waiting weight, scheduled-mode, and transfer multipliers in the same channel.
4. Wrote the resulting seconds into `WaitTimeEstimate(...)`.
5. `BoardingJob.WaitTimeEstimate(...)` added those seconds to `WaitingPassengers.m_ConcludedAccumulation`.

That polluted stop history with passenger-context disutility terms.

## Target Semantics

| Signal | Meaning | Scope | Can write to stop history? |
| --- | --- | --- | --- |
| `operational_wait_seconds` | Boarding time plus scaled measured wait | Stop-level | Yes |
| `overload_wait_seconds` | Real extra delay when the queue exceeds one vehicle load | Stop-level | Yes |
| `crowding_discomfort_signal` | Non-operational discomfort under crowding | Route-choice | No |
| transfer disutility | Passenger-context burden of changing routes | Route-choice | No |
| scheduled-mode disutility | Passenger-context adjustment for scheduled modes | Route-choice | No |

## Chosen Formulas

```text
operational_wait_seconds =
    boarding_time_seconds + (WaitingPassengers.m_AverageWaitingTime / t2w_timefactor)

overload_wait_seconds =
    operational_wait_seconds
    * crowding_overload_wait_factor
    * max(0, queue_ratio - 1)

crowding_discomfort_signal =
    saturate((queue_ratio - crowding_discomfort_threshold) / (1 - crowding_discomfort_threshold))

stop_history_wait_seconds =
    ceil(operational_wait_seconds + overload_wait_seconds)

perceived_wait_seconds =
    ceil((operational_wait_seconds + overload_wait_seconds)
         * operational_wait_weight
         * (1 + crowding_discomfort_factor * crowding_discomfort_signal)
         * scheduled/transfer disutility multipliers)
```

Important note:

- `perceived_wait_seconds` is still only a conceptual value in this branch.
- No separate writable route-choice hook was found beyond `WaitTimeEstimate(...)`.
- Because of that, this branch does **not** inject transfer, scheduled-mode, or crowd discomfort terms back into stop history as a hidden approximation.

## Setting Mapping

| Legacy key | New key | Status |
| --- | --- | --- |
| `waiting_time_factor` | `operational_wait_weight` | Migrated on load |
| `transfer_penalty` | `transfer_disutility_factor` | Migrated on load |
| `feeder_trunk_transfer_penalty` | `feeder_trunk_disutility_factor` | Migrated on load |
| `scheduled_wt_factor` | `scheduled_mode_disutility_factor` | Migrated on load |
| `crowdness_factor` | `crowding_discomfort_factor` | Migrated on load |
| `crowdness_stop_threashold` | `crowding_discomfort_threshold` | Migrated on load |
| none | `crowding_overload_wait_factor` | New field, default `1.0` |

Migration rule in this branch:

- new transit properties own the runtime value when present
- legacy keys remain as write-only aliases so older `.coc` files still load
- if a new property was not loaded but a legacy key was, the legacy value is copied into the new runtime property

## Implementation Status

- [x] Transit settings renamed to explicit operational/disutility terminology.
- [x] Legacy transit keys still load from existing settings files.
- [x] Stop-history feedback now writes only operational wait plus overload delay.
- [x] `ScaleWaitingTimesSystem` remains operational-only (system is currently **unregistered**; see review note below).
- [x] README wording updated to match the experimental semantics in this branch.
- [x] Code comments now state that no separate perceived-cost hook was found.
- [ ] Separate route-choice disutility channel

---

## Review Evaluation

> Reviewed 2026-03-06.

### Problem Awareness — Strong

The core problem is correctly identified: mixing stop-level timing signals with passenger-context disutility multipliers in a single value that feeds back into `WaitingPassengers.m_ConcludedAccumulation` makes stop history unreliable as an indicator of actual stop performance. The six signals enumerated in "Why This Exists" match the original code path exactly.

### Improvement Plan — Sound and Pragmatic

The two-phase approach is well-disciplined. The decision to **not** approximate perceived cost through `WaitTimeEstimate(...)` is correct — doing so would reintroduce the exact contamination this branch removes. Blocking Phase 2 rather than shipping a hidden compromise is the right trade-off for an experimental fork.

### Code Verification

All checked items in the implementation status have been verified against the source:

| Claim | Verified | Location |
| --- | --- | --- |
| Settings renamed | ✅ | `Setting.cs:214–290` — new property names with slider attributes |
| Legacy keys load | ✅ | `Setting.cs:292–345` — write-only setters with `_legacyXxx` backing fields |
| Migration logic | ✅ | `Setting.cs:133–173` — `MigrateLegacyTransitSettings()` copies legacy→new when new wasn't loaded |
| Stop history = operational + overload | ✅ | `RPFRouteUtils.cs:137` — `StopHistoryWaitSeconds = ceil(operational + overload)`, no disutility terms |
| Perceived cost discarded | ✅ | `RPFRouteUtils.cs:261` — `_ = waitProfile.PerceivedWaitSeconds;` with comment |
| README semantics | ✅ | `README.md:24–29` — describes operational-only wait, reserved disutility channels |
| `BuildTransitWaitProfile` formulas | ✅ | `RPFRouteUtils.cs:89–139` — all five formulas match the document |

### Issues Found

1. **`ScaleWaitingTimesSystem` is unregistered, not just operational-only.**
   `Mod.cs:62` is commented out: `//updateSystem.UpdateAfter<ScaleWaitingTimesSystem, ...>`. The system class exists and is correct, but it never runs. This appears intentional — `BuildTransitWaitProfile` already divides by `t2wTimeFactor` at line 105, so enabling the system would **double-apply** the Time2Work correction. The implementation status item now notes this. If the intent is to keep the system as dead code for future use, it should carry a comment in `Mod.cs` explaining why it is disabled.

2. **`ResetTransitMigrationTracking()` has no callers.**
   `Setting.cs:143–164` defines the method but nothing invokes it. This is likely intended for a future "reset to defaults" UI action. It is harmless dead code, but should be annotated or removed to avoid confusion.

3. **`BusLanePatches.cs` has two classes with identical `[HarmonyPatch]` signatures** (`Patch_GetCarDriveSpecification_Road` and `Patch_GetCarDriveSpecification_RoadWithTrack` at lines 30–52 and 62–83). Both target the same method overload. This is a pre-existing issue unrelated to the transit separation work, but it means only one of the two patches can bind — Harmony will likely apply the last one registered and silently ignore the other.

### Formulas — Correct

All five formulas (`operational_wait_seconds`, `overload_wait_seconds`, `crowding_discomfort_signal`, `stop_history_wait_seconds`, `perceived_wait_seconds`) in the document match the implementation in `BuildTransitWaitProfile`. Edge-case guards (`math.max`, `math.clamp`, `math.saturate`) are properly applied.

## Validation Checklist

Manual scenarios to verify in-game:

- uncrowded single-leg bus trip
- queue above discomfort threshold but below one vehicle load
- queue above one vehicle load
- bus to metro transfer
- direct train or ship trip with scheduled-mode factor changed
- loading an older settings file that only contains legacy transit keys

Acceptance checks:

- changing transfer or scheduled-mode factors must not change stop-history accumulation
- changing crowding discomfort factor must not change stop-history accumulation below overload
- overload wait must increase stop-history accumulation only when the queue exceeds one vehicle load
- `operational_wait_weight` must conceptually apply to all stop waits, not only the first stop
- user-facing text must describe the implemented semantics, not the old mixed channel

## Follow-Up: Phase 2 Approach Analysis

The core challenge for Phase 2: `StripTransportSegments` runs during path **execution** (the citizen has already chosen a path and is about to travel it), not during path **selection** (the native pathfinder is evaluating route alternatives). The pathfinder runs on the C++ side and reads `WaitingPassengers.m_AverageWaitingTime` as its only stop-level cost input. The only writable channel from mod code back to the pathfinder for transit stops is `m_ConcludedAccumulation` → `m_AverageWaitingTime`, which is exactly the channel this branch cleaned up.

### How `PathfindCosts` Channels and `PathfindWeights` Interact

The pathfinder's total edge cost is a weighted dot product of two float4 vectors:

```text
total_cost = dot(PathfindWeights, PathfindCosts.m_Value)
           = w.x * c.x  +  w.y * c.y  +  w.z * c.z  +  w.w * c.w
             ─── time ──    ─ behavior ─   ── money ──   ─ comfort ─
```

Both `m_Value.x` (time) and `m_Value.y` (behavior-time) are denominated in **seconds**. They are not separate unit systems — the behavior-time channel is just seconds that get weighted differently per citizen or per trip purpose.

#### How Citizens Weight the Channels

The `PathfindWeights` are set per pathfind request:

| Context | `w.x` (time) | `w.y` (behavior) | `w.z` (money) | `w.w` (comfort) | Source |
| --- | --- | --- | --- | --- | --- |
| Normal citizen trip | varies | varies | varies | varies | `CitizenUtils.GetPathfindWeights(citizen, household, size)` |
| Boarding vehicle (generic) | 1.0 | 1.0 | 1.0 | 1.0 | `RPFResidentAISystem.cs:3908` |
| Emergency shelter | 1.0 | 0.2 | 0.0 | 0.1 | `RPFResidentAISystem.cs:4108` |
| Cargo transport | 1.0 | 1.0 | transportCost | 1.0 | `RPFResourceBuyerSystem.cs:1326` |

For a citizen with `w.x = w.y = 1.0`, adding 30 seconds to `m_Value.y` has exactly the same effect as adding 30 seconds to `m_Value.x`. The difference only shows for citizens whose `GetPathfindWeights()` returns different values for the two channels.

#### What This Means for Disutility Injection

If Approach A (patching a transit boarding spec method) is viable, the disutility seconds injected into `m_Value.y` can be calibrated like this:

```text
disutility_seconds = perceived_wait_seconds - stop_history_wait_seconds
```

This is the **difference** between the full perceived cost (with crowding, transfer, and scheduled-mode multipliers) and the clean operational cost. For a typical citizen with `w.y ≈ 1.0`, adding this value to `m_Value.y` produces the same route-choice effect as the old mixed implementation produced via `m_Value.x` — but without contaminating stop history.

However, the exact weights that `CitizenUtils.GetPathfindWeights()` returns per citizen type need to be reverse-engineered by decompiling `CitizenUtils`. If `w.y` is systematically lower than `w.x` for some citizen classes, the effective disutility would be dampened compared to the old implementation. This can be compensated by dividing by the typical `w.y / w.x` ratio, but this requires knowing those ratios first.

**Bottom line**: the y-channel is in the same unit (seconds) and usually weighted equally with x, so the calibration approach is straightforward. But decompiling `CitizenUtils.GetPathfindWeights()` is a necessary prerequisite to confirm the weight distribution across citizen types.

### Candidate Approaches

| Approach | Idea | Feasibility | Risk |
| --- | --- | --- | --- |
| **A — Patch transit boarding specification** | If `PathUtils` exposes a `GetTransportBoardingSpecification` or similar method that returns a `PathSpecification` during pathfinding evaluation, a Harmony postfix could add perceived disutility to `PathfindCosts.m_Value.y` (behavior-time channel) — the same pattern `BusLanePatches` uses for car lanes. This would influence route choice without touching stop history. | **Investigate first.** Requires decompiling `PathUtils` to find the right method signature. | Low if the method exists; the pattern is already proven in this codebase. |
| **B — Dual-write with frame rollback** | Temporarily inflate `m_ConcludedAccumulation` with disutility terms before pathfinding runs, then restore the clean value after pathfinding completes. | Poor. Requires precise frame-phase timing and risks corrupted values on dropped frames or async job scheduling. | High — fragile and hard to test. |
| **C — Custom ECS component** | Add a `PerceivedTransitCost` component that stores disutility per stop. Patch the code that reads transit cost during path evaluation to sum `m_AverageWaitingTime + PerceivedTransitCost`. | Medium. Requires finding and patching the pathfinder's transit cost reader. | Medium — the patch point may be in Burst-compiled code. |
| **D — Accept Phase 1 as terminal** | The clean operational history is already a significant improvement. The unused `PerceivedWaitSeconds` field in `TransitWaitProfile` serves as documentation of intended future use. | Always available. | None — but leaves disutility settings as UI-visible no-ops. |

### Recommended Next Steps

1. **Decompile `PathUtils` to find transit-specific boarding/waiting cost methods.** Look for methods analogous to `GetCarDriveSpecification` but for `TransportType.Bus`/`Tram`/etc. If such a method returns a `PathSpecification`, Approach A becomes viable with minimal code.

2. **Decompile `CitizenUtils.GetPathfindWeights()`** to understand the weight distribution across citizen types. This confirms whether `w.y ≈ w.x` for most citizens (making calibration trivial) or whether a correction ratio is needed.

3. **If Approach A is viable**, implement a `TransitBoardingCostPatch` (similar to `BusLanePatches`) that injects `perceived_wait_seconds - stop_history_wait_seconds` into `PathfindCosts.m_Value.y`. This keeps stop history clean while influencing route choice. The injected value is in seconds, directly comparable to the time channel.

4. **If no patchable method is found**, evaluate whether the disutility settings should remain in the UI with "reserved for future use" labels (current state), or be hidden until a hook is found. Leaving them visible but inert may confuse users.

5. **Annotate dead code.** Add a comment to the `ScaleWaitingTimesSystem` registration line in `Mod.cs` explaining why it is disabled. Either annotate or remove `ResetTransitMigrationTracking()` depending on whether a settings-reset feature is planned.

Until a writable route-choice hook is confirmed, this branch intentionally stops at clean operational history rather than reintroducing mixed semantics through `WaitTimeEstimate(...)`.
