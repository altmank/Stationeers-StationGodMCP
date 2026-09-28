# Air, planet, plants and survival

[Back to the README](../README.md)

Rooms and their air, gas in tanks and pipes, the planet's atmosphere and weather, plants and their genes, and the
player's own needs. Everything here is read from the game's own state and formulas; only `move_gas` and `plant_genes`
change anything. No gateway is needed.

Units: pressure in kPa, temperature in kelvin, gas in moles, liquids also in litres, time in game seconds.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `rooms` | Every closed room, measured cell by cell: volume, pressure, temperature, every gas, its devices. | `reference_id` (only the room that thing is in), `include_cells`, `include_devices` |
| `atmosphere_contents` | What gas or liquid one thing holds: a canister, tank, suit, a pipe's whole network, a landing pad's shared atmosphere, or every network a device is on. | `reference_id` |
| `water_sources` | Every canister, tank, device and pipe network that holds water, polluted water or steam, largest first. | `min_mol` |
| `move_gas` | Move gas and liquid between atmospheres, or delete it. | `from`, `to` or `delete`, `gases`, `amount_mol`, `joined`, `force`; `transfer_id` to poll |
| `outer_frames` | Frames with a face on the planet's outside air. | `near_player_m`, `include_inner`, `limit`, `offset` |
| `planet` | The planet's atmosphere: pressure, temperature and its parts, every gas, today's and the orbit's temperature range, ice and cloud reservoirs. | none |
| `weather` | The storm schedule, when the next event can come, every event this world can roll, and the season. | none |
| `plants` | Every plant in a tray, planter or station: growth stage, health, problems in plain words, needs, and forecasts to the next stage, harvest and seeds. | `reference_id`, `include_unplanted` |
| `plant_genes` | Read or edit the genes of plants, seeds and produce, as the Gene Splicer does. | `reference_id` or `reference_ids`, `genes`, `unit`, `force` |
| `reagents` | What a furnace, centrifuge, mixer or microwave holds, reagent by reagent (logic only gives the total). | `reference_id` |
| `player_vitals` | Your hunger and thirst: stores, capacities, drain rates and time until empty, awake and asleep. | none |
| `ignition_risk` | Whether anything you carry would catch fire in the air around you, by the game's fire rule. | `include_prefabs` |
| `thing_health` | The damage of any thing, or every damaged thing in the world, worst first. | `reference_id`, `reference_ids`, or none; `structures_only`, `min_damage_ratio` |

## Rooms

A room is the game's closed flood fill of 2 m cells, at most 1200 cells; a bigger or open space is outside and has no
room. `rooms` sums each room's air fresh from its cells, as the game pools it. Sample `thermal_energy_j` twice: its
change over the time between is the room's net heat flow in watts.

`outer_frames` finds the frames that face the outside: a face counts as exposed when the cell across it is in no room
and can hold air. A sealed space bigger than 1200 cells has no room, so frames facing into it count as outer too.

## Moving gas

`move_gas` uses the game's own gas calls: each gas leaves with its share of the heat and arrives with it.

- `from` and `to` are reference ids of a canister, portable tank, tank or suit, a pipe (its network), any landing pad
  piece (the pad's shared atmosphere), a pipe or landing pad network id, or an atmosphere id from
  `atmosphere_contents`. Rooms and world cells are refused. `from: "planet"` with `delete: true` and named `gases` takes those gases out of the
  planet's own air (the mix Terraforming Reloaded reads) and its clouds and ice caps; it needs Terraforming Reloaded.
- `gases` names the gases (`Oxygen`, `Nitrogen`, `CarbonDioxide`, `LiquidOxygen`, `Steam`...); omit for all of them.
  `amount_mol` caps each; omit for all of it. `delete: true` instead of `to` destroys the gas.
- **Joined sets:** the game keeps some atmospheres at one mix every tick, for example a Gas Tank Storage's canisters and
  its pipe network, or two networks joined by an open valve. By default (`joined: true`) gas is taken from every member
  in proportion, so it does not flow back. `joined: false` moves between the named atmospheres only.
- **Burst check:** refused with `would_burst` when a side's settled pressure would exceed any member's rating. The
  receiving side is also refused when the move makes it worse by one of the game's matter rules: liquid over 2% of a
  gas pipe network's volume, gas or liquid freezing in a network, or the pressure once arriving liquid has boiled
  (`total.after_boiling` in the reply shows that state). `force` skips every check.
- **Timing:** the game changes gas only on its atmospherics thread, so the move is queued (`status: queued`) and applied
  at the next atmospherics tick, about half a second later, never while paused. The reply is the prediction; call again
  with only `transfer_id` for the outcome.
- To undo, move each gas back; it returns at the other side's temperature by then.
- Host only (`not_host` on a client).

### Landing pads

Every piece of one landing pad (tiles, gas storage tanks, the tank connectors, the data and power connection) shares
one atmosphere, the pad network's. Name any piece, or the network's id, in `atmosphere_contents` or `move_gas`. Its
volume is 500 L per gas storage piece and 1 L per other piece. It holds gases and liquids alike and never changes their
state, so liquid ozone stays liquid there, but boils once moved into a warm pipe network: check `after_boiling`.

Empty a canister's oxygen into a pipe network, `move_gas`:

```json
{ "from": "171002", "to": "169540", "gases": ["Oxygen"] }
```

## Planet and weather

`planet` reads the atmosphere every outdoor cell relaxes toward, with the parts of its temperature (sun angle, distance,
greenhouse, density, weather), the coldest and warmest the game's formula gives today and across the orbit, and which
gases would condense. With Terraforming Reloaded loaded it also reports
that mod's state.

`weather` gives the storm schedule from the game's weather manager: the current or scheduled event with its start and
length, the days until a new one can be scheduled, and every event the world can roll. The game picks an event at
random the moment both of its day rules pass, so the schedule is exact from that moment.

## Plants

`plants` reads each plant itself, so it works for trays with no data port. Problems come in plain words (dry, too cold,
harmful gas), each with how long it has lasted against the time after which it starts damaging the plant. Forecasts
use the plant's current growth efficiency. `include_unplanted` adds harvested crops and seed bags.

`plant_genes` reads all 19 genes, each with what it does and the stat it sets now and at both ends of its range.
Writing (`genes: {"GrowthSpeedMultiplier": 0.8}`) edits the gene set planting copies, as the Gene Splicer does; values
stay within -1 to 1 unless `force` is given. Edits are saved and inherited by harvests and seeds like natural genes.
Host only.

## Survival

- `player_vitals` computes hunger and thirst drain with the game's own per-tick formulas, right now and when up and
  about, with the time until each store runs out. Food and water you carry are in `consumables` and `water_sources`.
- `ignition_risk` checks every burnable thing you carry against the air of your cell: its effective flashpoint at this
  pressure, its autoignition temperature, and whether it would light now.
- `thing_health` reads the game's damage state, which no logic type exposes. `damage_ratio` 0 is like new, 1 destroyed;
  `health_percent` matches the solar panel tooltip. A solar panel generates (1 - `damage_ratio`) of its full output.
  Indestructible things report `null`, never a false 0.
