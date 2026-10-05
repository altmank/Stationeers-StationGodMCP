# Devices, logic and console

[Back to the README](../README.md)

These tools read and write devices the way IC10 and the Configuration Cartridge do, through logic values, slots and
memory, but for any device in the world at once and without a chip.

## Identifying things

- **Reference ids** are the game's own ids for things, passed and returned as strings (`"132000"`). `list_devices`,
  `find_things` and `looking_at` give them; every other tool takes them.
- **Logic types** are given by name, as IC10 writes them (`Setting`, `On`, `Pressure`, `Color`), or by numeric id
  (0 to 65535).
- **Scope:** every device tool reaches every device in the world. Omit `gateway_id`, or pass `world` (an empty
  `gateway_id` counts as omitted, and spaces around an id are ignored). A StationGod Gateway id narrows a call to that
  gateway's data networks (see below).

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `list_devices` | Every device in the world, or on one gateway's networks. | `name_contains`, `prefab_hash`, `gateway_id` |
| `describe_device` | One device and every logic type it can read or write; for a rocket's device its rocket, for an umbilical its pairing, for a Logic Rocket Uplink its downlink (see *Rockets and umbilicals*). | `reference_id` |
| `set_uplink` | Point a Logic Rocket Uplink at a downlink, as a screwdriver would. | `reference_id`, `downlink_id` |
| `read_logic` | Read one logic value. | `reference_id`, `logic_type` |
| `write_logic` | Write one logic value; the reply reads it back at once (`current_value`). | `reference_id`, `logic_type`, `value` |
| `read_logic_many` | Up to 256 reads in one call; each gets its own result or error. | `reads: [{reference_id, logic_type}]` |
| `write_logic_many` | Up to 256 writes in order, in one call. | `writes: [{reference_id, logic_type, value}]` |
| `read_devices` | A control loop's whole read in one call, all in one frame (1.10.0+): logic, slot logic, an atmosphere and reagents of up to 128 things, plus the clock (see *One read per tick*). | `items: [{reference_id, logic, slots, atmosphere, reagents}]`, `include: ["clock"]` |
| `read_memory` | Up to 512 consecutive values from a device with memory (an IC Housing's or suit's chip stack, a Logic Sorter, a satellite dish, a fabricator; a Logic Memory has only `Setting`). | `reference_id`, `start_address`, `count` |
| `write_memory` | Up to 512 consecutive values into such a device. | `reference_id`, `start_address`, `values` |
| `inspect_slots` | A device's slots, what is in them, what each slot takes, and every slot logic value. Changes nothing. Devices only: for a crate, the lander or a tool use `container_contents`. | `reference_id`, `slot_index` |
| `network_snapshot` | Many devices and their logic values at one instant, in one game frame. | `reference_ids`, `prefab_hash`, `name_contains`, `logic_types`, `max_devices` (default 2) |
| `sample_logic` | Record up to 32 values for up to 30 seconds; the first readings plus every change, timestamped. | `targets: [{reference_id, logic_type}]`, `duration_seconds` (default 5), `interval_seconds` (default 0.5) |
| `connections` | A pipe, cable, chute or device's ends and what each joins; or every member of a network with its load or contents, 30 a page, or `summarize` for counts by prefab and colour. | `reference_id`, or `network_id` with `kind` (filters `prefab_contains`, `open_ends_only`, an area: `min` and `max`, or `near` with `radius_m`), or `min` and `max` alone: open ends in a box |
| `list_gateways` | The scopes device tools accept: `world` and every StationGod Gateway. | none |
| `looking_at` | What your crosshair is on, and the button, switch, port or slot under it; where you look from and which way (1.4.3+), the surface the look ray hits and the grid there, and the target's body. | `max_distance_m` |
| `game_clock` | Game time, paused or not, time of day, days past. | none |
| `run_console_command` | Any console command, with the lines it printed. | `command`, `max_output_lines` |
| `set_battery_charge` | A cheat: set the charge of single batteries, full or to a ratio or joules (see *Batteries*). | `reference_ids`, or `rocket_id`, or `in_id`; `ratio` or `joules`; `dry_run`, `confirm` |
| `read_console` | The latest console lines, including Unity errors and stack traces. | `lines` |
| `mod_info` | Mod version, pipe name, call statistics per method, and every game member the mod relies on. | none |

