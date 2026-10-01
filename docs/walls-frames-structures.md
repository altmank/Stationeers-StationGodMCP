# Walls, frames and structures

[Back to the README](../README.md)

Swap walls, windows and frames in place without ever opening a room, and place or remove any structure a kit builds.
Every tool here works as described in [building.md](building.md#how-every-building-tool-works): dry run first, a
held-tick job, materials and refunds from your inventory, host only.

For cable, pipe and chute runs use the run tools in [building.md](building.md): they choose pieces by their
connections and guard against merging networks, which `place_structure` does not.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `replace_walls` | Replace walls and windows with another wall or window prefab, in place. | `room_id` or `reference_ids`, `to` (required), `from_prefabs`, `skip_unmatched` |
| `replace_frames` | Replace frames with another frame prefab, or finish unfinished frames, in place. | `room_id` or `reference_ids`, `to` (optional), `from_prefabs`, `skip_unmatched` |
| `wall_map` | A text elevation of a wall (or floor) as seen from one side: seams, walls, windows, doors, devices, runs, free rectangles (1.4.3+). | `plane` + `around` + `side`, or `looking`; `radius_m`, `free_rects` |
| `find_spot` | Ranked places for a prefab near a point on a wall or a room's walls, checked as the cursor checks them (1.4.3+). | `prefab`, `near`, `plane`/`looking`/`room_id`, `require` |
| `lint_layout` | Check a room or box against the layout rules: runs in doorways, floating or across windows, blocked ports, overlapping or out-facing devices, seams (1.4.3+); things a player could not place again where they stand (1.5.0+). | `room_id` or `min`/`max`, `limit` |
| `check_replaceable` | For each thing, could a player place it again exactly where it stands, with its neighbours present (1.5.0+). | `reference_ids` |
| `show_preview` | Draw wire boxes in your game for a planned placement's footprint, body and ports, or any cells and boxes; timed, nothing built (1.4.3+). | as `place_structure`, or `cells`, `boxes`; `seconds`, `clear` |
| `describe_prefab` | A prefab in its own frame: placement, allowed turns, footprint, ports, visual up (1.4.3+). | `prefab` |
| `place_structure` | Place any kit-built structure at a position and turn, at a build state, with a label and colour. Up to 64 in one job. | `prefab`, `at`, `facing` / `rotation` / `face` / `orient`, `build_state`, `label`, `color`; or `placements: [...]` |
| `remove_structure` | Remove structures as deconstructing them by hand would. Up to 256 in one job. | `reference_ids`, `allow_contents`, `allow_breach`, `allow_broken`, `allow_burst`, `refund_to` |

## Replacing walls and frames

1. `rooms` gives the `room_id`; or collect the pieces' reference ids with `looking_at` or `find_things`.
2. Dry run, `replace_walls`:

   ```json
   { "room_id": "12", "to": "StructureCompositeWall", "from_prefabs": ["StructureWallIron"] }
   ```

   or `replace_frames {room_id}` to finish that room's unfinished frames. Read `ready`, `problems`, `pieces` (each
   piece's target, what it blocks before and after, the pressure on its faces, its materials), `materials` and `rooms`.
3. The same call with `dry_run: false, confirm: true`, then poll `{job_id}` until the status is `applied`.
4. `rooms` again: the room keeps its id, its cells and its air.

**What is supported.**

- Walls: any wall or window prefab to any other with the same footprint: iron to composite or reinforced wall, wall to
  window, window to wall. Only plain walls and windows are touched; shuttered windows, floors of their own classes,
  ladder platforms and crew umbilical doors are kept as `special_piece`. A floor grating is a plain window to the
  game, so it swaps like one (a grating to a plate seals the face).
- Frames: iron to steel and back, and any frame to its own prefab, which finishes it. Rocket towers are never touched.
- Scope: `reference_ids` (up to 4096) or `room_id`: for walls every wall on a face of the room's cells, for frames every
  frame next to the room. `from_prefabs` narrows either.

**How a swap never opens a room.** With the game tick held, each old piece is marked as being destroyed, which lets
the game hand its place to the new piece. The new piece is built in the same place with the old one's owner and colour
and raised at once to its final build state. Only when it holds every place the old one held, and blocks what the old
one blocked, is the old piece removed. If not, the new piece is taken away, the old one gets its place back, and the run
stops there. The next frame the mod checks every new piece and that the air around them is as it was (to within
0.01 mol and 10 J, plus 0.001 % of the total, the rounding of the game's sums). After
the game has run and re-evaluated its rooms, it checks that every room beside a swapped piece still exists with the same
id, cells and air (within 1 %). A room that appears because a finished frame closed a space is listed in `new_rooms`.

**What is refused.**

| Code | Why |
| --- | --- |
| `footprint_mismatch` | The new piece would not take exactly the old one's place. `skip_unmatched: true` leaves such pieces. |
| `would_open` | The old piece blocks air or gravity and the target does not. Making a leaky piece airtight is allowed (`seals`). |
| `would_overstress` (walls) | The face's pressure difference is at or above what the new wall bears; the game would damage it until it breaks. Above its stress mark the piece is only flagged: `stressed` in its entry and the face's verdict. |
| `cell_occupied` (frames) | Finishing a frame closes its cell, which would seal in whatever is there: a pipe, cable, device, player, creature or loose item. |
| `cannot_place` (1.5.0+) | A player's placement cursor would refuse the new piece where the old one stands, once the old one is gone (the rule `check_replaceable` asks; the game's reason is in the message); the old piece must also stand where the cursor snaps it, at a quarter turn the cursor gives the new prefab. Not checked for a prefab the game has no placement cursor for (it makes one for every structure prefab at start, a dedicated server too). |
| `not_enough_materials` | One per missing item. |

Kept with a reason: `already_at_target`, `special_piece`, `not_selected`, `indestructible`, `broken`,
`being_destroyed`.

Finishing a frame does what welding its last sheet does: its cell leaves its room, and its gas is shared among its open
neighbours. The room check expects exactly that.

**Materials** follow the game's deconstruction rule: each build state's items. The new piece costs its states up to
its final one; the old piece gives back its states up to the one it is at. Only the difference is taken or given back.
Tool wear, welder fuel and battery charge are not charged.

## Placing structures

1. Find the prefab name (`looking_at`, `find_things`) and a point in the target cell (`grid_survey`, `looking_at`).
2. Dry run, `place_structure`:

   ```json
   { "prefab": "StructureWallLight", "at": [701, 203, 655], "facing": "-x" }
   ```

   Read `ready`, `problems`, `warnings`, and each placement's snapped `position`, `orientation`, `face`,
   `build_state` and `cost`. A device (or an in-line tank, a passive vent) also lists `ports`: each port's `index`,
   `at` (the cell a pipe or cable joining it would stand in), `toward`, `type` and `role`, where they would land at
   that position and turn, in `grid_survey`'s shape. Check them before building, and turn the device if a port lands
   in the wrong cell.
3. The same call with `dry_run: false, confirm: true`; poll `{job_id}`. `result.verification` confirms each piece
   stands as planned: prefab, position, turn, build state, label and colour.

- **Position:** `at` is any point in the cell, snapped as the placement cursor snaps it. A 2 m device snaps to its
  cell's centre. A 0.5 m-grid device (a battery, a valve, a transformer) that cannot be built at the point as given is
  set down on the surface behind it, as the cursor's ray lands on a surface: the floor plane below a standing device,
  the face at the back of a mounted one. So a cell's centre works; a point as given that can be built is kept. Only a
  surface that is there counts (a plate on that face, or a frame behind it): a spot that is taken is not moved into
  the air below it, but refused. `resolved.at_how` says when a piece was set down, and why the point as given was not
  buildable.
- **Turn:** at most one of `rotation` (`[x, y, z]` degrees, multiples of 90), `facing` (`+x`, `-x`, `+y`, `-y`, `+z`,
  `-z`) with an optional `up`, or `face` for pieces placed on a cell face such as walls: `face: "+x"` puts the piece on
  the cell's +x face, looking into the cell. A grid piece may only turn about the axes its cursor turns it
  (`invalid_rotation`); a piece the cursor turns itself as it autoplaces (cables, pipes and some devices, such as
  lockers) is kept at any turn with an `unusual_rotation` warning. To re-place a device as it stands, copy the `rotation` that `grid_survey`, `find_things`,
  `looking_at` or `connections` report (`facing` and `up`, or `euler` as `rotation`); reverse `facing` to turn it round.
- **Build state:** `finished` (default), `first` (as a kit leaves it, costing only the kit), or an index.
- **Label and colour:** `label` as the Labeller writes it (not on the pipe-size in-line tanks, which the game
  cannot rename); `color` a name or index (`paint` lists them).
- **Checks:** the game's own placement cursor for that prefab (blocked cells and faces, collisions, each class's own
  rules, support for face-mounted pieces), nothing loose and nobody inside a piece that fills its cell. The check runs
  again just before each piece is built, so later placements see earlier ones. The dry run sees only what stands now,
  so it also checks the placements of one request against each other (`overlaps_placement`).
  A piece that needs a frame below it (a station battery, a dish, landing pad parts) may stand on a frame an
  earlier placement of the same request puts there (in the cell the game looks in for that piece: half its size
  below a battery, its whole size below a dish or pad part), also when a point above that frame is set down onto
  it: warning `supported_by_placement`, checked again once that frame stands. So may a face-mounted piece (a solar
  panel, a wall light) whose support behind it, a frame in the cell behind or a plate on that face (a frame only for a
  piece that requires one), an earlier placement puts there. When the reason is "requires a Frame below" and a frame fills the spot's own cell, `cannot_place`
  says so: such pieces stand in the free cell on top of a frame.
