# Building cables, pipes and chutes

[Back to the README](../README.md)

Survey the grid, plan a route under layout rules, lay it, remove or reroute runs, and upgrade whole networks in place.
Tidying and refactoring whole networks is on its own page: [cleanup-and-refactor.md](cleanup-and-refactor.md).

## How every building tool works

This applies to every tool that changes the world: the run, upgrade and clean tools here, and
`replace_walls`, `replace_frames`, `place_structure` and `remove_structure`.

1. **Dry run by default.** A call without `dry_run: false` changes nothing. It reports every piece, what it costs and
   gives back, the networks before and after, and every problem at once. Read `ready`, `problems` and `warnings`.
2. **A real run** needs `dry_run: false` and `confirm: true`, and starts only when the checks find no problem. It
   returns a `job_id`.
3. **One held tick.** The mod holds the game tick as a save does, runs every check again on the world as it is now,
   makes every change in one frame, and checks the result the next frame before letting the tick go. No power,
   atmospherics or logic tick ever sees a half-built network. Players see a brief pause.
4. **Poll** with `{job_id}` alone until the status is final:

   | Status | Meaning |
   | --- | --- |
   | `waiting`, `verifying` | Still running. |
   | `applied` | Done, and every check afterwards passed. |
   | `applied_with_differences` | Done, but the check found something other than planned; the job lists it. |
   | `applied_unchecked` | Done, but the check afterwards could not run; the job says why. Look before relying on it. |
   | `stopped` | A piece failed part way. The job lists what was done and where it stopped; nothing after that was done. For upgrades, running the same call again resumes. |
   | `gas_lost` | Pipe network contents went missing and could not be put back; `gas_check` says how much and where. Every later pipe job is refused (`gas_check_failed`) until the world is loaded again. |
   | `refused` | The checks in the held tick failed; nothing changed. |

5. **One job at a time.** A real run that finds another job running answers `busy` with `running_job_id` and changes
   nothing. With `wait: true` it is queued instead (status `queued`, its own `job_id` and `position`; up to 8 wait) and
   starts when the slot is free, with every check run again on the world the earlier jobs left.
6. **Materials** come from your inventory at any depth, or from `from_id` (a belt, a locker, any container), and cost
   what the game's own placement charges. Nothing is made for free (`not_enough_coils`, `not_enough_materials`).
7. **Refunds** (`refund`, default true) are what deconstruction would give back. They go into the source's inventory:
   first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand),
   then as new stacks into empty slots that take the item, and only what nothing takes onto the ground a metre in
   front of the holder, at rest. `refunded` lists where each part went: `merged`, `slot` or `ground`.
8. **Host only** (`not_host` on a client). Changes use the same calls as a player's own building, so other players,
   saves and ownership follow as for normal building. Players without the mod see ordinary cables and pipes.

A finished run job's log lists `created_ids`, every piece it built, and `created_by_part`, the same ids grouped by
`run`, `branch N`, `joined` (an existing piece the run changed: a neighbour or a tap's trunk piece turned into a
junction) and `fill`.

## Tools

| Tool | What it does |
| --- | --- |
| `grid_survey` | What stands in each 2 m cell of a box or room, down to 0.5 m: frames, walls, pieces, devices and their ports, networks, and how visible a piece would be in each small cell. Read only. |
| `plan_cable_route`, `plan_pipe_route`, `plan_chute_route` | Find a route under rules and return it with the place tool's own dry run. Read only. |
| `place_cables`, `place_pipes`, `place_chutes` | Lay a run, with branches, or one piece. |
| `remove_cables`, `remove_pipes`, `remove_chutes` | Remove pieces as wire cutters, a wrench or deconstruction would. |
| `undo_job` | Undo a finished place or remove job: remove what it built, build again what it removed (1.4.3+). |
| `upgrade_cables` | Normal cable to heavy (default) or super heavy, piece for piece, in place. |
| `upgrade_pipes` | Normal gas and liquid pipe to insulated pipe of the same content, in place. |
| `connections` | A piece's or device's ends, or a network's members and load; see [devices-and-logic.md](devices-and-logic.md#connections-and-networks). |