## One read per tick: `read_devices` (1.10.0+)

A script that reads the same devices every tick (a furnace card, a print queue) can make one call where it made one
per device, slot and pipe network. Every item is read in the same game frame (one update; the atmospherics thread may
still run between two items, so it is one frame, not one atmospherics tick).

```json
{"include": ["clock"],
 "items": [
   {"reference_id": "811234", "logic": ["Temperature", "Pressure", "RatioOxygen", "280"],
    "slots": [{"index": 0, "logic": ["Occupied", "OccupantHash", "Quantity"]}],
    "atmosphere": {"of": "internal"}, "reagents": true},
   {"reference_id": "811410", "atmosphere": {"port": 1}},
   {"reference_id": "811300", "slots": [{"index": 0}]}]}
```

Each item takes a `reference_id` and at least one part:

- `logic`: up to 64 logic types, read as `read_logic` reads them.
- `slots`: up to 16 `{index, logic}`; slot logic as a chip's `ls` reads it. Without `logic`, every slot logic type
  the slot reads, under the names `inspect_slots` gives them (`Occupied`, `Quantity`...).
- `atmosphere`, compact (pressure, temperature, total moles, volume, liquid volume, and each gas or liquid held):
  `{}` the id's own (a thing's internal atmosphere, a pipe's or landing pad piece's network, a pipe network id, an
  atmosphere id); `{of: "internal"}` only the thing's internal one; `{port: n}` the pipe network joined at a device's
  port `n`, the end index `connections` lists. A port follows the network through rebuilds, so a script need not keep
  network ids. A device's connected networks are not its own: ask for them by port.
- `reagents: true`: the total and each reagent, as the `reagents` tool.

`include: ["clock"]` adds `clock`, as `game_clock`.

```json
{"gateway_id": "world", "clock": {"game_time_s": 84211.5, "paused": false, "time_of_day_ratio": 0.31, "days_past": 12},
 "count": 3, "success_count": 3, "error_count": 0,
 "results": [
   {"index": 0, "ok": true, "reference_id": "811234",
    "logic": {"Temperature": 881.2, "Pressure": 2604.1, "RatioOxygen": 0.005, "280": 0},
    "slots": [{"index": 0, "logic": {"Occupied": 1, "OccupantHash": 1758427767, "Quantity": 50}}],
    "atmosphere": {"source": "internal", "atmosphere_id": "811235", "volume_l": 1000, "pressure_kpa": 2604.1,
                   "temperature_k": 881.2, "total_mol": 353.0, "liquid_volume_l": 0,
                   "contents": [{"gas": "Methane", "state": "gas", "amount_mol": 7.0}]},
    "reagents": {"total": 0, "reagents": []}},
   {"index": 1, "ok": true, "reference_id": "811410",
    "atmosphere": {"source": "pipe_network", "atmosphere_id": "900121", "network_id": "900120", "...": "..."}},
   {"index": 2, "ok": true, "reference_id": "811300", "slots": [{"index": 0, "logic": {"Occupied": 0, "...": 0}}]}]}
```

- **Keys are what you sent.** `"280"` comes back as `"280"`, `"pressure"` as `"pressure"`: the logic type enum has
  aliases that share a number, so the mod never renames. The same string twice in one list is refused.
- **Errors stay small.** An id that names nothing fails its item (`ok: false`, `error`, `thing_not_found`). A part that
  cannot be read at all goes in the item's `errors` by part name (`logic` and `slots`: `device_not_found` when the
  device is out of the gateway's scope or not a device; `atmosphere`: `no_atmosphere`, `port_not_joined`). A logic
  type that does not read goes in `logic_errors` under its name; a missing slot is that slot's `error`
  (`slot_not_found`). Codes and messages are the single tools'. Parts not asked for and empty error maps are left out.
- **Bounds.** At most 128 items, 64 logic types per item and per slot, 16 slots per item, 1,024 values in all (a slot
  without `logic` counts 32). Over a bound, an unknown key in an item, slot or atmosphere, or a slot index given twice
  is `invalid_argument` and nothing is read.
- Read only. Writes stay with `write_logic_many`.

## Rockets and umbilicals

