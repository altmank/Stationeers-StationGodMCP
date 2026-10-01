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
| `describe_device` | One device and every logic type it can read or write; for a rocket's device its rocket, for an umbilical its pairing (see *Rockets and umbilicals*). | `reference_id` |
| `read_logic` | Read one logic value. | `reference_id`, `logic_type` |
| `write_logic` | Write one logic value; the reply reads it back at once (`current_value`). | `reference_id`, `logic_type`, `value` |
| `read_logic_many` | Up to 256 reads in one call; each gets its own result or error. | `reads: [{reference_id, logic_type}]` |
| `write_logic_many` | Up to 256 writes in order, in one call. | `writes: [{reference_id, logic_type, value}]` |
| `read_memory` | Up to 512 consecutive values from a device with memory (an IC Housing's or suit's chip stack, a Logic Sorter, a satellite dish, a fabricator; a Logic Memory has only `Setting`). | `reference_id`, `start_address`, `count` |
| `write_memory` | Up to 512 consecutive values into such a device. | `reference_id`, `start_address`, `values` |
| `inspect_slots` | A device's slots, what is in them, what each slot takes, and every slot logic value. Changes nothing. Devices only: for a crate, the lander or a tool use `container_contents`. | `reference_id`, `slot_index` |
| `network_snapshot` | Many devices and their logic values at one instant, in one game frame. | `reference_ids`, `prefab_hash`, `name_contains`, `logic_types`, `max_devices` |
| `sample_logic` | Record up to 32 values for up to 30 seconds; the first readings plus every change, timestamped. | `targets: [{reference_id, logic_type}]`, `duration_seconds` (default 5), `interval_seconds` (default 0.5) |
| `connections` | A pipe, cable, chute or device's ends and what each joins; or every member of a network with its load or contents. | `reference_id`, or `network_id` with `kind` (filters `prefab_contains`, `open_ends_only`) |
| `list_gateways` | The scopes device tools accept: `world` and every StationGod Gateway. | none |
| `looking_at` | What your crosshair is on, and the button, switch, port or slot under it; where you look from and which way (1.4.3+), the surface the look ray hits and the grid there, and the target's body. | `max_distance_m` |
| `game_clock` | Game time, paused or not, time of day, days past. | none |
| `run_console_command` | Any console command, with the lines it printed. | `command`, `max_output_lines` |
| `read_console` | The latest console lines, including Unity errors and stack traces. | `lines` |
| `mod_info` | Mod version, pipe name, call statistics per method, and every game member the mod relies on. | none |

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
- `sample_logic`'s `interval_seconds` is the least time between samples. Each sample is a round trip to the game's main
  thread, so a short interval on a busy or slow game gives fewer samples than asked; `sample_count` says how many.

## Where you look (1.4.3+)

`looking_at` also answers the words a player uses: "on this wall", "to my right", "a metre up".

- `view`: the camera's `eye`, its `forward`, `right` and `up`, `yaw_deg` (0 along `+z`, 90 along `+x`), `pitch_deg`,
  and `axes`: the world axes nearest your **level** forward, right and up (and back, left, down), so "forward" runs
  along the floor whether you look up or down; `look` is the axis of the look itself. `ambiguous` is true within 10
  degrees of a diagonal, where forward and right could be either axis. Third person and seats are handled as the
  game's own cursor handles them.
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

Network ids change after almost every edit: when networks merge or split, the game gives them new ids. Wherever a
tool takes a network id it also takes the reference id of any piece or device on the network, or
`{reference_id, port}` of a device port, and resolves it to the current id when the call runs (`resolved_networks` in
the reply). Name networks that way rather than by a stored id.

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