- **Cost:** every build state's items up to the chosen state, from your inventory or `from_id`. `free: true` places
  without materials, in creative worlds only (`not_creative` otherwise). It waives the materials, never the checks:
  every placement is one a player's cursor would accept there.
- **Refused per placement:** `invalid_prefab` (not loaded, not a structure, no kit builds it),
  `invalid_rotation`, `invalid_build_state`, `cannot_place` (with the game's reason; a broken structure in the way is
  named, with how to remove it),
  `not_labelable`, `not_paintable`, `invalid_color`, `overlaps_placement` (two placements of the request in one slot,
  or one the game would refuse once an earlier one stands: two small-grid pieces taking the same slot of a cell, such
  as overlapping long pipes, or a frame and another piece in its 2 m cell, such as a wall facing into it, in either
  order). The ports and port checks of a placement read only what stands now, not the earlier placements of the same
  request.
- **Rockets (1.6.0+)** are built as a player builds them, with the game's own rules and reasons: the launch mount on
  four support frames, the engine fuselage on a fully built launch mount, fuselage pieces and the nose cone on a
  fuselage, never beside another rocket; engines, tanks, batteries, avionics, the miner, scanner, cargo bays and the
  umbilical sockets in the small cells the fuselage gives them, each cell taking only its kind; a male umbilical in a
  Rocket Tower. What is built joins the rocket exactly as a hand-built piece does (its network, internals, mass,
  saves). A rocket built bottom up in one request works: each placement that waits for an earlier one (the pillar
  frames, the mount or fuselage below, the fuselage whose cells take an internal, the tower) gets the warning
  `supported_by_placement`, and the job checks it again once that stands.