Grades: cables `normal`, `heavy` (default) or `super_heavy`; pipes `gas`, `liquid`, `insulated_gas` or
`insulated_liquid` (required); chutes `chute`.

## The grid

Structures sit on 2 m cells, whose centres are at odd whole metres. Cables, pipes and chutes sit on the 0.5 m small
grid, centres on multiples of 0.5 m: four small cells per axis in each 2 m cell. Directions are world axes: `+x`, `-x`,
`+y` (up), `-y`, `+z` (north), `-z`.

`grid_survey {room_id}` or `{min: [x, y, z], max: [x, y, z]}` gives, per 2 m cell, its frame, walls, room and two
64-character strings over its small cells:

- `small`: what fills each small cell: `.` empty, `c` cable, `p` pipe, `b` both, `h` chute, `d` device, `o` another
  small thing, `r` a rocket's cell.
- `support`: what would hold a piece there: `i` inside a frame (hidden in the frame's body), `e` a frame edge or
  corner, `f` a frame's face, `w` a wall's plane, `a` air.
  Over those, `x` marks a door's keep-out and `g` a window's face (see *Doors and windows*). A door's face is no
  wall: it holds nothing up.

Then every piece with its ends and network, every device with its `rotation` and each port's cell, direction, type
and role, and the networks with their loads or contents. `rotation` is `{facing, up, euler}` in the forms
`place_structure` takes, so a device can be placed again as it stands, or turned: `facing` reversed is a half turn.
`find_things`, `looking_at` and `connections` report the same `rotation` for structures. `network_visibility` counts each network's cells by class and lists the floating
ones; `include_refund: true` adds what removing each piece would give back. Pages of 27 cells (`limit` up to 125,
`offset`).

## Doors and windows

Every door keeps its doorway (1.4.3+): its face, with the jambs, the top edge and the threshold, and a band either side
of it inside the door's rectangle (mod setting `[Layout] DoorKeepOutBand`, default 0.5 m) is its **keep-out**. Doors,
airlocks, blast and hangar doors, hatches, roll covers, robot arm doors and the Force-Field Door mod's doors of every
size count. A piece hidden inside the floor slab under a threshold is not in it, and the cells joining the door's
own ports are released so the door can still be wired.

- The route planners never route through a keep-out; `allow_door_keepout: true` lets them. A route's own end cells
  are released, with a note.
- `place_cables`, `place_pipes`, `place_chutes` and `place_structure` refuse a new piece there with `in_door_keepout`;
  `allow_door_keepout: true` makes it a warning.
- A window's face (inside its square: glass, composite, padded and shuttered windows and window shutters; floor
  gratings are floors, not windows) is allowed but costs a route extra, and a run or device on one warns
  `crosses_window`.
- `grid_survey` lists `doors` (faces, plane, band, port cells) and each wall's `kind` (wall, window, door).

## Planning a route

`plan_cable_route` (and its pipe and chute twins) searches the small grid and dry-runs the result.

**Ends.** `from` and `to` are each `{at: [x, y, z]}` (a cell), `{reference_id}` of a piece (joined; leaving through an
open end is free, any other direction makes it a junction) or `{reference_id, port}` of a device port (`port` may be
left out when the device has one port of the kind). `to` may also be `{network_id}`, the nearest piece of that network,
or a long straight, any of its cells. For pipes, `{reference_id}` of an in-line tank or a passive vent (or another
pipe thing with its own ends that is neither a pipe piece nor a device) is the cell beyond its free end, and the pipe
there gets an end toward it, as at a device port; name the end with `port` when more than one is free.

**Keeping cells free.** `reserve_cells: [[x, y, z], ...]` and `reserve_ports: [{reference_id, port}]` are cells the
search treats as blocked, a port's being the cell a piece joining it stands in. Reserve a device's other ports while
routing to one of them, so this run cannot take the cell the next run needs. A reserved cell that is one of the
route's own ends is released, and `notes` say how many cells were kept free.

