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

4. **Current mod car-channel mapping is misaligned with vanilla precedent.**
   The decompile pass shows that vanilla ordinary car turning is primarily modeled through the comfort channel (`TurningCost`, `UTurnCost`, `LaneCrossCost`) and curve angle through time plus comfort (`CurveAngleCost`). By contrast, the current mod injects ordinary car turn penalty into `m_Value.y`. The current mod also injects bus-lane deterrence into `m_Value.x`, while vanilla behavior-channel examples are concentrated in stronger aversion signals such as `UnsafeUTurn`, `ForbiddenCost`, and pedestrian `UnsafeCrosswalk`. This is not a hard correctness bug in the current branch, but it is a channel-alignment mismatch worth recording for later cleanup.

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

## Follow-Up: Phase 2 Analysis (Current Decompile-Checked State)

> Decompiled and re-checked against `Game.dll` on 2026-03-06.

Phase 1 already established the clean operational-history path. The remaining Phase 2 question is whether route-choice disutility can be added without contaminating `WaitingPassengers.m_AverageWaitingTime`.

The decompile pass confirms a real pathfinding-side transit stop hook exists, but it also narrows what that hook can model faithfully. `PathUtils.GetTransportStopSpecification(...)` is patchable and definitely participates in route choice, yet stock transit stop cost is still an `x/z/w` model rather than a stock `y`-channel model.

That changes the practical interpretation of the earlier Phase 2 idea: a stop-global `y` add is still possible as an experimental route-choice deterrence layer, but it is no longer the stock-aligned default path for transit wait disutility.

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

`PathUtils.CalculateCost(...)` still computes edge cost as a weighted dot product (`decompiled PathUtils.cs:28-34`), but importantly it first adds actual travel time to `m_Value.x` via `length / speed`:

```text
total_cost = dot(PathfindWeights, PathfindCosts.m_Value)
```

`PathfindCostInfo(...)` is ordered as `(time, behaviour, money, comfort)` (`decompiled PathfindCostInfo.cs:17-27`), so the second slot is confirmed to be the vanilla behavior channel.

That means `m_Value.y` is still a real vanilla behavior-cost channel. However, one key assumption from the earlier write-up was wrong: vanilla normal citizen trips do **not** usually weight `x` and `y` equally.

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
- a global `+30` on `m_Value.y` does **not** reproduce the same effect as `+30` on `m_Value.x` for most citizens
- the route-choice strength of a fixed `y` add weakens as `w.x` rises, because vanilla does not scale `y` with the citizen's time weight

Some special-purpose vanilla requests also override the citizen-default mix entirely rather than using `CitizenUtils.GetPathfindWeights(...)`, including request types with reduced or zero behavior weight. So any injected `y` cost must be treated as request-type-specific, not as a globally equivalent time penalty.

Vanilla stock cost data also shows that the engine uses the `y` channel sparingly, mostly for stronger aversion signals rather than routine travel-time modeling:

| Vanilla cost item | Stock channel usage |
| --- | --- |
| `CarPathfind.m_TurningCost` | comfort |
| `CarPathfind.m_UTurnCost` | comfort |
| `CarPathfind.m_LaneCrossCost` | comfort |
| `CarPathfind.m_CurveAngleCost` | time + comfort |
| `CarPathfind.m_UnsafeUTurnCost` | behavior + comfort |
| `CarPathfind.m_ForbiddenCost` | time + behavior + money + comfort |
| `PedestrianPathfind.m_UnsafeCrosswalkCost` | behavior + comfort |
| `TransportPathfind.m_OrderingCost` | time + comfort (`y = 0`) |
| `TransportPathfind.m_StartingCost` | time + money + comfort (`y = 0`) |
| `TransportPathfind.m_TravelCost` | money (`y = 0`) |
| `GetTransportStopSpecification(...)` | mutates `x`, `z`, and `w`, not `y` |

A whole-project search of the decompiled stock game found no transport-side runtime write that adds a separate stop cost into `m_Value.y`. For public transport, vanilla stop pathfinding cost remains an `x/z/w` path: observed wait or half-headway fallback into time, fare into money, and stop comfort into comfort.

This matters because `RoutesModifiedSystem` builds a shared stop `PathSpecification`. A postfix on `GetTransportStopSpecification(...)` can add a global `y`-channel cost per stop, but it cannot tailor that cost to each citizen's `w.x / w.y` ratio, and it would still be using a channel that vanilla stock transit does not currently use for routine stop cost.

### Practical Implications of Stock `y`-Channel Deterrence

For stock `y`-heavy penalties, a rough time-equivalent comparison is:

```text
time_equivalent_seconds ~= weighted_penalty / w.x
weighted_penalty = y * w.y + w * w.w (+ any other non-time channels that apply)
```

