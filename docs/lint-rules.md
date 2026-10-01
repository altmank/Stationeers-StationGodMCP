# Lint rules

[Back to the README](../README.md)

`lint_layout` checks a room or a box against layout rules, and the place and plan tools check what they are about to
build against the same kind of rules. From 1.7.0 those rules are data: a file, `lint-rules.json`, that you can read,
change, extend and test without touching the mod's code. This page explains the file, the language its rules are
written in, everything a rule can look at, and how to add your own rules for one save.

## Where the rules come from

Two files, read again whenever either changes:

| File | What it is |
| --- | --- |
| `lint-rules.json` in the mod's folder | The rules the mod ships. Updates replace it, so do not edit it; copy rules from it instead. If it is missing, the copy built into the mod is used. |
| `lint-rules.json` in your save's folder (`Documents\My Games\Stationeers\saves\<station>\`) | Your rules for that save. It takes priority. |

How the save's file applies, rule by rule (by `id`):

- A rule with a new id is added, after the mod's rules.
- A rule with an id the mod's file has, and its own `select` or `assert`, replaces the mod's rule whole.
- A rule with an id the mod's file has and no `select` or `assert` changes only what it gives: `enabled`, `level`,
  `level_when`, `on`, `message`, `description`. `{"id": "run_along_door", "enabled": false}` turns a rule off;
  `{"id": "floating_run", "level": "info"}` makes it information only.

A rule that does not load (a typo, a field that does not exist) is left out and reported; every other rule still
runs. `lint_rules` with `action: "validate"` lists every error with its file, rule, field, line, column and the
character in the expression; with `text` it checks a file's content before you save it.

A small save file:

```json
{
  "schema_version": 1,
  "rules": [
    { "id": "run_along_door", "enabled": false },
    {
      "id": "batteries_charged",
      "level": "info",
      "select": "devices where prefab matches 'StructureBattery*'",
      "assert": "(logic(x, 'Ratio') ?? 1) > 0.2",
      "message": "{display_name} ({reference_id}) is at {format((logic(x, 'Ratio') ?? 0) * 100, '0')}%."
    }
  ]
}
```

## Where the rules run

- **`lint_layout`** runs the rules with `"audit"` in their `on` over a room or a box of what stands now. Its reply
  lists the findings (problems first, then warnings, then information, rules in file order) and `rule_source`: the
  files used, the rules the save adds or turns off, and any rule that did not load.
- **The place and plan tools** (`place_cables`, `place_pipes`, `place_chutes`, `place_structure`,
  `plan_cable_route`, `plan_pipe_route`, `plan_chute_route`) run the rules with `"dry_run"` in their `on` on what the
  request would build, in the world around it: the touched 2 m cells and their neighbours, with what the request
  removes or replaces left out. Each finding about a planned thing is a warning `lint_<rule id>`; nothing is refused
  for one, whatever the rule's level. A planned thing knows only its prefab, place, turn, cells and ports: it has no
  network, slots or logic yet. The grid around it is read as it stands now, so a frame or wall the same request also
  places does not yet count as support for its other placements. Removals (`remove_*`, `plan_removal`) are not
  linted.
- **`lint_rules`** lists the rules in effect (`list`), validates (`validate`), shows the model (`fields`) and the
  library (`functions`), runs each rule's examples (`test`) and explains a finding (`explain`): the rule run again on
  one subject, with every value it read, so you can see why it fired.

## A rule

