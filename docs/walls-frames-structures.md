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
| `place_structure` | Place any kit-built structure at a position and turn, at a build state, with a label and colour. Up to 64 in one job. | `prefab`, `at`, `facing` / `rotation` / `face`, `build_state`, `label`, `color`; or `placements: [...]` |
| `remove_structure` | Remove structures as deconstructing them by hand would. Up to 256 in one job. | `reference_ids`, `allow_contents`, `allow_breach`, `refund_to` |

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
  window, window to wall. Only plain walls and windows are touched; shuttered windows, floors, ladder platforms and
  crew umbilical doors are kept as `special_piece`.
- Frames: iron to steel and back, and any frame to its own prefab, which finishes it. Rocket towers are never touched.
- Scope: `reference_ids` (up to 4096) or `room_id`: for walls every wall on a face of the room's cells, for frames every
  frame next to the room. `from_prefabs` narrows either.

**How a swap never opens a room.** With the game tick held, each old piece is marked as being destroyed, which lets
the game hand its place to the new piece. The new piece is built in the same place with the old one's owner and colour
and raised at once to its final build state. Only when it holds every place the old one held, and blocks what the old
one blocked, is the old piece removed. If not, the new piece is taken away, the old one gets its place back, and the run
stops there. The next frame the mod checks every new piece and that the air around them is exactly as it was. After
the game has run and re-evaluated its rooms, it checks that every room beside a swapped piece still exists with the same
id, cells and air (within 1 %). A room that appears because a finished frame closed a space is listed in `new_rooms`.

**What is refused.**

| Code | Why |
| --- | --- |
| `footprint_mismatch` | The new piece would not take exactly the old one's place. `skip_unmatched: true` leaves such pieces. |
| `would_open` | The old piece blocks air or gravity and the target does not. Making a leaky piece airtight is allowed (`seals`). |
| `would_overstress` (walls) | The face's pressure difference is at or above what the new wall bears; the game would damage it until it breaks. Above its stress mark only a `stressed` warning. |
| `cell_occupied` (frames) | Finishing a frame closes its cell, which would seal in whatever is there: a pipe, cable, device, player, creature or loose item. |
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
   `build_state` and `cost`.
3. The same call with `dry_run: false, confirm: true`; poll `{job_id}`. `result.verification` confirms each piece
   stands as planned: prefab, position, turn, build state, label and colour.

- **Position:** `at` is any point in the cell, snapped as the placement cursor snaps it. A 2 m device snaps to its
  cell's centre. A 0.5 m-grid device (a battery, a valve, a transformer) that cannot be built at the point as given is
  set down on the surface behind it, as the cursor's ray lands on a surface: the floor plane below a standing device,
  the face at the back of a mounted one. So a cell's centre works; a point as given that can be built is kept.
- **Turn:** at most one of `rotation` (`[x, y, z]` degrees, multiples of 90), `facing` (`+x`, `-x`, `+y`, `-y`, `+z`,
  `-z`) with an optional `up`, or `face` for pieces placed on a cell face such as walls: `face: "+x"` puts the piece on
  the cell's +x face, looking into the cell. A grid piece may only turn about the axes its cursor turns it
  (`invalid_rotation`).
- **Build state:** `finished` (default), `first` (as a kit leaves it, costing only the kit), or an index.
- **Label and colour:** `label` as the Labeller writes it; `color` a name or index (`paint` lists them).
- **Checks:** the game's own placement cursor for that prefab (blocked cells and faces, collisions, each class's own
  rules, support for face-mounted pieces), nothing loose and nobody inside a piece that fills its cell. The check runs
  again just before each piece is built, so later placements see earlier ones.
- **Cost:** every build state's items up to the chosen state, from your inventory or `from_id`. `free: true` places
  without materials, in creative worlds only (`not_creative` otherwise).
- **Refused per placement:** `invalid_prefab` (not loaded, not a structure, no kit builds it, a rocket part),
  `invalid_rotation`, `invalid_build_state`, `cannot_place` (with the game's reason, or a cell inside a rocket),
  `not_labelable`, `not_paintable`, `invalid_color`, `overlaps_placement` (two placements of the request in one slot).
- **Rocket parts** are what the game places only in a rocket (strictly internal pieces), the fuselage and the launch
  mount. Batteries, tanks, pipes, valves, vents and other devices that may also be fitted in a rocket are placed as
  usual.
- **Walls back to back:** a face holds one wall per side, so two plates on one face, one facing into each cell, go
  in one request.

## Removing structures

`remove_structure {reference_ids}` gives back what hand deconstruction does, every build state's items down to the
kit: into your inventory (`refund_to: "source"`, the default, or `from_id`'s), on the ground where each piece stood
(`ground`), or not at all (`none`).

| Code | Meaning | Override |
| --- | --- | --- |
| `not_a_structure` | An item: use `move_item`. | none |
| `being_destroyed`, `indestructible`, `rocket`, `game_refuses`, `has_mounted` | The game would not deconstruct it, or a device is mounted on it. | none |
| `broken` | A damaged build state; repair it first. | none |
| `holds_items`, `holds_gas` | Items drop where it stood, as in the game; a tank lets its gas out into its cell, other devices lose it. | `allow_contents` |
| `would_breach` | It blocks air, and removing it joins spaces whose pressures differ by 1 kPa or more, such as a pressurised room and the outside. | `allow_breach` |
| `port_left_open` (warning) | A device end that joins a cable, pipe, chute or device now. | not needed |

The breach check judges the whole request at once, by the game's own air rule: a face stays sealed while anything left
on it blocks air, or while the structure filling a cell beside it does (a finished frame). So a wall plate on a
finished frame's face never breaches, and two plates back to back on one face breach only when both are removed in the
same request; that breach is reported once, on the first of them. `rocket` means part of a rocket: placed in one, a
rocket-only piece, a fuselage or a launch mount.

Cable, pipe and chute pieces are removed as the remove tools remove them, with their checks; `would_split` is only a
warning here, so read it.