`describe_device` on a device in a rocket adds `rocket`: its rocket network, name and state, and what the game counts
for it (`dry_mass_kg`, `hull_pieces`, `internals`). On a rocket umbilical (gas, power, chute or crew; the male one on
a Rocket Tower or the socket on the rocket) it adds `umbilical`, and `grid_survey` lists the same on each umbilical
among its `devices`:

- `role`: `umbilical` (the male one) or `socket`; `open`: extended and passing gas, power or items.
- `partner`, `partner_distance`: the partner the game holds now and its distance (small cells plus one; 0 unpaired).
- `search`: the game's own partner search replayed as things stand, without changing anything: `found`,
  `partner_distance`, `how` (from where, which way, how many columns) and, per column, why it stopped. The male one
  searches up to 14 small cells along its front, in its own column and two to its right; the socket one column. A
  partner must be the matching kind, face back (dot 0.9 or more), and have no other device between.

On a Logic Rocket Uplink `describe_device` adds `uplink`: the Logic Rocket Downlink it follows (`downlink`, with that
downlink's `rocket`; null when it follows none), `connected` (its data connection is live: on, powered, built, and
the downlink on a data network) and `choices`, every downlink it may follow: those a screwdriver press on the uplink
steps through, built and logic readable. `set_uplink` (`reference_id`, `downlink_id`) points it at one of them, as
those presses would; anything else is refused `invalid_downlink`. Its reply's `previous_downlink` sets it back.

## Reading back what you wrote