- **Replacing a fuselage piece:** placing a fuselage piece where another of its family stands (a plain fuselage by
  one with doors, say) replaces it as a player with an angle grinder in the other hand does: warning
  `replaces_fuselage`; the old piece is destroyed and gives nothing back, and no kit is taken for the new one (the
  states above the kit are charged as usual). Only where a merge kit builds the prefab.
- **Moving rockets:** nothing is built into or taken from a rocket while it launches or lands (the tool's own rule:
  its parts move with it then).
- **Walls back to back:** a face holds one wall per side, so two plates on one face, one facing into each cell, go
  in one request.

### Seeing a wall and finding a spot (1.4.3+)

`wall_map {looking: true}` (or `{plane: "z=668", around: [719, 201, 668], side: "+z"}`) draws the wall as you see
it from that side, 0.5 m per character, a ruler line marking the 2 m seams:

```
|   |  
WWWWWWW
WpAAWWW
WWAAWWW
```

`W` wall, `G` window, `D` door, `F` frame with no plate, `.` open, `x` a door's keep-out, `c`/`p`/`b`/`h` runs, and
a capital or digit for each device (`things` lists them). A device's key covers every cell its mesh reaches over by
more than 0.1 m, not only the cells the game registers it in: a 3x3 console registers 1 x 1 m but its frame draws
about 1.5 x 1.5 m, so it shows as 3 x 3 characters (1.4.4+). `top_left` is the world point of the first character's
cell, on the plane. `sections` names each 2 m face and what stands on it. `free_rects: {w: 1, h: 1}` lists where a
rectangle of free wall fits, within one section by default. A small cell on a 2 m seam belongs to the section on its
plus side, whichever side the map is seen from. A passive vent or in-line tank shows as a thing with its own key, not
as pipe.