**Rules.**

| Argument | Effect |
| --- | --- |
| `frames_first` (default true) | A cell in air, on no frame and no wall plane, costs as much as 50 more cells over frames, so a route over frames or along walls wins whenever the search box holds one. Only if none exists does the route cross air, with as few air cells as possible and a `through_air` note. `route.air_cells` counts them: 0 is a clean route. |
| `prefer: "frame_edges"` | Along frame edges and corners, beam tops included. |
| `prefer: "walls"` | On or beside wall planes. |
| `prefer: "hidden"` | The least visible route: per cell inside a frame 1, on a frame's surface 3, on a wall's plane 5, in air 9. A hidden route up to three times as long beats one along a surface. |
| `inside_frames: true` | Strict: only cells inside a frame or on its surface (beam tops and outer faces included). May give `no_route` where `prefer: hidden` would still find one. |
| `avoid_walkways`, `avoid_room_interior` | Extra cost for room cells above the floor, or away from every face plane. |
| `avoid_networks` | `true`: never beside another network of the kind; or a list of network ids. |
| `min_bends`, `axis_order` | Fewer turns; `vertical_first` or `horizontal_first`. |
| `margin_m` (default 6, max 32), `max_length` (default 400) | Search box around the ends; longest route. |

Every route reports `visibility {inside, frame_surface, wall, air}` for its new cells.

**Several starts.** `from: {reference_id, ports: [2, 3]}`, or an array of starts, up to 16, grows one tree: the first
start routes to `to`, every other start to the nearest cell of the tree so far, joined with a junction. A device with
separate power and data ports gets one run, never two parallel runs that close a loop. Plan such a device in one call.

**Bus mode.** `trunk: {waypoints}` (or `cells`) instead of `to` lays that trunk as given, for example one planned with
this tool and not built yet, and branches every start from it. The trunk and its drops are one job and one check.

**Rerouting.** `reroute: {between: [end, end]}` replaces the shortest run between two devices or pieces on one
network; `reroute: {reference_ids: [...]}` replaces those pieces. The old run must meet the rest at exactly two cells.
The new run is built and the old one removed in one job, so no device is ever unpowered. An APC, transformer or pump
sits on two networks: `between` uses the one both ends share, or name the port with `{reference_id, port}`.

**Planning around old pieces.** `assume_removed: [ids]` plans as if those things were gone: their cells free, their
links absent. The cable pieces among them go into `place_arguments.remove_ids`, so one job builds the new run and
removes the old pieces, and the dry run's checks see the finished network. Other things (a pipe in a cable's way) go
into `place_arguments.assume_removed`: remove them first with their own tool. `route.assumed_removed.in_the_way` lists
the pieces the route needs gone; `route.removal_refund` prices them.

**The result** has `found`, `route` (waypoints, length, bends, air cells, branches, visibility), `place_arguments`
(ready for the place tool: add `dry_run: false` and `confirm: true` to build) and `dry_run`, the place tool's own report.

Example, a drop from a device's power port to the nearest piece of the network that trunk piece `140977` is on, kept
inside frames where possible, `plan_cable_route`:

```json
{ "from": { "reference_id": "151020", "port": 0 }, "to": { "network_id": "140977" }, "prefer": "hidden" }
```

## Laying a run

`place_cables` takes a run as `waypoints` (points joined by straight lines), `cells` (every cell), or one piece as
`piece: {at, ends: ["+x", "-y"]}`.

- **Pieces fit their connections.** Each small cell gets the one-cell piece whose ends match exactly, read from the
  loaded coil or kit and turned to fit. Long straights are never placed.
- **Meeting existing pieces.** A piece the run crosses or joins becomes the junction with the extra end, keeping its
  own grade. A long straight the run must join in its middle is split into single pieces in the same job (warning
  `long_split`; `allow_split_long: false` refuses with `long_piece`).
