# Rockets

[Back to the README](../README.md)

Where a rocket is and where it is going, what the next trip costs before you fly it, what its gear can mine, and a
recorder for the flight itself. All four only read the game. No gateway is needed.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `rocket_status` | Every rocket: state, node, target and the hops left, mass, fuel lines (gas and liquid), engines and how each draws its fuel, thrust, auto-land confidence (the screen's and the one the game checks at the pad), gear, cargo, battery, burn time, and checks of the forecast against the game's own numbers. `parts` adds every part's position and turn from the engine mount, enough to build the rocket again. | `rocket_id` (omit for all), `compact`, `parts`, `self_test` |
| `rocket_forecast` | Flies a copy of the rocket to a node and back if asked: fuel, time, mass and battery per leg, the landing verdict, its margins and the same landing at every re-entry profile. Warns when the loadout cannot mine the destination. | `to`, `then_return`, `return_to`, `park_s`, `mine`, `fill_holds`, `deploy_payload`, `transfer_mol`, `transfer_with`, what-ifs |
| `rocket_mining_options` | The rocket's miners, drill heads, gas collectors, scanners, holds and tanks, and for each site what that loadout brings home: per cycle, per hour, how long the site and the head last, and why not when it gets nothing. | `rocket_id`, `to` (omit for every site), `collectable_only` |
| `rocket_flight_log` | Records a rocket once a second (or as set) while you fly; reads back a summary and pages of rows, and can write a CSV beside the save. | `action`, `rocket_id`, `interval_s`, `csv`, `offset`, `limit`, `every` |

## Every design the game builds

- **All six engines**, each by the way it draws fuel in the game's code:
  - Pumped Gas Engine: 18 mol a tick at full throttle from its one input, whatever the pressure.
  - Pressure Fed Gas Engine and its heavy version: fuel and oxidiser on two inputs, each giving moles by its own gas
    pressure. As the tanks drain the draw and the thrust sag, and a line that runs out first leaves the other burning
    alone.
  - Pumped Liquid Engine: 0.55 L of liquid a tick at full throttle, shared between its two inputs by its Setting. Liquid
    hydrazine alone works at Setting 100 (the second input still needs a pipe).
  - Pressure Fed Liquid Engine and its heavy version: 0.04 to 0.8 L (1.5 L for the heavy one) a tick, set by the gas
    pressure over the liquid in its first input, plus some of that gas. Its second input is a heat exchanger, not fuel.
- **Gas and liquid lines.** Pipes and tanks hold gas and liquid; liquid takes its room from the gas, so the pressure the
  pressure-fed engines read rises as liquid fills a tank. Tanks share their contents with the pipe by volume every
  tick, as in the game.
- **Mass** is the game's own count: hull, every device, payload bays and their payloads, crew modules, cargo slots
  (1 kg each) and the gas and liquid in the rocket's tanks and pipes. Canisters in tank storage do not count.
- **Pads.** A forecast can land on any launch mount, ground or orbital (`to` or `return_to`). At an Orbital Launch
  Mount the landing check and the descent use 1 m/s² gravity, so a much heavier rocket lands there. `pad` means this
  rocket's own pad when there are several.
- **Crew.** With someone in a Crew Module Chair the game lets the rocket fly only to launch mounts; the forecast
  refuses other targets the same way.
- **Several rockets.** `rocket_status` lists every rocket. At a stop, `transfer_with` another rocket models the
  Transfer action: each pair of matching gas or liquid umbilical sockets evens out the two networks by volume. A pump
  in the receiving rocket moves more than that; give `transfer_mol` for a planned amount instead (negative gives fuel
  away). `transfer_battery_j` does the same for charge.
- **Payloads.** `deploy_payload` at the planet's orbit drops the payloads' mass (200 kg each) for the rest of the trip;
  `payload_kg` tries a payload the rocket does not carry yet.
- **Before building.** `describe_prefab` on an engine prefab gives `engine`: its thrust, exhaust velocity, specific
  impulse and fuel flow on its own test fuel as the game fills them at load, efficiency, mass, chamber volume, its
  feed and each input's role and pipe rating, and the feed's per-tick limits.

## Mining

`rocket_mining_options` applies the game's mining formulas to the loadout:

- A cycle digs `round(baseline × richness^1.6 × type factor)` units, at least 1, where the baseline is 2 to 6 by the
  site's size and ice counts 4 times. A survey past 200 % adds 10 %, past 1,000 % 25 %. The head then multiplies ore by
  its ore factor and ice by its ice factor.
- A cycle takes 10 s at density 0 to 6 s at density 10, divided by the head's speed, in whole half-second ticks.
- **The wrong head still costs.** An ice head at an ore site gets 0 ore, and a mineral head at an ice site gets 0 ice,
  but each cycle still shrinks the site and can wear the head.
- Miners skip gas sites. A Rocket Gas Collector gets gas only there, never shrinks the site, and only evens out with
  its pipe, so a pump must fill a tank.
- A site listing both ore and ice gives only ore.

With `mine` and `park_s`, `rocket_forecast` fills the cargo slots the stop's yield would fill, and reports `mining`:
whether the loadout collects the destination, units per hour, units and slots in the stop, and what is wrong if not.

## How the forecast works

The forecast does not estimate. It runs the game's own rules on a copy of the rocket, tick by tick:

- **The route** is the one the game will fly. The game asks its pathfinder for the next hop at every node it reaches;
  the pathfinder counts hops, not distance, and goes through uncharted nodes. A hop costs its distance in Δv, and that
  distance is twice what the map's coordinates give.
- **Thrust** comes from the engine's own combustion, run on a copy of what each tick draws. Fuel temperature and mix
  therefore count, and so does a fuel-to-oxidiser ratio that drifts as two lines drain. It burns at the game's
  combustion rate (0.96), or at Terraforming Reloaded's when that mod is loaded (its per-world `RocketsBurnCompletely`
  option burns at 1.0); `combustion {rate, source}` in `rocket_forecast` and in `rocket_status`'s thrust says which.
  Draining keeps each line's mix and temperature; the forecast does not follow heat from the air, boiling or
  condensing, and says what 100 K either way would do to the thrust.
