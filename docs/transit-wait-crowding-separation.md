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
- [x] `ScaleWaitingTimesSystem` remains operational-only.
- [x] README wording updated to match the experimental semantics in this branch.
- [x] Code comments now state that no separate perceived-cost hook was found.
- [ ] Separate route-choice disutility channel

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

## Follow-Up

The next step is to find a separate writable perceived-cost hook that can carry:

- transfer disutility
- scheduled-mode disutility
- crowd discomfort

Until that hook exists, this branch intentionally stops at clean operational history rather than reintroducing mixed semantics through `WaitTimeEstimate(...)`.