- **Joining.** `join: "ends"` (default) joins the run's first and last cells to open ends and device ports pointing at
  them; `none` joins nothing extra; `all` joins at every run cell. `extra_ends` adds ends to run cells.
- **Branches.** `branches: [{waypoints, attach}]` adds side runs joined to the run with a junction.
- **Placement check.** The game's server checks nothing when a structure is built, so the tool checks as the
  placement cursor would: devices, chutes and other small things block (`cell_blocked`); a pipe blocks a cable only
  along its own axis; frames and walls never block cables or pipes.
- **Removal in the same job.** `remove_ids` removes pieces before building, so a device is never unpowered between its
  old cable and its new one.
- **Tap check.** A run end left open next to, or one free cell short of, another network's piece warns `not_joined`.
  `join_to` names the network the run must end up on; `join_trunk: true` adds the missing tap (warning `tap_added`).
  The planners set `join_to` themselves when `to` names a network, piece or port.

## Undoing a job (1.4.3+)

`undo_job {job_id}` undoes a finished `place_*`, `remove_*`, `place_structure` or `remove_structure` job among the
last 16: it removes (with `remove_structure`) everything the job built and builds again (with `place_structure`)
everything it removed, as it stood when the job started (the mod takes a snapshot of each removed thing then). It is
refused, with `plan.diverged` saying why, when the world is no longer as the job left it: something it built is gone
or another prefab now, or something it removed cannot be placed again exactly. The dry run shows the plan, both
tools' arguments and the removal's own dry run; `dry_run: false, confirm: true` starts the removal and queues the
placements behind it. Every guard of both tools applies, and materials are paid and refunded as by hand.

## Removing pieces