`find_spot {prefab, near, plane | looking | room_id, require}` tries every 0.5 m spot within `radius_m` of `near`
(nearest first over every plane, at most 4000; with `room_id` the planes are the room's walls, not its floor or
ceiling: name one of those with `plane` and it is searched too, seen from the room's side unless `side` says otherwise;
seen from the side away from the room, e.g. under its floor, its spots are those with the room behind them),
filters them on geometry first (cells free, its mesh clear of every other thing's mesh with `no_visual_overlap`,
`avoid_doors`, `one_section` by its mesh, `min_bottom_above_floor_m`, `front_clear_m`), then checks the nearest ones (at most `max_checks`) with the game's cursor and the layout preview
(`no_visual_overlap`, `ports_reachable`), and returns the best with ready `place_arguments`. `reasons` counts why the
other spots were ruled out, most frequent first, so an empty answer says what stood in the way.

### Seeing it before building (1.4.3+)

`show_preview` takes the same placement fields as `place_structure` and draws, on your screen only, each
placement's footprint (green, red with a problem), its render box (white) and its port cells (cyan, red when
blocked), for `seconds` (default 30). `cells` (yellow 0.5 m cubes, e.g. a planned route) and `boxes` can be drawn
too; a new call replaces the last (`keep: true` adds), `clear: true` removes them. Nothing in the world changes and
other players see nothing.

### Checking a layout (1.4.3+)

`lint_layout {room_id}` (or a box) reads what stands there and lists findings, problems first, then warnings, with
`counts` per rule. A box takes the 2 m cells it overlaps; a side on a face plane takes nothing beyond it (a box up to y 222 stops
below that floor).

| Rule | Level | Finds |
| --- | --- | --- |
| `not_replaceable` | problem | a piece, device or 2 m structure a player could not place again where it stands (1.5.0+; `check_replaceable` below) |
| `run_in_door_keepout` | warning | a cable, pipe or chute piece in a door's keep-out |
| `port_into_doorway` | warning | a device port that joins in a door's keep-out |
| `port_cell_foreign_network` | warning | a port whose joining cell holds a piece that does not join it |
| `floating_run` | warning | a cable, pipe or chute piece in air (in-line tanks and passive vents are not runs) |
| `run_crosses_window` | warning | a piece on a window's face |
| `device_visual_overlap` | warning | two devices whose mesh boxes run more than 0.1 m into each other, or one inside the other |
| `mounted_faces_out_of_room` | warning | a mounted device facing out of the room behind it |
| `device_crosses_seam` | warning | a mounted device whose mesh spans two wall sections by more than 0.1 m, though it is small enough (2.2 m or less each way) to fit one |
| `run_along_door` | info | a cable, pipe or chute piece hugging a door's jamb (`pipe_along_door` before 1.4.4) |
| `controls_not_on_wall` | info | a console, computer, display or switch not on a wall |
| `replaceable_unchecked` | info | a thing `not_replaceable` could not ask about (no placement cursor for its prefab) |

