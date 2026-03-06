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

## Update: Decompile Pass 1 (Supersedes Earlier Phase 2 Assumptions)

> Decompiled and re-checked against `Game.dll` on 2026-03-06.

This update supersedes three earlier assumptions in the previous Phase 2 section:

- the relevant transit pathfinding hook is **not** a hypothetical `GetTransportBoardingSpecification(...)`-style method; it is `PathUtils.GetTransportStopSpecification(...)`
- vanilla normal-citizen `w.y` is **not** usually equal to `w.x`
- a stop-level postfix can help with stop-global disutility, but it cannot correctly implement transfer-aware disutility by itself

### Confirmed Transit Stop Hook on the Pathfinding Side

`Game.Pathfind.PathUtils.GetTransportStopSpecification(...)` is a real, patchable method that returns a `PathSpecification` for transit stop edges (`decompiled PathUtils.cs:1527-1566`).

`Game.Pathfind.RoutesModifiedSystem` calls that method when rebuilding route graph specs (`decompiled RoutesModifiedSystem.cs:221` and `741`).

`Game.Simulation.WaitingPassengersSystem` adds `PathfindUpdated` when a stop's `m_AverageWaitingTime` changes (`decompiled WaitingPassengersSystem.cs:185-187`), and `RoutesModifiedSystem` listens for `PathfindUpdated` (`decompiled RoutesModifiedSystem.cs:1304`).

That confirms a real route-choice hook on the pathfinding side. It is not just an execution-time estimate path.

The actual vanilla stop-wait formula at that hook is:

```text
stop_cost_time_seconds =
    max(transportLine.m_VehicleInterval * 0.5f,
        waitingPassengers.m_AverageWaitingTime)
    - RouteUtils.GetStopDuration(transportLineData, transportStop)
```

Then vanilla applies:

```text
transportPathfindData.m_StartingCost.m_Value.x =
    max(0f, transportPathfindData.m_StartingCost.m_Value.x + stop_cost_time_seconds)

transportPathfindData.m_StartingCost.m_Value.z *= transportLine.m_TicketPrice
transportPathfindData.m_StartingCost.m_Value.w *= (1 - transportStop.m_ComfortFactor)
```

Important implications:

- the pathfinder is not reading raw `m_AverageWaitingTime` directly into cost without modification
- the stop edge already combines observed wait, half-headway fallback, stop-duration subtraction, fare, and comfort

A whole-project search of the decompiled game code found only two pathfinding-side readers of `m_AverageWaitingTime`:

- `PathUtils.GetTransportStopSpecification(...)` for public transport
- `PathUtils.GetTaxiStopSpecification(...)` for taxi stands

For passenger transit, `GetTransportStopSpecification(...)` is the relevant hook.

### Confirmed Stop-History Feedback Path

The operational feedback loop is now re-verified against vanilla game code:

- `ResidentAISystem.WaitTimeEstimate(Entity waypoint, int seconds)` writes directly to `WaitingPassengers.m_ConcludedAccumulation` (`decompiled ResidentAISystem.cs:3981-3986`)
- successful boarding also writes observed wait into `m_ConcludedAccumulation` and increments `m_SuccessAccumulation` (`decompiled ResidentAISystem.cs:3784-3789`)
- `WaitingPassengersSystem.TickWaitingPassengersJob` recomputes `m_AverageWaitingTime` from `m_OngoingAccumulation` and `m_ConcludedAccumulation`, then marks the stop with `PathfindUpdated` (`decompiled WaitingPassengersSystem.cs:180-192`)

So the current branch's Phase 1 understanding is now validated end to end:

```text
WaitTimeEstimate / successful boarding
    -> WaitingPassengers.m_ConcludedAccumulation
    -> WaitingPassengersSystem.TickWaitingPassengersJob
    -> WaitingPassengers.m_AverageWaitingTime
    -> PathfindUpdated
    -> RoutesModifiedSystem rebuilds stop PathSpecification
    -> PathUtils.GetTransportStopSpecification feeds stop wait into route choice
```

### Confirmed `PathfindCosts` / `PathfindWeights` Facts

`PathUtils.CalculateCost(...)` still computes edge cost as a weighted dot product (`decompiled PathUtils.cs:28-34`):

```text
total_cost = dot(PathfindWeights, PathfindCosts.m_Value)
```

That means `m_Value.y` is still a valid seconds-like behavior-time channel. However, one key assumption from the earlier write-up was wrong: vanilla normal citizen trips do **not** usually weight `x` and `y` equally.

