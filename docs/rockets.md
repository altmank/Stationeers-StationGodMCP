# Rockets

[Back to the README](../README.md)

Where a rocket is and where it is going, what the next trip costs before you fly it, and a recorder for the flight
itself. All three only read the game. No gateway is needed.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `rocket_status` | Every rocket: state, node, target and the hops left, mass, fuel, engines, thrust, auto-land confidence, cargo, battery, burn time, and checks of the forecast against the game's own numbers. | `rocket_id` (omit for all), `self_test` |
| `rocket_forecast` | Flies a copy of the rocket to a node and back if asked: fuel, time, mass and battery per leg, the landing verdict, its margins and the same landing at every re-entry profile. | `to`, `then_return`, `park_s`, `mine`, `fill_holds`, what-ifs |
| `rocket_flight_log` | Records a rocket once a second (or as set) while you fly; reads back a summary and pages of rows, and can write a CSV beside the save. | `action`, `rocket_id`, `interval_s`, `csv`, `offset`, `limit`, `every` |

## How the forecast works

The forecast does not estimate. It runs the game's own rules on a copy of the rocket, tick by tick:

- **The route** is the one the game will fly. The game asks its pathfinder for the next hop at every node it reaches;
  the pathfinder counts hops, not distance, and goes through uncharted nodes. A hop costs its distance in Δv, and that
  distance is twice what the map's coordinates give.
- **Thrust** comes from the engine's own combustion, run on a copy of the fuel in the line. Fuel temperature and mix
  therefore count. It burns at the game's combustion rate (0.96), or at Terraforming Reloaded's when that mod is loaded
  (its per-world `RocketsBurnCompletely` option burns at 1.0); `combustion {rate, source}` in `rocket_forecast` and in
  `rocket_status`'s thrust says which. Draining a tank changes neither, so the thrust per mole holds unless the tank warms or cools; the
  forecast does not follow that heat, and says what 100 K either way would do to the thrust.
- **The landing** starts with the game's confidence check, once, as the re-entry hop begins. A confidence of 0 sends
  the rocket back to orbit. Otherwise the autopilot's own throttle rules fly it down, and touchdown is judged as the game
  judges it: under 4 m/s is fine, 4 to 25 m/s damages the engines, faster explodes the rocket.
- **The confidence check is optimistic.** It uses the highest thrust the rocket has ever recorded, not what the fuel
  gives now. Fuel that has cooled or drifted off its mix passes the check and then crashes. `rocket_status` flags this
  as `recorded_exceeds_achievable`; the forecast's `survivable_thrust_drop_pct` says how much thrust the landing can
  lose.
- **Power** is paid every half-second tick from the batteries, device by device. When the batteries run flat the
  engines stop.
- **Cargo** weighs 1 kg per filled slot. The game ignores what is in the slots, so a full hold of ice is 50 or 100 kg.

## Limits and what-ifs

`landing_limits` gives the heaviest landing that still comes down undamaged, the fuel range that lands, the thrust loss
the landing survives, and the heaviest rocket whose confidence stays above `min_confidence` (default 0.25). `profiles`
repeats the landing from 25, 40, 70 and 120 km. `least_fuel_mol` is the least starting fuel for the whole plan.

What-ifs replace part of the rocket before it flies: `fuel_mol`, `fuel_temperature_k`, `fuel_mix`, `thrust_scale`,
`throttle`, `cargo_slots`, `add_cargo_kg`, `battery_j` or `battery_percent`, `extra_load_w`, `profile`.

`column` lists anything standing in the column above the pad that the landing rocket would hit.

## Not covered

- Only the Pumped Gas Engine is flown. A rocket with another engine is refused.
- A landing rocket cannot be given a new target in the forecast. The game would turn it into a launch from its height.
- The tanks' heat exchange with the air is not followed (see above).