- **The landing** starts with the game's confidence check, once, as the re-entry hop begins, with that pad's own hop
  and gravity. A confidence of 0 sends the rocket back to orbit. Otherwise the autopilot's own throttle rules fly it
  down, and touchdown is judged as the game judges it: under 4 m/s is fine, 4 to 25 m/s damages the engines, faster
  explodes the rocket.
- **The confidence check is optimistic.** It uses the highest thrust the rocket has ever recorded, not what the fuel
  gives now. Fuel that has cooled or drifted off its mix, or a pressure-fed tank running low, passes the check and then
  crashes. `rocket_status` flags this as `recorded_exceeds_achievable`; the forecast's `survivable_thrust_drop_pct` says
  how much thrust the landing can lose. The Rocket Control screen's confidence always assumes the ground pad; the
  status's `landing_at_pad` is the check the game makes at the pad the rocket is heading for.
- **Power** is paid every half-second tick from the batteries, device by device. When the batteries run flat the
  engines stop.
- **Cargo** weighs 1 kg per filled slot. The game ignores what is in the slots, so a full hold of ice is 50 or 100 kg.

## Limits and what-ifs

`landing_limits` gives the heaviest landing that still comes down undamaged, the fuel range that lands, the thrust loss
the landing survives, and the heaviest rocket whose confidence stays above `min_confidence` (default 0.25). `profiles`
repeats the landing from 25, 40, 70 and 120 km. `least_fuel_mol` is the least starting fuel for the whole plan.

What-ifs replace part of the rocket before it flies: `fuel_mol`, `fuel_temperature_k`, `fuel_mix` (gas and liquid
names, e.g. `{"LiquidHydrazine": 1}`), `thrust_scale`, `throttle`, `cargo_slots`, `add_cargo_kg`, `payload_kg`,
`battery_j` or `battery_percent`, `extra_load_w`, `profile`.

`column` lists anything standing in the column above the pad that the landing rocket would hit.

## Large replies

`rocket_status` with `compact` leaves out the mass breakdown, the tanks, the power devices and the checks; what the
thrust and landing numbers mean is said once per reply in `explanations`. `parts` is off by default.

## Not covered

- An engine from another mod is refused; its feed law is unknown.
- A landing rocket cannot be given a new target in the forecast. The game would turn it into a launch from its height.
- The tanks' heat exchange with the air, boiling and condensing in the lines, and the Pressure Fed Liquid Engine's heat
  exchanger are not followed (see above).
- A transfer with another rocket assumes both are at the stop and the fuel arriving has this rocket's own mix.