```json
{
  "id": "floating_run",
  "level": "warning",
  "description": "A cable, pipe or chute piece with a cell in air: on no frame and no wall plane.",
  "on": ["audit", "dry_run"],
  "select": "pieces where kind in ['cable', 'pipe', 'chute']",
  "let": { "air": "count(cells, c => c.support == 'air')" },
  "assert": "air == 0",
  "message": "{prefab} {reference_id} floats in air ({air} of {count(cells)} cells on no frame and no wall plane).",
  "examples": {
    "pass": [ { "kind": "cable", "prefab": "StructureCableStraight", "reference_id": 3, "cells": [ { "support": "frame_face" } ] } ],
    "fail": [ { "kind": "pipe", "prefab": "StructurePipeStraight", "reference_id": 3, "cells": [ { "support": "air" } ] } ]
  }
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Lower case letters, digits and `_`. Findings carry it as their `code`. |
| `level` | yes | `problem`, `warning` or `info`. |
| `level_when` | no | An expression giving the level of one finding, e.g. `if(c?.fallback ?? false, 'info', 'warning')`. |
| `on` | no | `["audit"]` (the default), `["dry_run"]` or both. |
| `select` | yes | The subjects: a set, optionally `where <expression>`, or `pairs(<set>, within: <metres>)`. |
| `let` | no | Named expressions, each worked out only when read and able to use the ones before it. |
| `assert` | yes | An expression every subject must hold. A subject for which it is false is a finding. |
| `message` | yes | The finding's text; `{expression}` parts are filled from the subject (`{{` and `}}` are braces). |
| `other` | no | The other thing a finding names (`other_id` in the reply): a thing or an id. A pair names `b` by default. |
| `description` | no | What the rule is for. |
| `examples` | no | `{"pass": [...], "fail": [...]}`: subjects the rule must pass and fail (below). |
| `enabled` | no | `false` turns the rule off. |

### Subjects

| Set | Subjects |
| --- | --- |
| `pieces` | Cable, pipe and chute pieces, and what stands in their slots (in-line tanks, passive vents). |
| `devices` | Small-grid devices and other small-grid things: consoles, sensors, lights, vents, solar panels, machines. |
| `structures` | 2 m structures: frames, walls, windows, doors and plates on the cells' faces, and other 2 m structures. |
| `things` | `pieces`, `devices` and `structures` together. |
| `ports` | The cable, pipe and chute ports of the devices. |
| `cells` | The small cells the pieces and devices stand in. |
| `networks` | The cable, pipe and chute networks of the pieces and ports. |
| `rooms` | The rooms the things stand in. |

In a rule over one set the subject is `x`, and its fields can be named bare: `prefab` is `x.prefab`. A pair rule
reads `a` and `b`: each unordered pair once, of the set's subjects (after its `where`) whose mesh boxes (positions,
for things without one) are no further apart than `within` metres along any axis; `within: 0` means touching or
overlapping. `world` is the world object everywhere.

## The expression language

Expressions are checked when the file loads: every name, field, function and type must exist and fit, so a typo is
an error with its place, not a rule that silently never fires.

| Kind | Written |
| --- | --- |
| Literals | `12`, `0.5`, `'text'` or `"text"`, `true`, `false`, `null`, lists `[1, 2, 3]` |
| Logic | `and`, `or`, `not` (the right side is worked out only when needed) |
| Comparison | `==`, `!=`, `<`, `<=`, `>`, `>=` |
| Arithmetic | `+`, `-`, `*`, `/`, `%`; `+` also joins strings and lists; vecs add, subtract and scale |
| Membership | `x in [..]`, `x not in [..]`, `'part' in text` |
| Patterns | `prefab matches 'StructureSolar*'` (glob: `*` any run, `?` one character, ignoring case), `name matches_regex '^Tank [0-9]+$'` (.NET regular expression) |
| Members | `x.room.id`, `position.y`; `x.f(a)` is the same as `f(x, a)` |
| Index | `gases['Oxygen']` (a gas that is not there reads 0), `list[0]` |
| Nulls | `room?.id` (null when room is), `label ?? display_name`, `has(room)` |
| Names | `let k = 4 in k * k` inside an expression; the rule's `let` for the whole rule |
| Choice | `if(condition, then, else)` |
| Collections | `any(xs, x => ...)`, `all`, `count(xs)` or `count(xs, x => ...)`, `sum`, `min`, `max`, `first`, `map`, `filter`, `flat_map`, `sort_by`, `distinct`; `min(a, b, ...)` and `max(a, b, ...)` of numbers |

Operators bind loosest first: `let`, `or`, `and`, `not`, the comparisons (with `in`, `matches`), `??`, `+ -`,
`* / %`, a leading `-`, then members, indexes and calls.

**Nulls are explicit.** A field whose type ends in `?` may be null and must be guarded before it is read: `room.id`
is refused at load when `room` may be null. Write `room?.id ?? 0`, or guard it: in `has(room) and room.id > 2`,
`not has(room) or room.id > 2` and `if(has(room), room.id, 0)`, `room` reads as not null after the guard
(`room != null` works the same way).

**Nothing throws at run time.** A rule that cannot be worked out on a subject (a value the game does not have, a
function that fails) gives a `rule_error` finding (a warning) naming the rule, the subject and the reason, at most 25
per rule, and the other rules still run.

Types: `bool`, `number`, `string`, `vec` (a point or direction in metres, with `x`, `y`, `z` and `length`),
`list<T>`, `map<T>` (string keys), `T?` (T or null), and the object types below. Numbers are doubles: `1 / 0` is
infinite.

## Examples and self-tests

Each example is a subject written as its fields; nested objects are written the same way (`"cells": [{"support":
"air"}]`). Fields left out are null (or empty lists); reading one that cannot be null is an error. A pair rule's
example is `{"a": {...}, "b": {...}}`. A key with parentheses fixes a function's result on that object instead of
asking the game: `"sun_blocked()": true`, `"logic(Ratio)": 0.05`, `"replaceable()": {"replaceable": false, "rule":
"support", "reason": "..."}`. It works for any function whose first argument is that object. A `"world"` key gives
the world object.

`lint_rules` with `action: "test"` runs every rule's examples (or `rule_id`'s) without the world: a `pass` example
must pass (or be left out by the `where`), a `fail` example must fail. Every shipped rule has both.

## The rules the mod ships

| Rule | Level | Runs on | Finds |
| --- | --- | --- | --- |
| `not_replaceable` | problem | audit | a piece, device or 2 m structure a player could not place again where it stands ([check_replaceable](walls-frames-structures.md#could-a-player-place-it-again-150)) |
| `run_in_door_keepout` | warning | both | a cable, pipe or chute piece in a door's keep-out |
| `port_into_doorway` | warning | both | a device port that joins in a door's keep-out |
| `port_cell_foreign_network` | warning | both | a port whose joining cell holds a piece that does not join it |
| `floating_run` | warning | both | a cable, pipe or chute piece in air (in-line tanks and passive vents are not runs) |
| `run_crosses_window` | warning | both | a piece on a window's face |
| `device_visual_overlap` | warning | both | two devices whose mesh boxes run more than 0.1 m into each other, or one inside the other |
| `mounted_faces_out_of_room` | warning | both | a mounted device facing out of the room behind it |
| `device_crosses_seam` | warning | both | a mounted device spanning two wall sections though it could fit one |
| `controls_blocked` | warning (info for a forward fallback) | both | the side with a device's slots and buttons facing a device, a frame's body, a wall or frame right in front |
| `run_along_door` | info | both | a cable, pipe or chute piece hugging a door's jamb |
| `controls_not_on_wall` | info | both | a console, computer, display, dial, button, switch, lever or keypad not on a wall |
| `replaceable_unchecked` | info | audit | a thing `not_replaceable` could not ask about |
| `solar_outdoor_reinforced` | problem | both | a solar panel storms reach that is not a Reinforced panel (nor one with a WeatherDamageScale of 0): each storm wears the others down |
| `cable_grade_matches_network` | warning | audit | a cable lighter than its network's heaviest while the power through the network is more than its weakest cable carries (the power tick burns a cable rated below that flow) |
| `controller_labels` | warning | audit | a device a chip on its data network batches by name (lbn, sbn, batch_write_name) that carries none of those names, so the chip never reaches it |
| `solar_unshaded` | warning | both | a solar panel with a structure or the terrain between it and the sun somewhere on the day's path |
| `filter_output_capped` | warning | audit | a filtration unit with an output network holding no tank or regulator, or with no chip switching it On and off: it pushes with no back-pressure check |
| `no_oxidiser_vented_outdoors` | problem | audit | an outdoor vent whose network, or anything upstream of it through pumps, valves, regulators, filters and mixers, holds oxygen, nitrous oxide or ozone |
| `fuel_oxidiser_mix_temperature` | warning | audit | a pipe network holding fuel and oxidiser above its auto-ignition temperature |
| `outdoor_liquid_insulated` | warning | audit | an outdoor liquid pipe that is not insulated while its network is at or below a freezing point of what it holds |
| `deep_miner_column_clear` | warning | both | a frame or other full-cell structure in a deep miner's drill column, which stops it |
| `cable_on_frames` | warning | both | a cable with a cell off the frames (in air or along a bare wall) |

`lint_rules` with `action: "list"` and a `rule_id` shows any rule in full.

## The model

Everything a rule reads. `lint_rules` with `action: "fields"` gives the same list from the running mod. Fields are
read from the game the first time a rule asks and kept for the rest of the call.

### thing

Anything built: a cable, pipe or chute piece, a device, a frame, wall, window or door, a large device.

| Field | Type | Meaning |
| --- | --- | --- |
| `reference_id` | `number` | The game's reference id (0 for a planned thing). |
| `prefab` | `string` | Prefab name, e.g. StructureSolarPanel. |
| `prefab_hash` | `number` | Prefab hash (as IC10's HASH("prefab") gives). |
| `display_name` | `string` | The name a player sees: its label when it has one, else its kind's name. |
| `label` | `string?` | The label a labeller gave it; null when none. |
| `name_hash` | `number` | The hash batch operations by name compare (the game's HASH of its DisplayName, as it keeps it). |
| `kind` | `string` | cable, pipe, chute (network pieces), in_line_tank, passive_vent (stand in a pipe's slot), frame, wall, window, door, device (small-grid devices and things), structure (other 2 m structures). |
| `runtime_type` | `string` | The game class, e.g. SolarPanel. |
| `runtime_types` | `list<string>` | The game class and every base class, e.g. ["SolarPanel", "Electrical", "Device", "SmallGrid", "Structure", "Thing", ...]. |
| `grid` | `string` | small (0.5 m grid) or large (2 m grid). |
| `build_state` | `number` | Build state index (0 is the kit; finished is the last). |
| `finished` | `bool` | At its last build state. |
| `broken` | `bool` | A broken structure (a fire-burnt device). |
| `weather_damage_scale` | `number` | How much storm damage it takes (Thing.WeatherDamageScale): 0 takes none, 1 the full amount. |
| `planned` | `bool` | Part of a dry run's plan, not built yet: only its prefab, place, turn, cells and ports are known. |
| `position` | `vec` | Where it stands, metres. |
| `rotation` | `rotation?` | Which way it faces; null when not on the grid's axes. |
| `mesh_box` | `box?` | The box its meshes fill. |
| `cells` | `list<cell>` | The cells it registers in: small cells for small-grid things, 2 m cells for 2 m structures. |
| `room` | `room?` | The room it stands in; null outdoors or in no room. |
| `outdoors` | `bool` | It stands in no room. |
| `slots` | `list<slot>` | Its slots. |
| `mounted` | `mount?` | How it sits on its plane, for a face-mounted thing; null otherwise. |
| `network` | `network?` | The network a cable, pipe or chute piece belongs to. |
| `networks` | `list<network>` | Every network it is on: a piece's own; a device's cable networks (power and data) and the networks its ports join. |
| `ports` | `list<port>` | Its cable, pipe and chute ports. |
| `flow` | `string?` | What it does to a pipe flow from its inputs to its outputs: pump, valve, regulator, filter, mixer; null for anything else. |
| `grade` | `string?` | A cable piece's grade (normal, heavy, super_heavy); a pipe piece's (normal, insulated, normal_low_volume, insulated_low_volume, duct). |
| `max_power` | `number?` | A cable piece's rating in watts: more through it burns it. |
| `max_pressure` | `number?` | A pipe piece's rating in kPa: more in it bursts it. |
| `insulated` | `bool` | An insulated pipe piece. |
| `content` | `string?` | A pipe piece's content: gas or liquid (null for anything else). |
| `chip` | `chip?` | The program chip it holds (an IC housing, a Lua circuit); null when none. |

### port

One cable, pipe or chute connection of a device.

| Field | Type | Meaning |
| --- | --- | --- |
| `device` | `thing` | The thing the port belongs to. |
| `index` | `number` | Its index on the device, as connections and grid_survey number ports. |
| `type` | `string` | The game's network type: Power, Data, PowerAndData, Pipe, PipeLiquid, Chute. |
| `role` | `string` | The game's connection role: Input, Input2, Output, Output2, Waste, None and others. |
| `flow` | `string?` | in for inputs, out for outputs and waste, null for a role with no direction. |
| `joining_cell` | `cell` | The cell a piece joining the port stands in. |
| `network` | `network?` | The network joined to it; null when nothing is. |
| `occupant` | `thing?` | The piece of the port's kind standing in the joining cell; null when none. |
| `joined` | `bool` | occupant joins the port (has an end toward it). |
| `position` | `vec` | The joining cell's centre. |

### network

A cable, pipe or chute network.

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | `number` | The network's id. |
| `kind` | `string` | cable, pipe or chute (a liquid pipe network is a pipe network with content liquid). |
| `members` | `list<thing>` | Its pieces (cables, pipes, chutes and what stands in their slots). |
| `devices` | `list<thing>` | The devices with a port on it. |
| `chips` | `list<chip>` | The program chips in housings on it (a cable network). |
| `grades` | `list<string>` | The cable grades it is made of, lightest first. |
| `min_cable_power` | `number?` | Its weakest cable's rating in watts; null for a pipe or chute network. |
| `max_cable_power` | `number?` | Its strongest cable's rating in watts. |
| `load` | `number?` | Watts it delivered last power tick (CableNetwork.CurrentLoad). |
| `required_load` | `number?` | Watts its consumers ask for (CableNetwork.RequiredLoad). |
| `potential_load` | `number?` | Watts its suppliers can give (CableNetwork.PotentialLoad). The power tick burns a cable rated below min(potential_load, required_load). |
| `content` | `string?` | A pipe network's content: gas or liquid. |
| `gases` | `map<number>` | Moles by gas name (the game's Chemistry.GasType names: Oxygen, Nitrogen, CarbonDioxide, Methane, Pollutant, NitrousOxide, Water, Hydrogen, Ozone, LiquidNitrogen ...) for a pipe network; empty otherwise. |
| `total_mol` | `number` | Total moles. |
| `pressure` | `number?` | kPa. |
| `temperature` | `number?` | Kelvin. |
| `max_pressure` | `number?` | Its weakest pipe's rating in kPa. |
| `volume` | `number?` | Litres. |
| `outdoors` | `bool` | Any member stands outdoors. |
| `position` | `vec` | Its first member's position. |

### cell

A 0.5 m small-grid cell (size 0.5), or a 2 m cell (size 2) where a 2 m structure registers.

| Field | Type | Meaning |
| --- | --- | --- |
| `position` | `vec` | The cell's centre, metres. |
| `size` | `number` | 0.5 for a small cell, 2 for a 2 m cell. |
| `large` | `cell` | The 2 m cell it lies in (itself for a 2 m cell). |
| `support` | `string` | What holds a piece there: inside_frame (in a frame's body), frame_face (on a frame's surface, edge or corner), wall_plane (on a wall or window's plane), air (nothing). |
| `in_door_keepout` | `bool` | In a door's keep-out (its face and the configured band either side). |
| `keepout_door` | `thing?` | The door whose keep-out it is in. |
| `on_window_face` | `bool` | On a window's face. |
| `window` | `thing?` | The window whose face it is on. |
| `door_jamb` | `thing?` | The door whose jamb band it lies in: on the door's plane band just past its side edges, within its height (walls only; floor and ceiling doors have none). |
| `room` | `room?` | Its room; null outdoors. |
| `outdoors` | `bool` | In no room. |
| `pieces` | `list<thing>` | Cable, pipe and chute pieces in it. |
| `devices` | `list<thing>` | Devices and other small-grid things in it. |
| `frame` | `thing?` | The frame filling its 2 m cell. |
| `blocker` | `thing?` | The structure filling its 2 m cell with BlockGrid collision (a frame, a large full-cell structure): what stops a deep miner's drill. |
| `walls` | `list<thing>` | Walls, windows and doors on its 2 m cell's six faces. |

### room

A room the game has found: cells sealed from the outside.

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | `number` | The room's id. |
| `cell_count` | `number` | Its 2 m cells. |
| `pressure` | `number?` | kPa, at its first cell. |
| `temperature` | `number?` | Kelvin, at its first cell. |
| `gases` | `map<number>` | Moles by gas name, at its first cell. |
| `devices` | `list<thing>` | The devices and 2 m structures standing in it, among the things the lint call reads. |
| `position` | `vec` | Its first cell's centre. |

### slot

One slot of a thing.

| Field | Type | Meaning |
| --- | --- | --- |
| `index` | `number` | The slot's index. |
| `name` | `string` | The slot's name. |
| `type` | `string` | The slot's type (the game's Slot.Class). |
| `occupant` | `string?` | The prefab name of what is in it; null when empty. |
| `occupant_name` | `string?` | Its display name. |
| `quantity` | `number` | How many (a stack's quantity, 1 for a single item, 0 when empty). |

### world, sun, atmosphere

| Type | Field | Type | Meaning |
| --- | --- | --- | --- |
| world | `sun` | `sun` | The sun. |
| world | `day_length` | `number` | Seconds in a day. |
| world | `outdoor` | `atmosphere` | The planet's air, where no room is. |
| sun | `direction` | `vec` | Unit vector toward the sun now. |
| sun | `up` | `bool` | Above the horizon now. |
| sun | `path` | `list<vec>` | Unit vectors toward the sun at even steps over one day (sun_blocked tests them). |
| atmosphere | `gases` | `map<number>` | Moles by gas name. |
| atmosphere | `pressure` | `number` | kPa. |
| atmosphere | `temperature` | `number` | Kelvin. |

### rotation, box, mount

| Type | Field | Type | Meaning |
| --- | --- | --- | --- |
| rotation | `facing` | `string` | Its forward: +x, -x, +y, -y, +z, -z. |
| rotation | `up` | `string` | Its up. |
| box | `min`, `max`, `centre`, `size` | `vec` | The box's corners, centre and size, metres. |
| mount | `plane` | `string` | The face plane behind it, e.g. z=668. |
| mount | `outward` | `string` | Which way it faces out of the plane: +x ... -z. |
| mount | `back` | `cell` | The 2 m cell behind its mesh rectangle's centre, through the plane. |
| mount | `front` | `cell` | The 2 m cell in front of it. |
| mount | `sections` | `number` | How many 2 m wall sections its mesh rectangle spans (more than 0.1 m into each). |
| mount | `fits_one_section` | `bool` | Neither side of its rectangle is wider than 2.2 m: some shift puts it on one section. |

### chip, batch

| Type | Field | Type | Meaning |
| --- | --- | --- | --- |
| chip | `housing` | `thing` | The housing, or the device holding the circuit. |
| chip | `language` | `string` | ic10 or lua. |
| chip | `source` | `string` | The program. |
| chip | `pins` | `list<thing?>` | The devices on the housing's pins d0, d1 ... (null where none is set). |
| batch | `chip` | `chip` | The chip whose program has it. |
| batch | `op` | `string` | The instruction (lb, lbn, lbs, lbns, sb, sbn, sbs) or the Lua call. |
| batch | `prefab_hash` | `number?` | The prefab hash it names; null when not a constant. |
| batch | `prefab` | `string?` | The prefab name inside HASH("..."), when written so. |
| batch | `name_hash` | `number?` | The name hash; null when it names none, or not a constant. |
| batch | `name` | `string?` | The name inside HASH("..."), when written so. |
| batch | `logic` | `string?` | The logic type it reads or writes. |
| batch | `writes` | `bool` | It writes rather than reads. |

### placement, controls

| Type | Field | Type | Meaning |
| --- | --- | --- | --- |
| placement | `replaceable` | `bool?` | true: a player could place it again; false: refused; null: not checked. |
| placement | `rule` | `string?` | Which rule refused it: support, mount, host, location, adjacent, collision, rotation, no_kit, off_grid. |
| placement | `reason` | `string` | The game's reason, or why it was not checked. |
| controls | `side` | `string` | The world side its controls face: +x ... -z. |
| controls | `fallback` | `bool` | No control sits clearly on one side, so its forward stands in. |
| controls | `source` | `string` | What decided the side. |

## The library

`lint_rules` with `action: "functions"` lists every form and function with its signature. Functions marked cached
keep their result for the rest of the call when asked again with the same arguments.

| Function | What it gives |
| --- | --- |
| `replaceable(x: thing) -> placement` | Whether a player could place the thing again where it stands, its neighbours present: check_replaceable's rule. `replaceable` is null when it could not be asked, and for a planned thing. Cached. |
| `controls_side(x: thing) -> controls?` | The world side of a device that carries its slots, buttons and switches, read from its prefab's interactables (describe_prefab's controls). |
| `controls_blocked(x: thing) -> string?` | What stands right in front of the side its controls face (another device, a chute or small thing, a frame's body, a wall or frame on the plane in front), as text; null when that side is clear, when it has no control side, or when that side is a face-mounted thing's front. Cached. |
| `sun_blocked(x: thing) -> bool` | Something stands between the thing and the sun somewhere on the day's path while the sun is more than 10 degrees up: the solar arm's own five rays with the panel's collision mask, from each arm's cells turned to the sun, and the terrain; from the mesh box's centre for anything else or a planned panel. 36 sun directions over the day. Cached. |
| `weather_exposed(x: thing) -> bool` | Storms reach the thing: the air in its cell is the planet's or within 1 kPa of it, or it has no air and no room (the test the game applies before storm damage). |
| `logic(x: thing, type: string) -> number?` | The thing's logic value of a type (On, Setting, Pressure, Ratio ...), as a Logic Reader reads it; null when it cannot be read. |
| `freezing_point(gas: string) -> number?` | Kelvin at or below which the gas or liquid freezes in a pipe; null for one that cannot freeze. |
| `auto_ignition_temperature(gases: map<number>) -> number?` | Kelvin above which the mix burns by itself: methane or hydrogen over 1 mol 573.15 K, liquid alcohol 673.15 K, each 250 K lower with over 1 mol of nitrous oxide or 150 K with ozone; hydrazine its critical temperature. Null when no fuel is over 1 mol. |
| `drill_column(x: thing) -> list<cell>` | The 2 m cells a deep miner drills through: from its own cell straight down while the cell reaches above y 0. |
| `mesh_overlap(a: thing, b: thing) -> number` | How deep two things' mesh boxes run into each other on the shallowest axis, metres: infinite when one holds the other, 0 or less when they only touch or are apart. A thin panel wholly inside the other's extent on an axis clashes however thin it is. |
| `shares_cell(a: thing, b: thing) -> bool` | The two register in a common cell (a device on a pipe). |
| `seam_sections(x: thing) -> number?` | How many 2 m wall sections a face-mounted thing's mesh rectangle spans; null when it is not face-mounted. |
| `distance(a: any, b: any) -> number` | Metres between two places: vecs, or things, cells, ports, networks (their positions). |
| `cells_of(x: any) -> list<cell>` | The cells of a thing, a port (its joining cell), a network (its members' cells) or a cell (itself). |
| `neighbors(c: cell, axis?: string) -> list<cell>` | The cells beside a cell, one cell size away: all six, along one axis (x, y, z) or on one side (+x ... -z). |
| `upstream(n: network) -> list<network>` | The pipe networks that can feed this one through pumps, valves, regulators, filters and mixers (a device's inputs, when one of its outputs is on the network), followed on; not the network itself. A valve with no direction passes both ways. Cached. |
| `downstream(n: network) -> list<network>` | The pipe networks this one can feed the same way. Cached. |
| `chip_batch_names(n: network) -> list<batch>` | The batch operations the program chips on this data network make (IC10 lb, lbn, lbs, lbns, sb, sbn, sbs; Lua batch_read, batch_write and their _slot and _name forms), with the prefab and name hashes they name. Cached. |
| `chip_writes(x: thing, logic: string) -> bool` | Some chip on one of the thing's data networks writes the logic type to it: by batch to its prefab (sb, or sbn with its name), through a pin set to it (s dN), or by its id (sd). Cached. |
| `hash(s: string) -> number` | The game's HASH("..."): the hash IC10 gives a prefab name or a label. |
| `is_fuel(gas: string) -> bool`, `is_oxidiser(gas: string) -> bool` | Whether the game burns the gas as fuel (Methane, Hydrogen, LiquidAlcohol, Hydrazine and their liquids) or burns fuel with it (Oxygen, NitrousOxide, Ozone and their liquids). |
| `mol_of(gases: map<number>, names: list<string>) -> number` | The moles of the named gases together. |
| `keys(m: map<any>) -> list<string>` | A map's keys, sorted. |
| `len`, `contains`, `starts_with`, `ends_with`, `lower`, `upper`, `trim_end`, `str`, `format`, `join` | Text: `format(1.234, '0.00')` is `1.23`; `join(xs, ', ')`; `str(x)` writes any value as a message would. |
| `abs`, `round`, `floor`, `ceil`, `sqrt`, `is_infinite` | Numbers. |

The forms are part of the language: `has`, `if`, `any`, `all`, `count`, `sum`, `min`, `max`, `first`, `map`,
`filter`, `flat_map`, `sort_by`, `distinct`.

## Explaining a finding

`lint_rules` with `action: "explain"`, a `rule_id` and the subject runs that one rule again on it in the world around
it and returns its outcome (skipped by the `where`, passed, failed, error), the message and level, and every value it
read: each field, call and `let` with the values it had (up to four inside a lambda). Name the subject with
`reference_id` (a thing, a network or a room; `port` for one port of a device; `other_id` for the other of a pair),
or `at` for a cell.

```json
{ "action": "explain", "rule_id": "floating_run", "reference_id": "171026" }
```

## Adding a function (maintainers)

A new need is either a rule in the language or one new library function; the model and the engine do not change.
A function is one registration: in `LintStandardFunctions` (or a class it calls) when it needs nothing but the model,
in `Api/Shared/Game/Lint/LintGameLibrary.cs` when it asks the game:

```csharp
library.Add(new LintFunction("is_powered", "(x: thing) -> bool",
    "The thing has power now.",
    call => LintValue.Of(call[0].AsObject is ThingSubject thing && thing.Source is Device device && device.Powered),
    cached: false));
```

The signature is checked at load like any expression: rules that call the function with the wrong types are refused
with their position. `lint_rules functions` lists it at once. A function never throws past the engine: a
`LintEvaluationException` (or any other exception) becomes a `rule_error` finding for that subject. Keep game
functions on the main thread's data (a lint call runs in the game's update loop) and mark expensive ones `cached`.
A new field on an object type is added the same way, in `LintModel` (its type and meaning) and the subject class that
reads it from the game.