### Could a player place it again (1.5.0+)

Everything StationGod builds must be something a player could build there: a vent that works but has nothing behind
it can never be rebuilt once it breaks. `place_structure`, `place_cables`, `place_pipes`, `place_chutes`,
`replace_walls`, `replace_frames` and `undo_job` all refuse what a player's placement cursor would refuse; `free`
waives materials only. BlueprintMod's paste (`paste_blueprint`, `bppaste`) spawns pieces without any of these checks,
so check what it placed:

```json
{ "reference_ids": ["171026", "171027"] }
```

`check_replaceable` puts the game's own placement cursor for each thing's prefab at its exact position and turn and
asks it as a player's cursor would: the class's own rule (a frame below a battery, printer or machine; a frame one grid
down for dishes, radiators, wind turbines, landing pads and stairs; the straight pipe or cable a pipe- or
cable-mounted device sits on; terrain, outside, a rocket; no port straight onto another device's port), support
behind every face-mounted piece, and collisions. The thing itself is treated as gone, every neighbour stays. It must
also stand where the cursor snaps it, at a quarter turn the cursor gives its prefab. Each result is
`{reference_id, prefab_name, replaceable, rule, reason}`: `replaceable` true, false (`rule` one of `support`, `mount`,
`host`, `location`, `adjacent`, `collision`, `rotation`, `no_kit`, `off_grid`, or null for a reason the table does not
know; `reason` the game's text) or null (not checked: no placement cursor for the prefab, which the game makes for every
structure prefab at start, a dedicated server too, so this is rare; not a structure; no such thing). Up to 1024 ids per
call. `lint_layout`'s `not_replaceable` runs the same check over a room or box.

Not seen: a device whose own check stops at itself before the port rule (vents, lights, consoles, APCs) is not checked
for ports straight onto other devices' ports; loose things inside are ignored.

The game's frame cursor looks only at 2 m structures, not at small-grid devices, so a player (and `place_structure`)
can put a frame around a station battery or another device that needs a frame below. The game allows it; the device
then stands inside a frame, not on one, and `check_replaceable` answers false (`support`) for it.

### Placing where you look (1.4.3+)

`at` can be read against the world, and the reply's `resolved` says where it landed and how:

- `{"crosshair": true}`: where your look ray hits (within 10 m), optionally moved by `right_m`, `up_m`, `forward_m`.
- `{"relative_to": "player" | "crosshair" | "<id>" | {"reference_id": "<id>"}, "frame": "player" | "world" | "target",
  "right_m": 1, "up_m": 0, "forward_m": 2, "from": "top"}`: an offset from you, the crosshair or a thing. The player
  frame is level: its right and forward are the world axes nearest yours (`ambiguous_axis` when you look within 10
  degrees of a diagonal). The target frame is the thing's own turn (the default for a thing); `from` starts at the
  middle of a side of its footprint (`top`, `bottom`, `left`, `right`, `front`, `back`) instead of its origin.
- `{"on_face_i_look_at": true, "along_right_m": 0.5, "along_up_m": 1}`: on the wall, floor or ceiling you look at,
  right and up as you see them.
- `above_floor_m`: its bottom (the bottom of its mesh, what stands on the floor) that high above the floor below `at`
  (a floor plate, or a frame under the plane, within 10 m; with none the placement is refused). The cursor snaps to
  0.5 m, so the snap whose bottom lands nearest the height asked is used; `resolved.at_how` says where it ended up.
- The player frame, `crosshair` and `on_face_i_look_at` need a player camera: a dedicated server answers `no_camera`,
  so use frame `world` or `target` there.
- `facing` also takes `toward_player`, `away_from_player`, `out_of_face` (the face you look at) and `into_room`.

### Placing by intent (1.4.3+)

Instead of `rotation`, `facing`, `face` or `up`, give `orient` and let the tool pick the turn:

```json
{ "prefab": "StructureConsole3x3", "at": [719, 200.5, 668],
  "orient": { "mount": "wall", "controls_toward": "room" } }
```

- `mount`: `wall`, `floor`, `ceiling`, or the axis the surface is on.
- `upright` (default true): its visual top points up.
- `controls_toward`, and each of `ports: [{role, index, type, toward}]`, and `flow: {from, to}` (inputs face `from`,
  outputs `to`) take a target: an axis, `room`, `player`, a point, or `{reference_id}`.

Every turn the placement cursor allows is aimed and checked as a plain placement would be, scored (refused or off
the mount: excluded; not upright: +20; each target missed: up to +10 by angle; the layout preview's conflicts:
problem 100, warning 10) and the best used. The reply's `orient` echoes `chosen` with its `reasons`, the next three
`alternatives`, and `tried`. A turbo volume pump is also scored with its flow reversed; when that wins, `mode_flip`
says to write `Mode` 1 after building.

`describe_prefab {prefab}` shows a prefab before it stands anywhere: how it is placed and turned
(`allowed_rotations`), its small cells and boxes relative to its origin, its ports (joining cell offset, the way a
run leaves, role, flow), `visual_up` (which own axis reads as its top, with the source of that fact; `verified:
false` marks a guess; a prefab with no entry of its own is verified +y up only when the cursor can turn it about y
alone, and otherwise reads `ASSUMED`), a flow its `Mode` reverses, and `controls` (1.7.0+): the own axis its slots,
buttons and switches face, read from the colliders of the game's own interactables against its mesh box; when none
sits clearly on one side its forward (+z) stands in (`fallback: true`).

### The layout preview (1.4.3+)

Every placement's dry run carries `layout`, read from the game's own data for that prefab at that position and turn:

- `footprint`: the small cells the game would register it in (its real footprint, from the prefab's grid bounds),
  2 m cells for grid structures, `body` (`render_box`, the box its meshes fill, and `grid_box`, the footprint's box),
  and `mount`: the face plane behind it (`z=668`) and the rectangle its mesh box covers there (1.4.4+). The cells
  themselves are listed only with `include_footprint_cells: true` (1.7.0+); without it `small_cells` is its `count`
  and `large_cells` is left out.
- `sections`: the 2 m wall (or floor) sections that rectangle covers by more than 0.1 m, with what stands on each, and
  `crosses_seam`.
- `conflicts`, each `{code, level, message, reference_id}`; warnings also appear in the report's `warnings`:

| Code | Level | Meaning |
| --- | --- | --- |
| `visual_overlap` | warning | Its mesh box and another thing's run more than 0.1 m into each other, or one lies inside the other (1.4.4+). A small device under a console's overhang clashes even where their small cells do not; neighbours flush on one wall only touch, or overlap by a rim, and do not; a thing sharing one of its cells (a device on a pipe) is skipped. |
| `crosses_section_seam` | warning | Its mesh spans more than one 2 m section by more than 0.1 m, though it could fit one (neither side wider than 2.2 m), so shifting it fixes it. Not given for cable, pipe and chute pieces, in-line tanks and passive vents, which rest on no section, nor for a piece wider than a section (a medium dish, a landing pad part), which crosses a seam wherever it stands; `sections.crosses_seam` still reports the span. |
| `in_door_keepout` | problem | A cell in a door's keep-out; `allow_door_keepout` makes it a warning. |
| `crosses_window` | warning | It stands on or rests against a window. |
| `blocks_route_cells` | warning | It would take the joining cell of a free port of a device beside it. |
| `front_blocked` | warning | Something stands right in front of a mounted piece, or its front faces into a frame. |
| `controls_blocked` | warning (info for the fallback) | The side with its slots, buttons and switches (`describe_prefab` `controls`) faces another device, a chute or small thing, a frame's body, or a wall or frame on the plane right in front of it (1.7.0+). `orient` pays for it like any warning and aims `controls_toward` at that side. |
| `faces_out_of_room` | warning | A mounted piece whose back is in a room and whose front is not. |
| `not_upright` | warning (info for in-line tanks) | Its visual top does not point up. |

- `port_checks`: each port with what stands in its joining cell now (`occupant`), whether that piece `joins` it on
  build and `would_join_network`, why it is `blocked`, its `flow` (`in` or `out`) and whether its cell is in a door's
  keep-out.

## Removing structures

`remove_structure {reference_ids}` gives back what hand deconstruction does, every build state's items down to the
kit, where `refund_to` says: by default `["inventory", "source", "storage", "ground"]` (your inventory, then
`from_id` topped up when it is a stack, then `from_id`'s slots and its container's, then the ground in front of
you), any list of those targets and container ids, or one word as before: `source` (into `from_id`'s inventory,
default yours), `ground` (where each piece stood) or `none`. The dry run's `refund_plan` shows where each item would
go; see [building.md](building.md#how-every-building-tool-works), item 7.

| Code | Meaning | Override |
| --- | --- | --- |
| `not_a_structure` | An item or another movable thing: use `move_item`. | none |
| `being_destroyed`, `indestructible`, `game_refuses` | The game would not deconstruct it. A fuselage piece is judged as its last step would be: nothing on top of it, and no internals left in its cells (both unless the same request removes them). | none |
| `rocket_moving` | Part of a rocket that is launching or landing (the tool's own rule). | none |
| `has_mounted` | A device is mounted on it (a light, sensor, console or vent on a wall), or stands on it, and nothing else would hold that face: a plate on the same face, or a frame beside it. The game would leave the device hanging in the air. Remove the device in the same request, or first. | none |
| `broken` | It is broken (`is_broken`): fire, pressure or other damage wrecked it, including a burst pipe or a burnt cable. The game cannot repair a broken structure, only deconstruct it, and that gives nothing back. | `allow_broken` |
| `holds_items`, `holds_gas` | Items drop where it stood, as in the game; a tank lets its gas out into its cell, other devices lose it. An in-line tank or passive vent that is the last of its pipe network (with the rest of the request) takes the network's gas with it: the game deletes it. So does one left as the last of a part of a network the request splits, since the job removes pipe pieces first, and so does a pipe piece left as the last of such a part when the request removes pipe pieces alone. The job's gas check expects exactly that gas gone (`planned_loss_mol`) and does not put it back. | `allow_contents` |
| `contents_would_move` | An in-line tank or passive vent between pipes of a network that holds gas or liquid: removing it splits the network, and the game divides the contents among the networks left by volume (the message names each share). `remove_pipes` refuses the same split. | `allow_contents` |
| `would_breach` | It blocks air, and removing it joins spaces whose pressures differ by 1 kPa or more, such as a pressurised room and the outside. | `allow_breach` |
| `would_burst` | An in-line tank or passive vent that is not the last of its pipe network takes its volume away, and the game keeps the network's gas in what is left. The pressure of what is left would be over its weakest pipe, which would burst; the message gives the forecast and how much gas to take out first. Removed with pipe pieces of its network, or alone, the network the request leaves in one piece keeps all its gas and its id (every removed member leaves it first), so `will_burst` names the network as it stays; where the request splits it, each part is a new network with its share by volume at each split, in the job's order. The same model judges pipe pieces removed alone, so `allow_burst` also lets a request of pipe pieces leave a network over its weakest pipe, which `remove_pipes` refuses outright: use it only where a burst is acceptable, such as outdoors. | `allow_burst` |
| `refund_holder_removed` | `from_id` (when `refund_to` uses it) or a container `refund_to` names is removed by the same request, or is inside something it removes: the refund would be destroyed with it. | another `from_id`, or `refund_to` |
| `port_left_open` (warning) | A device end that joins a cable, pipe, chute or device now. | not needed |

An allowed guard becomes a warning with its own code: `broken_removed`, `items_dropped`, `gas_released` (a tank's gas
let out where it stood), `contents_deleted` (gas or liquid the game deletes with what is removed; not the job status
`gas_lost`, which only a failed gas check gives) and `breach`; an allowed `contents_would_move` or `holds_contents`
keeps its code as a warning.

The breach check judges the whole request at once, by the game's own air rule: a face stays sealed while anything left
on it blocks air, or while the structure filling a cell beside it does (a finished frame). So a wall plate on a
finished frame's face never breaches, and two plates back to back on one face breach only when both are removed in the
same request; that breach is reported once, on the first of them. A wall and the frame behind it removed together
are one opening too, reported once. With `refund_to: "none"` the dry run lists no refund.

A rocket's parts go as a player takes them down: everything else in the request first, then the fuselage pieces from
the top down, so a whole rocket can be removed in one request (internals, then nose cone, fuselage, engine fuselage).

Cable, pipe and chute pieces are removed as the remove tools remove them, with their checks (a pipe network's `holds_contents` and `would_burst` come from the same model of what the job leaves as above, as `holds_gas` and `would_burst`, pipe pieces alone too); `would_split` is only a
warning here, so read it (it does not ask for `allow_split` or `root`, which `remove_structure` does not take; price
the split against a root with `plan_removal`). Those checks run with `remove_structure`'s own refund (`refund_to`, `from_id`), so they need
no player on a dedicated server.

### Letting a pipe burst (`allow_burst`)

Outdoors a burst into the atmosphere can be acceptable. `allow_burst: true` lets the dry run and the real run go ahead
where `would_burst` would refuse; every other check still applies. The reply warns `will_burst` once for each network
left over its weakest pipe, and lists the same in `will_burst`:

- the forecast: `network_id`, `networks_left`, `volume_left_l`, `pressure_now_kpa`, `pressure_after_kpa` and
  `rating_kpa` (the weakest pipe left);
- `pipes` expected to burst: the weakest pipes left that stand in a cell holding air, each with `reference_id`,
  `prefab_name`, `rating_kpa`, `where` and `pressure_there_kpa`. The game damages only a pipe in such a cell, so a
  network whose weakest pipes all sit inside walls or frames bursts nowhere: `pipes` is empty and nothing leaks;
- `where` they leak: a room id, or `outdoors` when no closed room has the cell;
- the gas expected out: a burst pipe leaks until the network is down to the pressure where it leaks, so
  `released_mol` of the `holds_mol` the network holds, gas by gas in `gases`.

The burst itself comes on a later game tick, after the job. The job's gas check expects that release
(`planned_release_mol` on the family): anything from none of it gone to all of it is fine and is never put back, so
the release alone never ends the job `gas_lost`. Without `allow_burst` nothing changes: the removal is refused.

### Broken structures

A structure that reaches full damage and has a broken model (a burnt-out vent, a burst pipe) is not destroyed: the
game swaps in the broken model and heals the damage, so it reads 0 damage and 100 % health while it is a wreck. It
still takes its place, so nothing can be built there. `find_things {broken: true}` and `thing_health {broken_only:
true}` list them; both report `is_broken` and `condition: "broken"`. A burst pipe (bursting does not damage it) and
a burnt cable (the separate, undamaged piece an overload leaves) count as broken too, here and in `remove_structure`.

`remove_structure {reference_ids, allow_broken: true}` removes them as the game's own deconstruction of a broken thing
does: nothing is given back (the game skips the kit refund for a broken thing), and the game's own deconstruct refusal
is not asked, since the game does not ask it for a broken thing either. Every other check still applies: items or gas
inside, a mounted device, a breach, and for pipe and cable pieces the network checks and the gas check. Placing a
structure where a broken one stands stays refused (`cannot_place` names the broken one); remove it first.