`CitizenUtils.GetPathfindWeights(...)` returns (`decompiled CitizenUtils.cs:63-70`):

```text
w.x = 5 * (4 - 3.75 * citizen.m_LeisureCounter / 255)
w.y = 2
w.z = 2500 * max(1, householdCitizens) / max(250, household.m_ConsumptionPerDay)
w.w = 1 + 2 * random(TrafficComfort)
```

Derived consequences:

- `w.x` ranges from `20.0` down to `1.25`
- `w.y` is a constant `2.0`
- a global `+30` seconds on `m_Value.y` does **not** reproduce the same effect as `+30` seconds on `m_Value.x` for most citizens

This matters because `RoutesModifiedSystem` builds a shared stop `PathSpecification`. A postfix on `GetTransportStopSpecification(...)` can add a global `y`-channel cost per stop, but it cannot tailor that cost to each citizen's `w.x / w.y` ratio.

### What the Verified Hook Can and Cannot Solve

Because `GetTransportStopSpecification(...)` receives stop-level and line-level data, but not passenger trip context, it can support only part of the original disutility split:

| Signal | Available at `GetTransportStopSpecification(...)`? | Notes |
| --- | --- | --- |
| stop operational wait | Yes | Already implemented by vanilla via `m_AverageWaitingTime` and half-headway fallback |
| stop crowding discomfort | Yes | Can be derived from `WaitingPassengers` plus line/vehicle context if supplied by patch |
| scheduled-mode disutility | Partly yes | Line or mode context is available |
| transfer disutility | No | No passenger-specific boarding context here |
| feeder-trunk transfer disutility | No | No previous-leg context here |

This is the most important design update from the decompile pass: a postfix on `GetTransportStopSpecification(...)` is viable for stop-global disutility, but it is **not** sufficient for transfer-aware disutility.

### Revised Approach Assessment

| Approach | Updated assessment |
| --- | --- |
| **A1 - Patch `GetTransportStopSpecification(...)`** | Viable now. Best candidate for stop-global crowding or scheduled disutility on the pathfinding side. |
| **A2 - Use the same hook for transfer penalties** | Not viable as a complete solution. The hook has no transfer or previous-leg context. |
| **B - Dual-write / rollback around `m_ConcludedAccumulation`** | Still poor. The decompile results do not make this safer. |
| **C - Separate component plus a later path-evaluation hook** | Still possible, but only if a later transit evaluation point can be found beyond route-graph rebuild. |
| **D - Treat Phase 1 as the stable endpoint** | Still a valid fallback if no transfer-aware hook is found. |

### Transfer Penalty Findings

The legacy transfer penalty was applied per passenger during path execution, not during path selection.

In the pre-`144a841` version of `RPFRouteUtils.StripTransportSegments(...)`, the mod built a single `seconds` value like this:

```text
seconds =
    boarding_time_seconds
    + scaled_historical_wait_seconds

seconds *= crowding_factor
seconds *= waiting_weight
seconds *= scheduled_mode_factor   // rail / ship / air
seconds *= transfer factor         // transfer_penalty or feeder_trunk_transfer_penalty

seconds -= scaled_historical_wait_seconds
transportEstimateBuffer.AddWaitEstimate(waypoint, seconds)
```

The verified transfer-specific part of that legacy flow was:

- compute `isTransfer` as `lastBoardedRoute != currentRoute`
- multiply by `feeder_trunk_transfer_penalty` when the previous leg was feeder (`Bus`/`Tram`) and the current leg was trunk (`Subway`/`Train`/`Ship`/`Airplane`/`Ferry`)
- otherwise multiply by `transfer_penalty`
- subtract the already-counted historical wait already read by vanilla `PathUtils`
- write the remainder through `transportEstimateBuffer.AddWaitEstimate(...)`

That means the old transfer penalty was not a separate route-choice-only signal. It was written back into stop-history accumulation, which polluted:

```text
WaitingPassengers.m_ConcludedAccumulation
    -> WaitingPassengers.m_AverageWaitingTime
    -> PathUtils.GetTransportStopSpecification(...)
```

### Current Execution-Side Transfer Tracking

Per-passenger transfer detection is still possible in the current branch, but only in the execution-time integration path.

Verified current flow in `RPFRouteUtils.StripTransportSegments(...)`:

- `ResolveRouteForWaypoint(...)` resolves the currently boarded route for the boarding waypoint
- `lastBoardedRoute` is carried across successive boardings
- `lastTransportType` is also carried across successive boardings
- `isTransfer` is computed per passenger as `lastBoardedRoute != currentRoute`
- feeder-to-trunk classification is still available from `lastTransportType` plus the current transport type