`remove_cables`, `remove_pipes` and `remove_chutes` take `reference_ids`, or the pieces in the cells of `waypoints` or
`cells`. A network that only loses pieces keeps its id. `plan_removal` prices a removal without doing it; see
[cleanup-and-refactor.md](cleanup-and-refactor.md#pricing-a-removal).

## Guards

Each refusal names what it found. An `allow_*` argument accepts that one case after you have read it.

| Code | Meaning | Override |
| --- | --- | --- |
| `would_bridge` | The run would join two or more networks, or two ports of one device (both sides of an APC or transformer, a battery's input and output, a pump's two sides, a device's chute output into its own input). Names the networks and the devices on each. | `allow_bridge`, naming every network of the merge, or the device |
| `would_split` | A removal would split a network or leave a device port joined to nothing. Lists each resulting network with its devices (`components`), the devices that feed it (`root`: your `root`, else every supplier: an APC's, transformer's or battery's output, a generator, a solar panel) and `cut_off`, the devices no root reaches afterwards. | `allow_split` |
| `would_overload` | A cable network after the edit would carry more than its weakest cable, so the game would burn a cable every power tick. | none: upgrade the cable or keep the networks apart |
| `would_burst` | A pipe network's pressure after the edit would exceed its weakest pipe. | none |
| `holds_contents`, `contents_would_move` | A pipe removal would delete a network's gas or liquid, or divide it. | none: empty it first with `move_gas` |
| `content_mismatch` | A pipe of the other content (gas and liquid never join). | none |
| `cell_blocked`, `cannot_change`, `no_piece_for_ends`, `link_lost` | A cell is taken; a piece cannot be changed (a fuse or meter mounted, indestructible, rocket); no piece has those ends; a piece would lose a link. | none |
| `assumed_present` | A real run while something in `assume_removed` still stands. | remove it first |
| `would_loop` (warning) | The run joins something already joined another way: a second path. Keep it only if the redundancy is meant. | not needed |
| `in_door_keepout` | A new piece in a door's keep-out (1.4.3+). | `allow_door_keepout` |
| `crosses_window` (warning) | A new piece on a window's face (1.4.3+). | not needed |
| `not_joined`, `through_air`, `open_end`, `long_split` (warnings) | A run end stops short of a network; pieces in air; an end left open; a long straight split. | not needed |

## Pipes

The same tools and arguments with `grade` required. The contents always stay in the network's own atmosphere: placing
pipe adds volume (pressure falls); a removed pipe leaves the network before it goes, so its volume leaves and its gas
stays (pressure rises, refused above the weakest pipe). The last pipes of a network that still holds gas or liquid are
never removed. `would_bridge` lists each network's gases, pressure and temperature, so you can see whether they may mix.

**Contents are checked.** The game moves gas between merged or split pipe networks at the next game tick, not at
once; a job applies those changes after every piece it builds, so a run that joins several networks keeps every
mole. Every job that can change pipe networks (`place_pipes`, `remove_pipes`, `upgrade_pipes`, `clean_pipes`,
`place_structure`, `remove_structure`) then compares each family of networks it changed, before and after, in
`gas_check`:

| Field | Meaning |
| --- | --- |
| `ok`, `summary` | Every family holds what it held, and no network without pipes holds gas. |
| `families` | `networks_before`, `networks_after`, `mol_before`, `mol_after`, `energy_before_j`, `energy_after_j`, `missing_mol`; `emptied` when all its pipes were removed (the contents go with the last pipe, as in the game). |
| `ghosts` | Networks without pipes left holding gas, with the devices still registered on them: where missing gas sits. |
| `recovered`, `ghosts_cleared` | Gas the game's merge lost, put back into the family's networks by volume, and the pipeless networks emptied and dropped after. The job then ends `applied_with_differences`. |
| `old_ghosts` | Pipeless networks that already held the same gas before the job: not its doing, left as they are. |
| `checked` | False only when a save took the game tick before the check. |

## Chutes

The same tools with Kit (Chute), and the item flow checked.

- **Pieces:** straights and corners (1 kit each) and, where three ends meet, a junction (2 kits). Chutes have no other
  shape, so a cross is refused (`no_piece_for_ends`). Valves, splitters, overflows, bins, windows, inlets and outlets
  are never placed.
- **Direction matters.** Items travel from a run's first cell to its last. Start at the source (a device's chute Output
  port, a chute bin, a line carrying items towards you) and end at the sink (a device's chute Input port).
  `connections` and `grid_survey` list each port's role.
- A junction takes items in through two inputs and lets them out through its output, so it merges flows; it cannot
  split one. The tool turns it to face downstream.
- Refused: `flow_reversed` (reverse the waypoints), `flow_conflict` (two flows meeting head-on), `flow_ambiguous`
  (nothing says which way a junction must face). Warned: `drops_items` (items would fall out of an open end),
  `flow_unknown`.
- A chute with an item riding in it is never removed or replaced: let it pass, or take it out with `move_item`.
- Chutes block cables, pipes, devices and other chutes in the same small cell. Chute networks take new ids after any
  change.

## Upgrading networks in place

`upgrade_cables` turns normal cable into heavy (default) or super heavy cable; `upgrade_pipes` turns normal gas and
liquid pipe into insulated pipe of the same content. The game itself will not place heavy cable over normal cable or
insulated pipe over normal pipe, so the tools rebuild each piece the way the coil's or kit's own merge placement does.

- Name a network with `network_id` or pieces with `reference_ids` (up to 4096). Pieces already at the target stay, and
  so do pipe members that are not pipes (vents, radiators).
- Each piece becomes the target piece with exactly its cells and ends, long straights included. A piece without one is
  refused, or left as it is with `skip_unmatched: true`.
- Before anything changes, the tool records the game's connections around every piece, predicts them with every
  replacement in place, and refuses if any would appear or disappear. So no networks merge and no device is cut off.
  Mounted fuses, analysers and pipe meters stay attached.
- Cable networks keep their ids. A pipe network keeps its contents; only its volume changes by the difference between
  old and new pieces, and the run is refused if the pressure would then exceed the weakest pipe.

Make a whole network heavy cable, `upgrade_cables` (dry run, then again with `dry_run: false, confirm: true`):

```json
{ "network_id": { "reference_id": "133871", "port": 1 }, "to": "heavy" }
```
