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
| `move_gas` | Move gas and liquid between atmospheres, or delete it. A cheat: it bypasses the game's physics. | `from`, `to` or `delete`, `gases`, `amount_mol`, `joined`, `force`, `dry_run`; `transfer_id` to poll |
| `outer_frames` | Frames with a face on the planet's outside air. | `near_player_m`, `include_inner`, `limit`, `offset` |
| `planet` | The planet's atmosphere: pressure, temperature and its parts, every gas, today's and the orbit's temperature range, ice and cloud reservoirs. | none |
| `weather` | The storm schedule, when the next event can come, every event this world can roll, and the season. | none |
| `plants` | Every plant in a tray, planter or station: growth stage, health, problems in plain words, needs, and forecasts to the next stage, harvest and seeds. | `reference_id`, `include_unplanted` |
| `plant_genes` | Read or edit the genes of plants, seeds and produce, as the Gene Splicer does. | `reference_id` or `reference_ids`, `genes`, `unit`, `force` |
| `reagents` | What a furnace, centrifuge, mixer or microwave holds, reagent by reagent (logic only gives the total). | `reference_id` |
| `player_vitals` | Your hunger and thirst: stores, capacities, drain rates and time until empty, awake and asleep. | none |
| `ignition_risk` | Whether anything you carry would catch fire in the air around you, by the game's fire rule. | `include_prefabs` |
| `thing_health` | The damage of any thing, or every damaged or broken thing in the world, broken first, then worst first. | `reference_id`, `reference_ids`, or none; `structures_only`, `broken_only`, `min_damage_ratio` |

## Rooms

A room is the game's closed flood fill of 2 m cells, at most 1200 cells; a bigger or open space is outside and has no
room. `rooms` sums each room's air fresh from its cells, as the game pools it. Sample `thermal_energy_j` twice: its
change over the time between is the room's net heat flow in watts.

`outer_frames` finds the frames that face the outside: a face counts as exposed when the cell across it is in no room
and can hold air. A sealed space bigger than 1200 cells has no room, so frames facing into it count as outer too.
`build_state` is the frame's build state index, from 0 (bare) up to its finished state, which differs by frame (a
finished steel frame is 3); `blocks_air` says whether that state holds air.

`water_sources` lists a new pipe network at once, even while the game is paused. A thing's own atmosphere made since
the last atmospherics tick (a canister spawned while paused) shows from the next tick on.

## Moving gas

`move_gas` is a cheat: it bypasses the game's physics. Gas and liquid jump from one atmosphere to another with no
pipe, pump or valve between them, no flow time and no power, which nothing a player builds can do. It uses the game's
own gas calls: each gas leaves with its share of the heat and arrives with it.

- `from` and `to` are reference ids of a canister, portable tank, tank or suit, a pipe (its network), any landing pad
  piece (the pad's shared atmosphere), a pipe or landing pad network id, or an atmosphere id from
  `atmosphere_contents`, or a room (below). A single world cell is refused. `from: "planet"` with `delete: true` and
  named `gases` takes those gases out of the planet's own air (the mix Terraforming Reloaded reads) and its clouds and
  ice caps; it needs Terraforming Reloaded (`terraforming_mod_required`, after the arguments are checked).
- `gases` names the gases (`Oxygen`, `Nitrogen`, `CarbonDioxide`, `LiquidOxygen`, `Steam`...); omit for all of them.
  `amount_mol` caps each; omit for all of it. `delete: true` instead of `to` destroys the gas.
- **Joined sets:** the game keeps some atmospheres at one mix every tick, for example a Gas Tank Storage's canisters and
  its pipe network, or two networks joined by an open valve. By default (`joined: true`) gas is taken from every member
  in proportion, so it does not flow back. `joined: false` moves between the named atmospheres only.
- **Burst check:** refused with `would_burst` when a side's settled pressure would exceed any member's rating. The
  receiving side is also refused when the move makes it worse by one of the game's matter rules: liquid over 2% of a
  gas pipe network's volume, gas or liquid freezing in a network, or the pressure once arriving liquid has boiled
  (`total.after_boiling` in the reply shows that state; it is null unless every liquid that can boil would boil
  away, so a liquid that stays liquid, or whose boiling would cool it to its freezing point first, leaves it null). Into a room, liquid the room's air would lose is refused the
  same way: a room's cells freeze any amount out of their air (ice per 50 mol in a cell, smaller amounts held out of
  the air), so the move is refused when more would freeze than before, or when an arriving liquid would sit under its
  minimum liquid pressure (6.3 kPa of gas for water) in a room without the heat to boil it all, where it keeps
  evaporating and cooling the room until the rest freezes. Boiling that would cool the room under the liquid's
  freezing point counts as not having the heat. `force` skips every check.
- **Timing:** the game changes gas only on its atmospherics thread, so the move is queued (`status: queued`) and applied
  at the next atmospherics tick, about half a second later, never while paused. The reply is the prediction; call again
  with only `transfer_id` for the outcome: `queued` until it has been applied, then `applied` or `failed` (with
  `error.code` `nothing_to_move` when the source held none of the gases by then, `atmosphere_not_found` when an
  atmosphere was destroyed first, `move_failed` when the game's gas call threw). `transfer_not_found` means an id never
  issued, or older than the last 64 outcomes. `dry_run: true` returns the same prediction after the same checks and
  queues nothing (`status: dry_run`, no `transfer_id`). While a move waits, `upgrade_pipes` and `clean_pipes` on a
  network it takes from or gives to answer `atmosphere_busy`: wait for the tick and try again.
- **Rooms:** `{"room_id": "<id>"}` (the `room_id` from `rooms`) or `{"room_of": "<reference id>"}` (the room that
  thing is in; the player's id gives the room you stand in, and an item in a slot is in its outermost holder's room) as `from` or `to`. The game keeps no room-wide
  atmosphere, only one per 2 m cell, so a room here is every cell of it that has air of its own; cells without are
  left out, and a room with none is refused `no_atmosphere`. From a room, `gases` is required, so its breathable air
  is never emptied by leaving `gases` out. Each named gas is taken from every cell in proportion to what the cell
  holds (`amount_mol` caps the room's total), each with its share of heat, in one tick. Into a room, each gas is
  spread over the cells by volume, so the room is at once at the mix the game would settle it to, instead of one cell
  spiking to many times the room's pressure while the game spreads it. A room has no burst rating; the tank or network
  on the other side keeps every check. The reply's side has `room {room_id, room_type, cell_count, cells_with_air,
  volume_l}`, no per-cell `members`, and `total.before` / `total.after` are the room's air, pressure included, over
  its cells with air (the `rooms` tool also counts cells without air of their own at the planet's air, so it can
  differ slightly).
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

Clear the nitrous oxide out of the room you stand in into a tank (`rooms` gives `local_player_room_id`):

```json
{ "from": { "room_id": "5321" }, "to": "124640", "gases": ["NitrousOxide"] }
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
- `condition` says it in one word: `broken`, `damaged`, `intact`, `indestructible` or `none`. `broken` is the game's own
  broken state (`is_broken`), and it wins over the numbers: the game heals a structure when it breaks it, so a
  burnt-out vent reads 0 damage and 100 % health. A burst pipe (`pipe_burst` not `none`) is broken too: bursting does
  not damage it, so it also reads 0 damage. The scan lists broken things whatever their numbers;
  `broken_only: true` lists only them. Structures also report `broken_build_state`, their Labeller name
  (`custom_name`) and the cable, pipe and chute networks they are on (`networks`). `remove_structure` with
  `allow_broken` removes them.