- `write_logic` returns `requested_value`, `previous_value` and `current_value`. `current_value` is read back at once
  and can differ from the request: the device clamps it (`On` 7 reads 1), ignores it (a paint-only colour index on the
  LED display), or moves toward it over time (a solar panel's angle). The write still succeeds; compare the two when
  it matters.
- In `read_logic_many`, `write_logic_many` and `sample_logic` every result has its `index`; a failed one also names
  the `reference_id` and `logic_type` it asked for (null where the entry's own could not be read) and its `error`.
- `read_memory` and `write_memory` check the whole range against the device's `stack_size` before touching it: an
  address past the end is refused (`invalid_argument`) and nothing is written. An IC Housing or suit with no chip has
  no memory to read (`no_programmable_chip`).
- `sample_logic`'s `interval_seconds` is the least time between samples. The game takes each sample in the first frame
  at or after it is due, on the real clock (a paused game is still sampled), so an interval shorter than a frame gives
  fewer samples than asked; `sample_count` says how many. The call answers when its last sample is taken.

## Where you look (1.4.3+)

`looking_at` also answers the words a player uses: "on this wall", "to my right", "a metre up".

- `view`: the camera's `eye`, its `forward`, `right` and `up`, `yaw_deg` (0 along `+z`, 90 along `+x`), `pitch_deg`,
  and `axes`: the world axes nearest your **level** forward, right and up (and back, left, down), so "forward" runs
  along the floor whether you look up or down; `look` is the axis of the look itself. `ambiguous` is true within 10
  degrees of a diagonal, where forward and right could be either axis. Third person and seats are handled as the
  game's own cursor handles them. `source` is `local` (this game's camera) or `remote` (1.12.0+: on a dedicated
  server, the view the player's own StationGod shares, read on their game the same way), with `age_s`, the seconds
  since it was taken. Without a view the call refuses `no_view`, saying why (see
  [install: multiplayer](install.md#multiplayer)).
- `hit`: the first surface on the look ray up to `max_distance_m` (default 10 m, beyond the game's 3 m reach): the
  point, its normal and `face` axis, the `face_plane` it lies on (`z=668`; within 0.3 m of the plane, so the top of a floor plate counts, 1.4.4+), the 2 m cell on your side, the small cell a
  mounted piece would stand in, that cell's `support` character (as `grid_survey`: `x` a door's keep-out, `g` a
  window), what was hit, and the point in the target's own frame (`local_on_target`).
- `target.body` for a structure: `render_box` (the box its meshes fill), `centre_offset` from its origin, and
  `grid_box`, the box of the small cells the game registers it in, its real footprint. `facing_me` says whether its
  front points at you.

## Examples

Examples show a tool's name and the arguments the agent passes.

Turn a device on and set it, `write_logic_many`:

```json
{ "writes": [
  { "reference_id": "132000", "logic_type": "On", "value": 1 },
  { "reference_id": "132000", "logic_type": "Setting", "value": 101.3 } ] }
```

Every Wall Heater's power draw at one instant, `network_snapshot`:

```json
{ "name_contains": "Wall Heater", "logic_types": ["On", "Power"] }
```

Watch a tank's pressure for 20 seconds, `sample_logic`:

```json
{ "targets": [{ "reference_id": "140211", "logic_type": "Pressure" }], "duration_seconds": 20, "interval_seconds": 1 }
```

## Connections and networks

`connections {reference_id}` lists each end of a pipe, cable, chute or device: its type (`Power`, `Data`,
`PowerAndData`, `Pipe`, `Chute`...), its role (`Input`, `Output`...), the network attached there and everything
connected at that end, by the game's own connection test.

`connections {network_id, kind}` lists a network's members, pieces first, then devices, paged. It adds a summary:

- **Cable:** the Cable Analyser's numbers from the last power tick: `required_w`, `potential_w`, `actual_w`,
  `shortfall_w`, the weakest cable's rating (`lowest_cable_max_w`), the weakest fuse, and `overloaded` when the network
  carries more than its weakest cable (the game then burns one such cable every power tick).
- **Pipe:** content (gas or liquid), volume, pressure, temperature and every gas.
- **Chute:** the member count.

Two filters narrow the members before paging, so `total` counts what they keep (`structure_count` and `device_count`
stay the whole network's): `prefab_contains` (part of the prefab name, any case) and `open_ends_only`, which keeps
members with an end of the network's kind that nothing is attached at (a run's loose ends, a device port left
unjoined) and lists those ends in each member's `open_ends`. A 32 KB chute network of plain straights answers with
the few pieces that matter.

`summarize: true` answers "what is on this network" in one reply instead of pages of near-identical pieces:
`by_prefab` counts the members by prefab, most first, with `colors` (how many of each show each paint colour), and
`devices` and `open_ends` list the devices and the members with a loose end (up to `limit` each, default 10), with
`device_count` and `open_end_count`. The filters apply first.

Every member, the one-thing form and the box form's pieces carry `color {index, name, is_default}` when the thing has
a colour, as `paint` reads it, so a new run can be painted like the lines beside it.

`min` and `max` without `reference_id` or `network_id` list every cable, pipe and chute piece in the box with an open
end, across all networks, each with its `network_id` and `open_ends`; `kind` keeps one kind, `prefab_contains` works
as above. Up to a 32 m cube, 25 a page.

Network ids change after almost every edit: when networks merge or split, the game gives them new ids. Wherever a
tool takes a network id it also takes the reference id of any piece or device on the network, or
`{reference_id, port}` of a device port, and resolves it to the current id when the call runs (`resolved_networks` in
the reply). Name networks that way rather than by a stored id.

## Batteries

`set_battery_charge` sets the charge of the batteries you name, where the console's `setbatteries` sets every battery
in the world at once. It is a cheat: the power comes from nowhere, so ask before each use. Like the building tools it is
a dry run by default (`dry_run: false` and `confirm: true` to set), and it runs on the host only; other players see
the new charge through the game's own sync.

- **Which batteries:** `reference_ids` (up to 256, each checked on its own), `rocket_id` (every battery of that
  rocket: its id, its rocket network's or any part's), or `in_id` (every battery cell in a thing's slots and in the
  slots of what they hold, six levels down: a suit, a tool, a locker, a rover; `"player"` for you).
- **What counts:** placed Station Batteries and Large Station Batteries, the batteries built into rockets, battery cells
  of every size (the wireless one too) whether loose, in a device, a suit or a tool, and a power pylon end's buffer.
  Anything else is refused `not_a_battery`, naming what the thing is; when its slots hold cells (a suit, a drill), the
  message gives the `in_id` call that sets them. A Disposable Battery Charger is a consumable, not a battery.
- **How full:** full by default; `ratio` (0 to 1) sets a share of each battery's capacity, `joules` an amount, filling
  a battery smaller than that (`clamped`). A ratio below the present charge drains it.
- **The reply:** per battery `kind` (`station_battery`, `rocket_battery`, `battery_cell`, `pylon_buffer`), `held_by`
  (the suit, tool or device a cell sits in), `capacity_j`, `before_j`/`after_j` and the same as ratios; `added_j`
  for the whole call. Keep the before values: a call with that battery and its `before_j` as `joules` puts it back.

A cell's charge display and its percentage catch up on the next power tick (about half a second; not while paused).

## Console

`run_console_command` runs any console command on the game's main thread and returns what it printed. It is
unrestricted: the console can spawn and delete things, teleport players, change world settings and quit the game, and
none of it can be undone. Refusals such as "requires creative mode" come back as red output lines, not as errors.

Some commands (`save`, `load`, `new`, `difficulty` and others) finish on a background task and print nothing before the
call returns: the reply then says `completes_asynchronously`. Read the result a moment later with `read_console`.

## The StationGod Gateway

Optional. The mod adds one structure, the StationGod Gateway: a passive, always-on copy of the game's Logic Memory.
Print *Kit (StationGod Gateway)* at the Electronics Printer (1 g copper, 1 g gold, 10 seconds), build it and connect a
data port. Passing its id as `gateway_id` limits a device call to the devices on its data networks; attached to two
data networks it covers both, each device once. Without a gateway every device tool reaches the whole world.

`list_gateways` gives each gateway's `status`: `ready` (`available` true, its id scopes a call), `incomplete` (not
fully built) or `no_data_network` (no data cable on either port); the `world` entry says `bypass`. A device tool given
an id no gateway has answers `gateway_not_found`; given a gateway that is not `ready`, it answers `gateway_unavailable`
and the message names the status.

Its model and kit picture are copied from the Logic Memory at run time; no game assets are distributed.

## Health and game updates

`mod_info` counts every call per method (`calls`, `errors`, `mean_ms`, `max_ms`) and lists `reflection`: every game
member the mod reaches by name, and whether it was found. After a game update, a missing member turns off only the
tools that need it; they answer `game_changed`. The mod's pipe reply envelope, which pipe clients such as the script
dashboard read, also carries `elapsed_ms`, the time a request took on the game's main thread; MCP tool results do not
carry it, so read the per-method `mean_ms` and `max_ms` instead.

Pipe clients can shape a reply with an optional `shape` object beside `params` in the request line:
`{"id": "1", "method": "thing_health", "params": {...}, "shape": {"fields": ["reference_id", "damage_ratio"],
"limit": {"things": 20}, "max_bytes": 65536}}`. `fields` works as the tools' `fields` argument (single names and
dotted paths, read from each list entry and top-level object at any depth or from the top-level key a path starts
with; a name that matches nothing, or is not a name, comes back in `fields_unmatched`, with `fields_valid`); `omit` as the tools' `omit` argument (paths from the reply's top,
left out wherever they reach, top-level keys included; unused ones come back in `omit_unmatched`); `limit` keeps the
first entries of a top-level list and adds `truncated` (each cut list's length before the cut); `max_bytes`
(1,024 to 16,777,216) answers `reply_too_large`, with `data` holding the reply's `bytes`, the `limit` and the length
of every top-level list (`counts`), instead of a larger reply. A key the mod cannot use is ignored. A shaped reply's
envelope carries `"shaped": true`; errors are never shaped. The method's own paging arguments save the game's work as
well and are better than `limit` where a method has them.

`mod_info` also has a `runtime` section (1.9.1+) that shows what the mod costs the game since it loaded. Per method
called: the main-thread time of the tool (`handler_ms`) and of turning its reply into text (`serialize_ms`), the wait
for the next frame (`queue_wait_ms`) and the reply's size (`reply_bytes`), each as total, mean and max. Per frame: how
many requests were answered and how long they took (`frames`), and how often the request budget left some for the
next frame (`budget_stops`). And the game's garbage collector and Mono heap (`memory`). `catalogue_drift` lists any
argument name a method's code read that its catalogue entry does not declare (`method`, `argument`, `reads`); it should
stay empty, and the mod's log names the first read of each. A frame spends at most
`[Performance] RequestBudgetMs` (4 ms by default, at most 2 ms while a job holds the game tick) on requests; the rest
wait for the next frame, in order (see [configuration](configuration.md)).