Two stock examples show why `y` is real but modest for high-`w.x` citizens:

- `UnsafeCrosswalkCost = (0, 100, 0, 5)` produces a weighted penalty of about `205..215`, which is only about `10..11` seconds when `w.x = 20`
- `UnsafeUTurnCost = (0, 50, 0, 10)` produces a weighted penalty of about `110..130`, which is only about `5.5..6.5` seconds when `w.x = 20`

These examples do not make `y` meaningless, but they do show why a stock `y` add is a soft deterrence rather than a stock-equivalent replacement for direct time cost.

### What the Verified Hook Can and Cannot Solve

Because `GetTransportStopSpecification(...)` receives stop-level and line-level data, but not passenger trip context, it can support only part of the original disutility split:

| Signal | Available at `GetTransportStopSpecification(...)`? | Notes |
| --- | --- | --- |
| stop operational wait | Yes | Already implemented by vanilla via `m_AverageWaitingTime` and half-headway fallback |
| stop crowding discomfort | Yes | Can be derived from `WaitingPassengers` plus line/vehicle context if supplied by patch, but any `y`-channel write here is experimental stop-global deterrence rather than vanilla-aligned routine stop cost |
| scheduled-mode disutility | Partly yes | Line or mode context is available, with the same experimental `y`-channel caveat |
| transfer disutility | No | No passenger-specific boarding context here |
| feeder-trunk transfer disutility | No | No previous-leg context here |

This is the most important design update from the decompile pass: a postfix on `GetTransportStopSpecification(...)` is technically viable for stop-global disutility, but it is **not** sufficient for transfer-aware disutility, and any `y`-channel implementation there should be treated as experimental rather than vanilla-aligned routine transit cost modeling.

### Revised Approach Assessment

| Approach | Updated assessment |
| --- | --- |
| **A1 - Patch `GetTransportStopSpecification(...)`** | Technically viable for experimental stop-global crowding or scheduled deterrence, but low-confidence for route-choice realism. Stock transit stop cost uses `x/z/w`, not routine `y`, and any global `y` add varies by request type and citizen `w.x / w.y` mix. |
| **A2 - Use the same hook for transfer penalties** | Not viable as a complete solution. The hook has no transfer or previous-leg context, so it cannot model exact transfer-aware disutility. |
| **B - Dual-write / rollback around `m_ConcludedAccumulation`** | Still poor. The decompile results do not make this safer. |
| **C - Separate component plus a later path-evaluation hook** | More promising if exact transfer-aware or better-aligned route-choice cost is still desired. It requires a later transit evaluation point beyond route-graph rebuild, and likely route-identity propagation as well. |
| **D - Treat Phase 1 as the stable endpoint** | Strong fallback. Clean operational history is already implemented and better aligned than a rushed `y`-channel approximation. |

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

But those terms would still be an experimental route-choice deterrence layer if written through `m_Value.y`, not a stock-equivalent transit stop cost model. It also cannot host exact transfer-aware disutility for an individual rider.

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

1. **Document `GetTransportStopSpecification(...)` as an experimental stop-global hook first.**
   If prototyped, scope it to crowding discomfort and possibly scheduled-mode disutility, and describe the route-choice effect as calibration-sensitive rather than stock-equivalent.

2. **Do not treat `m_Value.y` injection as a stock-equivalent replacement for perceived wait.**
   Stock transit stop costs remain `x/z/w`, and fixed `y` costs weaken for high-`w.x` citizens or requests with overridden weights.

3. **Prioritize searching for later path-evaluation or transition hooks if transfer-aware penalties are still required.**
   Focus on `PathfindJobs` transition logic and route-identity propagation, not only route-stop graph construction.

4. **Do not re-enable transfer-related disutility through stop-global wait channels.**
   The verified stop hook has no per-passenger previous-leg context, so doing so would silently mis-model the feature.

5. **Record car-channel cleanup as a separate future task.**
   This is distinct from the transit wait/crowding split. The current mod's ordinary car turn penalty is misaligned with vanilla channel precedent and should move from `m_Value.y` to `m_Value.w`. The current mod's bus-lane deterrence and matching taxi offset should move from `m_Value.x` to `m_Value.y`.

6. **Annotate branch dead code.**
   `ScaleWaitingTimesSystem` is still intentionally unregistered in `Mod.cs`, and that should be commented inline so the branch state is explicit.

Until a later route-choice hook is verified, this branch should continue treating clean operational history as the primary win and avoid reintroducing mixed semantics through `WaitTimeEstimate(...)`. The new decompile facts weaken the case for using `m_Value.y` as the primary transit disutility channel, even though they do not eliminate it as an experimental hook.