This is the key boundary:

- individual transfer tracking exists
- but it exists only while evaluating the already-chosen path during execution
- it is not available at the shared stop-graph rebuild hook

### Why the Stop-Global Hook Is Still Insufficient

`PathUtils.GetTransportStopSpecification(...)` is a stop-global hook shared by every passenger using that stop.

It has:

- stop-level data
- line-level data
- waiting-passenger data

It does **not** have:

- previous boarded route for the specific passenger
- previous transport mode for the specific passenger
- a reliable "same line vs changed line" signal for the specific passenger

So it can host stop-global disutility such as:

- crowding discomfort
- scheduled-mode stop-global adjustments

But it cannot host exact transfer-aware disutility for an individual rider.

### Candidate Transfer-Aware Hook

The first credible transfer-aware pathfinding hook found so far is not `GetTransportStopSpecification(...)`, but `Game.Pathfind.PathfindJobs.PathfindExecutor.AddConnections(...)` (`decompiled PathfindJobs.cs:693-840`).

Why this hook matters:

- it runs during path expansion, not execution
- it sees the current edge and the candidate next edge in the same transition
- it has the current accumulated `baseCost`
- it is the last obvious place to add transition cost before the candidate path is pushed through `AddHeapData(...)`

The nearby `DisallowConnection(...)` method (`decompiled PathfindJobs.cs:959-982`) is also relevant, but only as a transition filter:

- it sees `prevMethod` and `newSpec` together
- it can allow or block a transition
- it is **not** the best hook for adding a numeric transfer penalty

For a numeric transfer penalty, `AddConnections(...)` is the right candidate hook. `FindEndNode()` already carries forward the previous `PathMethod` and current `EdgeID` into that transition logic (`decompiled PathfindJobs.cs:583-605`).

### Remaining Blocker for Exact Route-Aware Transfer

Even with the transition hook identified, exact route-aware transfer is still blocked by missing route identity inside the pathfinding job.

What `PathfindJobs` currently tracks:

- previous `PathMethod`
- current `EdgeID`
- candidate next `EdgeID`

What it does **not** track directly:

- route entity identity for the current transit edge
- route entity identity for the candidate transit edge

The graph edge owner is not the route entity itself. For route graph edges, the owner stored in the pathfinding graph is a waypoint or segment entity created by `RoutesModifiedSystem`, not the transport line entity directly.

Route identity is recovered elsewhere through ECS lookups such as `Owner.m_Owner`. That is how route-aware boarding checks work in `RouteUtils.GetBoardingVehicle(...)`.

So the remaining requirement for exact transfer-aware penalties is:

```text
edge owner entity -> route entity
```

That mapping must be made available to the pathfinding transition logic if the mod wants to distinguish:

- staying on the same line
- boarding a different line
- feeder-to-trunk transfer

Without that mapping, a pathfinding-side patch can only approximate "public transport transition" or "boarding transition". It cannot reliably implement exact "changed line/route" transfer disutility.

Decision for this branch:

- the recommended next search target is route-identity propagation into `PathfindJobs`
- until that exists, do not re-enable transfer penalties through stop-global wait channels

### Current Work Items After Decompile Pass 1

1. **Prototype a narrow Harmony postfix on `PathUtils.GetTransportStopSpecification(...)`.**
   Scope it to stop-global terms only: crowding discomfort and possibly scheduled-mode disutility.

2. **Do not re-enable transfer-related disutility through this hook yet.**
   The verified call site has no per-passenger transfer context, so doing so would silently mis-model the feature.

3. **Choose a calibration policy for `m_Value.y`.**
   Because vanilla normal-citizen weights are `w.x != w.y`, the injected `y` seconds must either:
   - target a chosen reference citizen,
   - use a new exposed multiplier for tuning, or
   - accept that the new route-choice effect will differ from the old mixed `x`-channel behavior

4. **Search for a later transit-specific hook if transfer penalties must remain functional.**
   The next search should focus on path expansion or boarding-transition code that has trip context, not only route-stop graph construction.

5. **Annotate branch dead code.**
   `ScaleWaitingTimesSystem` is still intentionally unregistered in `Mod.cs`, and that should be commented inline so the branch state is explicit.

Until a transfer-aware route-choice hook is verified, this branch should continue treating clean operational history as the primary win and avoid reintroducing mixed semantics through `WaitTimeEstimate(...)`.
