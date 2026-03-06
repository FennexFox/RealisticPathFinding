# Realistic Path Finding

A Cities: Skylines II mod that replaces the vanilla pathfinding cost model with a more realistic one, giving citizens, vehicles, and transit riders more configurable travel behaviour.

---

## Features

### Vehicles

| Feature | Description |
| --- | --- |
| **Turn penalties** | Adds a time cost for sharp turns, scaled by angle. U-turns carry an extra fixed penalty. |
| **Road hierarchy bias** | Adds a density cost to collector, local, and alleyway roads, pushing traffic toward higher-class roads. |
| **Congestion feedback (EWMA)** | Monitors real in-game travel times per lane and feeds live congestion back into routing costs via exponential smoothing. |
| **Car mode weight** | Scales perceived in-vehicle car time to balance car vs. public transport attractiveness. |
| **Stochastic route choice** | Adds a tunable temperature parameter so route splits among near-equal paths feel more natural. |

### Transit

| Feature | Description |
| --- | --- |
| **Operational wait weight** | Multiplies operational wait at every transit stop in this experimental branch. |
| **Transfer disutility factor** | Reserved for a future separate route-choice channel; it no longer writes into stop wait history. |
| **Feeder-to-trunk disutility factor** | Reserved for a future separate route-choice channel for bus/tram to metro/train/ship transfers. |
| **Scheduled-mode disutility factor** | Reserved for a future separate route-choice channel for rail, ship, and air. |
| **Crowding overload/discomfort split** | Stop history records operational wait plus overload delay only; crowd discomfort is documented separately for a future route-choice hook. |
| **Mode weights** | Individual in-vehicle time multipliers for bus, tram, subway, train, and ferry. |
| **Bus lane penalty** | Adds a time penalty for non-bus vehicles using bus-only lanes. |

### Pedestrians

| Feature | Description |
| --- | --- |
| **Age-specific walk speeds** | Sets separate walking speeds for children, teens, adults, and the elderly. |
| **Walking cost multiplier** | Globally scales pedestrian path cost to control how attractive walking is. |
| **Long-distance penalty** | Ramps down perceived walk speed beyond a comfort distance, discouraging very long walks. |
| **Crosswalk cost factors** | Separate multipliers for safe crosswalks and unsafe crossings. |

### Bicycles

| Feature | Description |
| --- | --- |
| **Ownership by age** | Sets the share of teens, adults, and seniors allowed to use bicycles. |
| **Short-trip penalty** | Penalises very short bike trips, keeping walking competitive for nearby destinations. |
| **Long-trip penalty** | Gradually discourages cycling beyond a comfort distance, favouring transit or cars. |

### Taxi

| Feature | Description |
| --- | --- |
| **Crowding fare increase** | Raises perceived taxi cost when the waiting queue at a stand exceeds a threshold. |

## Configuration

All settings are available in **Options -> Realistic Path Finding**.

### Notable defaults

| Setting | Default | Notes |
| --- | --- | --- |
| Car mode weight | 0.90 | Slightly favours car over transit |
| Base turn penalty | 2 s | Per sharp turn |
| Collector bias | 0.05 | Mild highway preference |
| Local bias | 0.10 | Discourages cut-throughs |
| Alleyway bias | 0.15 | Strong preference away from alleys |
| Operational wait weight | 1.00 | Applies to all stop operational wait |
| Transfer disutility factor | 1.5x | Reserved for a future separate perceived-cost channel |
| Crowding discomfort factor | 0.30 | Reserved for a future separate perceived-cost channel |
| Crowding overload wait factor | 1.00 | Adds real wait only above one vehicle load |
| Adult walk speed | 3.1 mph | Realistic average |
| Ped walk factor | 1.6 | 60% extra perceived travel time when walking |
| Adult bike % | 25 % | 1-in-4 adults may cycle |

---

## Compatibility

| Mod | Status |
| --- | --- |
| **Realistic Trips (Time2Work)** | Detected automatically; trip timing is adjusted accordingly |
| Other pathfinding mods | May conflict; use **Disable walking cost multiplier** if pedestrian issues arise |

---

## Contributing

Pull requests and issues are welcome on [GitHub](https://github.com/ruzbeh0/RealisticPathFinding).
