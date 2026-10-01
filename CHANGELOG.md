# Changelog

## 1.7.0

2026-09-30. Smaller replies: ask a tool for only what you need. 84 tools.

- **`grid_survey` filters.** A survey of a 6 x 4 x 4 m box ran to 63 KB. `sections` (any of `cells`, `pieces`,
  `devices`, `networks`, `network_visibility`, `doors`) keeps only the parts named, the others left out of the reply;
  `network_ids` (a network id, or any cable, pipe or chute piece on it) keeps only the pieces on those networks, the
  devices with a port on one of them, and those networks; `compact: true` leaves out each cell's two 64-character
  strings (`small`, `support`) and the legend. Without them the reply is unchanged.
- **`get_ic_status` without the source.** `include_source: false` answers `source` null while `source_length` still
  gives its length, for polling a chip's log or state without a long Lua program in every reply. Default true.
- **`item_totals` holders.** `holders_limit` sets how many holders each item type lists (default 5, as before; 0 leaves
  `top_holders` out).
- **Changed default: job polls are brief.** Polling a `place_structure`, `remove_structure`, `place_*` or `remove_*`
  job with `job_id` answers its status and what it did (pieces, materials, verification, gas check, error) with
  `preflight` null and the held tick's `final_check` left out: both repeat the reports the real run already answered
  with. A refused job keeps `final_check`, which holds why. `verbose: true` gives the whole job as before.
- **Changed default: footprint cells.** Each `place_structure` layout preview gives `footprint.small_cells` as its
  count only and leaves `large_cells` out (a small in-line tank listed 36 cells, in the dry run, the job and every
  poll); `include_footprint_cells: true` lists them.
- **Changed default: run reports.** `place_*`, `remove_*`, `plan_*_route` and `plan_removal` give `links` as counts
  (`added_count`, `lost_count`, new) and leave out the tool's fixed `notes`; `include_links: true` lists every added
  and lost link (34 entries for 15 pipes), `include_notes: true` adds the notes. `limit: 0` lists no cells (the counts
  stay), also for the dry run a `plan_*_route` reply carries.
- **Controls facing a wall.** `place_structure` (dry run and job checks), `orient` and `find_spot` now know which side
  of a device carries its slots, buttons and switches, read from the game's own interactables on the prefab, and warn
  `controls_blocked` when that side faces another device, a frame, or a wall panel right in front of it. `orient`
  avoids such turns and aims `controls_toward` at that side; `find_spot` keeps that side clear. `describe_prefab`
  reports it as `controls` (with `fallback: true` where no control sits clearly on one side and the device's forward
  stands in; the warning is then only information).
- **Fix: `describe_prefab` no longer claims every prefab's top is checked.** `visual_up` said local +y, verified, for
  any prefab without an entry of its own. That is now verified only where the game forces it (the placement cursor
  turns the prefab about y alone, so it always stands +y up); anything a player can build any way up reads
  `verified: false` with the source `ASSUMED`. The modded Compact Filtration (plain and Mirror) gets an entry: its
  top is local -y (two units built +y up under a ceiling read upside down), and it is built on its side as often, so
  `not_upright` is only information for it and `orient` no longer turns it +y up.

## 1.6.2

2026-09-30. Fix: frame removal gives the cell back to the air as hand deconstruction does. 84 tools.

- **A removed frame no longer leaves outdoor air behind.** A finished frame fills its cell, which then holds no
  atmosphere. The game's last deconstruction step sets the frame's build state to -1 before destroying it
  (`Structure.AttackWith`), so as it leaves the grid it lets air pass and its cell is released
  (`AtmosphericEventInstance.StructureReleaseGrid`): the cell gets an atmosphere of its own, empty unless one of its
  open neighbours is outdoors with no air of its own and none belongs to a room. `remove_structure` destroyed the frame
  at its finished state, so the game queued a blocking event instead, which does nothing in a cell without air; the
  cell stayed without an atmosphere, which the game reads and mixes as planet air. A block of frames hollowed out from
  inside a vacuum room filled with outdoor air, even a cell boxed in by frames on every side. Now the frame's build
  state goes to -1 through the game's setter and the game's own air checks release the cell before it is destroyed,
  and each release is applied at once, as the ticks between a player's steps apply it: in a request of many frames
  each cell is judged with the cells removed before it already holding air and the later ones still filled, as by
  hand. Walls, devices and pieces are removed as before.

## 1.6.1

2026-09-30. Fix: chutes join rocket chute umbilicals. 84 tools.

- **A chute umbilical's port carries items both ways.** `place_chutes` refused the piece joining a chute umbilical's
  port with `flow_conflict`, and `plan_chute_route` refused it as a `from`, because the port's role reads Input. In
  the game it is two-way: a chute pushes items into the umbilical through it (a chute feeds any chute device whose
  end there is not an Output), and the partner umbilical pushes its item out into the chute at this port
  (`RocketChuteUmbilicalMale` and `RocketChuteUmbilicalFemale` `OnServerTick`). The flow checks, route ends,
  `describe_prefab` and the placement port checks now treat the port of the male, the socket and the socket angle as
  fixing no direction (`flow` null). Every other guard stays: a line between the umbilical and a device port still
  follows that port's direction. The other chute devices' ports were checked against their code and prefabs and move
  items the way their roles say.

## 1.6.0

2026-09-30. Rockets: the build tools build and edit them as a player does. 84 tools.

- **`place_structure` builds rocket parts.** Launch mount, engine fuselage, fuselage pieces, nose cone, and the
  internals (engines, tanks, batteries, avionics, miner, scanner, cargo bays, downlink, umbilical sockets...) were
  refused as "a rocket part". They are now placed through the same cursor check as everything else, which is where the
  game keeps every rocket rule (the fuselage on a fully built launch mount or on another fuselage, never beside another
  rocket; an engine in an engine fuselage; each internal cell taking only its kind; a male umbilical in a Rocket Tower;
  the launch mount on four support frames), so a placement the game would refuse is refused with the game's reason.
  The piece is built as a kit builds it and joins the rocket the way a hand-built one does (its network, internals,
  mass and save data are the game's own doing). `describe_prefab` describes them too.
- **A rocket in one request.** Placements that wait for an earlier one of the same request (the pillar frames, the
  mount or fuselage below, the fuselage whose cells take an internal, the tower) get `supported_by_placement` and are
  checked again by the job once that stands, as a frame below already was.
- **Replacing a fuselage piece.** A fuselage piece placed where another of its family stands replaces it, as a player
  with an angle grinder in the other hand does: `replaces_fuselage`; the old piece gives nothing back and no kit is
  taken for the new one. Only where a merge kit builds the prefab.
- **Pieces inside rockets.** `place_cables`, `place_pipes` and `place_chutes` (and the route planners) no longer treat
  every rocket cell as blocked: a cell takes a piece when its fuselage made it a cell of that kind, and a free cell
  right above or below a rocket's cell takes none, as in the game. Such cells count as held up (no `through_air`).
- **Taking rockets apart.** `remove_structure`, `remove_cables`, `remove_pipes` and `remove_chutes` no longer refuse
  rocket parts. A fuselage piece is judged as its last deconstruction step would be (nothing on top of it, no
  internals left in its cells), both unless the same request removes them; a request's fuselage pieces go last, from
  the top down, so a whole rocket goes in one request. The `rocket` refusal code is gone; `rocket_moving` refuses
  parts of a rocket that is launching or landing, the tools' own rule (its parts move with it then).
- **Umbilical pairing.** `describe_device` and `grid_survey` devices report `umbilical` for rocket umbilicals: the
  partner the game holds, `partner_distance`, and the game's partner search replayed (`found`, `how`, and per column
  why it stopped). `describe_device` also reports a rocket device's `rocket` (network, name, state, dry mass, hull
  pieces, internals).
- The clean and upgrade tools still leave rocket pieces alone.

## 1.5.0

2026-09-29. Fixes from a twelve-round live test of 1.4.4 on a dedicated server, the player-placement
rule, and new controls over refunds and the gas hold. 84 tools.

- **A job that takes nothing needs no player (live test round 11).** On a dedicated server with no `from_id`, the
  clean, upgrade and replace tools asked for one even when they took no coils or materials: `clean_cables`
  `remove_dead_ends` and `merge_straights`, a `replace_walls` swap that charges nothing. Only a charge or `refund_to`
  `"source"` needs one now; the refund follows the chain, the ground where the first piece stood at the end.
- **Refund skips say why.** `storage` with a `from_id` that has no slots (a cable piece) is skipped with
  `refund_target_skipped` as `source` is; the skip text tells no `from_id` from one that names no thing. A `from_id`
  the same request removes is refused (`refund_holder_removed`) when `refund_to` gives into it on the remove, place,
  clean, upgrade and replace tools too, as on `remove_structure`.
- **A lone in-line tank keeps its network's id.** `remove_structure` of an in-line tank or passive vent that leaves
  its network in one piece takes it out of the network first, as with pipe pieces, so the network keeps its id and
  `will_burst` names the network that stays. `allow_burst` also lifts `would_burst` for pipe pieces alone; documented.
- **`gas_hold` on every poll.** A run that acknowledged a gas loss repeats its `gas_hold` in every `job_id` poll, the
  finished job's too. An empty `acknowledge_gas_lost` is refused (`invalid_argument`) instead of read as left out.
- **Traders.** `trader_buy`'s real run reports `credits_spent` signed, as the dry run does (a negative-price line was
  +1.0 real and -1.0 dry). A stock refusal says "after the earlier lines" only when an earlier line bought from that
  entry. `trade_failed` drops the game's colour tags. `no_credit_card` says when there is no local player at all.
  `have` is documented as not capped by `wanted`; `get_ic_status` documents `error_line` as a string.

- **Choose where refunds go (`refund_to` on every tool that refunds).** The place tools with `remove_ids`, the remove,
  upgrade, clean and replace tools, `remove_structure`, `undo_job` and `plan_removal` take `refund_to`: a list of
  targets tried in turn per item until it fits, `inventory` (the player's), `source` (top up the `from_id` stack),
  `storage` (`from_id`'s slots, then its container's), a container's reference id, `ground`. The default is
  `["inventory", "source", "storage", "ground"]`; what fits no target goes on the ground in front of the holder.
  One word keeps its old meaning (`source`, `ground`, `none`; `refund: false` is `none`). Dry runs show `refund_plan`
  with each item's destinations; the job's refunded entries name their `target`. On a dedicated server the default
  skips the inventory, so a removal no longer needs `from_id` there.
- **`trader_inventory` `have` is what the trader would take from you.** It counts the goods on the pad network's
  vending machines and in the card holder's inventory that meet the trader's conditions, as `trader_sell` takes them
  (a "Box of ..." line counts only the boxes whose contents it accepts), and gas lines now count the pad network's gas
  in units. Before, it counted every item of the prefab in the world, conditions ignored. The pad is the one the
  trader is called to, else every landing pad's network; new optional `credit_card_id` picks the card holder
  (default you). `have` is now a whole number, never null.
- **`landing_pads` drops `obstructed`.** It reported the game's `LandingPadCenter.IsObstructed`, which nothing in the
  game calls, so it never decided a landing.
- **Only what a player could build.** One placement rule, the game's own cursor checks, now guards every tool that
  creates a structure. `free: true` waives materials, never the rule.
  - `check_replaceable` (new): for up to 1024 things, could a player place each again exactly where it stands, with
    its neighbours present; `replaceable` true/false/null with a `rule` (support, mount, host, location, adjacent,
    collision, rotation, no_kit, off_grid) and the game's reason. For checking what BlueprintMod pasted, which skips
    every placement check.
  - `lint_layout`: `not_replaceable` (problem) and `replaceable_unchecked` (info) on every piece, device and 2 m
    structure in the area; `structures` counts the 2 m ones.
  - `replace_walls`, `replace_frames`: `cannot_place` when a player could not place the new piece where the old one
    stands, including when the old piece stands off the cursor's grid or at a turn the cursor never gives.
  - `place_cables`, `place_pipes`, `place_chutes`: each piece's own rule too (no end entering an umbilical the way it
    faces; only a straight pipe along a pipe-mounted device, of its content), as `cell_blocked`. A pipe into an
    in-line tank's or passive vent's cell is refused by the planner first (`cannot_change`, `no_kit` or
    `long_piece`).
  - The game's frame cursor does not look at small-grid devices, so a frame can go around a station battery (a player
    can do it too); the battery then fails `check_replaceable` (`support`). Documented.
- **Names in messages.** A thing whose prefab has no English name (the game shows `<N:EN:StructureCrewUmbilicalDoor>`)
  is named by its prefab name in every message, in the game's own texts too ("Placement is blocked by
  StructureCrewUmbilicalDoor.", which lost the name before), and in every `display_name` field.
- **Acknowledging a gas loss lifts the gas hold.** After a pipe job ends `gas_lost`, pipe jobs no longer stay refused
  until the world is reloaded: a real run of `place_pipes`, `remove_pipes`, `upgrade_pipes`, `clean_pipes`,
  `place_structure`, `remove_structure` or `undo_job` with `acknowledge_gas_lost` naming the job that set the hold
  lifts it and goes on; the reply's `gas_hold` repeats the loss (job, networks, missing mol). Another job's id is
  refused (`gas_hold_mismatch`). Dry runs report `gas_hold` without lifting it. A later loss holds pipe jobs again.
  Agents must ask the user before acknowledging.
- **`remove_structure` `allow_burst`.** Removing an in-line tank or passive vent that squeezes the network left past
  its weakest pipe is still refused (`would_burst`) by default; `allow_burst: true` lets it go ahead where a burst is
  acceptable, e.g. outdoors. The reply warns `will_burst` for each such network, and its `will_burst` list gives the
  forecast pressure and rating, the pipes expected to burst, where each leaks (a room id or `outdoors`) and the gases
  and moles expected out. The job's gas check expects that release (`planned_release_mol`): it is never put back and
  never ends the job `gas_lost`.
- **`trader_buy` dry runs add the lines up.** Each line is checked against the credits, stock and empty slots the
  lines before it would leave, so the dry run refuses the lines the real run would (`insufficient_credits` and the
  rest) and `credits_after` no longer goes below 0. A line that runs out of room part way shows the part it would buy.
- **`created_by_part` `pieces`.** Pieces laid with the `pieces` form of `place_cables`, `place_pipes` and
  `place_chutes` (and `undo_job`'s rebuilds) are listed under `pieces`, no longer under `run`.
- **`undo_job` knows a job it already undid:** the plan says it was already undone and lists the undo's jobs in
  `plan.undone_by`, instead of reporting the world as diverged.
- **Arguments are checked against each tool's schema.** The sidecar holds every call to the input schema `tools/list`
  publishes before it reaches the game: a property the tool does not declare (named, with the nearest declared name),
  a value of the wrong JSON type (ids sent as numbers included: ids are strings), a missing or null required
  argument, an array outside its size, an enum value not listed, a number past a double's range and a key given
  twice are `invalid_argument`. JSON null is an omitted argument; integers written `3.0` or `1e2` are accepted.
  `min_moles` (`water_sources`) and `min_ratio` (`thing_health`) stay as deprecated aliases. An enum argument names
  exactly one member (`"Error,PressureInternal"` was ORed into `Setting` and written there). Pipe clients such as
  the dashboard stay lenient.
- **One error envelope.** Every tool error is `{code, message}` as both the text and `structuredContent`; a line
  that is not JSON gets -32700 with the id where it can be recovered; `game_unavailable` says whether no pipe
  answered within 3 s or the game took the request and did not reply within 35 s (it may still have run).
- **No item lost in a hidden slot.** `move_item`, refunds and `vault_withdraw` skip slots a player cannot click
  (the game's `HandleSwitch` rule) and a stack's own slot (a cable coil destroyed what was put there); moving out of
  a vending machine's store is one-way. Refunds top up the `from_id` stack itself first, walk a stored source's
  holders level by level, and put what is left on the ground in front of the outermost holder, not at the world
  origin.
- **Growers.** A seed or plant moved into a grower's plant slot is planted as a player plants it (one unit, a new
  plant with its genes); a fertiliser slot takes only fertiliser, one unit into an empty slot; `auto` puts each kind
  only into its own slot and never takes out a growing plant.
- **Gas kept through pipe jobs.**
  - `clean_pipes` `split_long_straights` no longer leaves a copy of a network's gas on a network the game no longer
    lists; the gas check reads such orphans, so a duplicate fails it.
  - The gas check links networks by the pipes and the cells they fill, not only by pipe id: a long straight swapped
    for its singles is one family, not a false `gas_lost` that held every pipe job in the world.
  - Changing the only pipe of a gas network (straight to tee, a run ending on a lone single), or joining the middle
    of a gas-filled long straight, keeps the network's id and gas without a refill.
  - A refill never takes a network over its weakest pipe (`gas_check.withheld`; the job ends `gas_lost` instead of
    bursting pipes), and gas a `remove_structure` plan deletes on purpose (`allow_contents`) is expected gone
    (`planned_loss_mol`) instead of being put back into the parts left.
  - The gas check and gas hold apply to `place_structure` / `remove_structure` only when they touch a pipe network.
  - `remove_redundant` never empties a network that holds contents and keeps in-line tanks and passive vents joined.
- **`remove_structure` and pipe networks.** An in-line tank or passive vent whose removal empties its network is
  `holds_gas`; one that splits a network holding gas or liquid is `contents_would_move` unless `allow_contents`, as
  `remove_pipes` refuses the split; one that squeezes the gas left past the weakest pipe is `would_burst`, naming
  the forecast pressure. A network the request takes pieces from is modelled in the job's own order.
- **The power overload guard.** `would_overload` priced every device output port as its input (the test was always
  false), so a fresh run from a battery or transformer output burnt cables. Ports are now priced by the game's power
  roles and on/off/error checks per device (a data port carries nothing), a network the edit removes whole keeps
  its load, and each newly joined port brings its device's own supply or demand. New `would_overload_when_on`
  warning: the same count with every device that is off switched on, at the game's ceilings, passed on through
  APCs, transformers and power transmitters up to 8 deep. The job is not refused for it.
- **`trader_sell` sold one stack for several lines.** Five lines of 1 iron paid 5 and destroyed 1 (the game leaves
  a destroyed stack in its slot until the end of the frame). Lines now add up against what earlier lines claimed and
  one entry sells in one game call; a sale that would take a thing twice is refused `sell_separately`. A card not
  held by a player or a vending machine is `card_not_usable`; `insufficient_available` counts the card holder's
  inventory; credits are rounded to the cent; a dry run's `credits_after` is the predicted balance.
- **Cables, pipes and chutes.**
  - A run end at a device port, standing on a piece or joining an open end no longer joins the piece straight ahead
    (it bridged an APC and a transformer).
  - `would_split` roots per fed network (a battery or APC input is no root of the network that feeds it); `root`
    must be a device on a touched network.
  - `remove_loops` no longer fails with `internal_error` on the game's runtime; `remove_redundant` lists every
    candidate it keeps with its reason.
  - Burnt cables can be removed (`remove_cables`, `plan_removal`, `remove_ids`); they refund nothing and are never
    built again by `undo_job`.
  - `undo_job` reuses the job's own `from_id` and refund choice, and removes the job's pieces inside the run that
    builds the old ones back, so undoing a tee on a trunk no longer cuts the network.
  - `plan_*_route` never ends in a blocked cell; `avoid_networks` takes network handles; `plan_chute_route` refuses
    ends that push or take items the wrong way and leaves a chute only by an end items leave by.
  - Upgrade and clean verification follow a renumbered network (`renumbered_from`); `upgrade_pipes` and
    `clean_pipes` see a queued `move_gas` (`atmosphere_busy`).
- **Structures.** Placements of one request are checked against each other; a piece that needs a frame may stand on
  a frame an earlier placement of the request puts there (`supported_by_placement`); `above_floor_m` never invents
  a floor; `has_mounted` for a device held only by the removed piece; a breach is reported once per opening;
  `crosses_section_seam` only for pieces that could fit one section, never for runs or in-line tanks; `find_spot`
  searches nearest first and says why spots were ruled out; `paste_blueprint` refuses a paste while another still
  places; the crosshair tools answer `no_camera` without a player camera.
- **Devices and chips.** `read_memory` / `write_memory` refuse a chipless housing (`no_programmable_chip`) and a
  range past the stack; `set_ic_source` stores IC10 as a save keeps it (CRLF and lone CR to LF, non-ASCII to `?`,
  with warnings naming every line over the editor's limits); `compile_error_line` / `compile_error_type`; plain-text
  `error_code`; a refused step no longer leaves the chip paused; `resolve_ic_selectors` counts over the holder's
  batch list (`reachable`, `unique`, `batch_device_count`, no `ic10_example` for an unreachable device);
  `paint`'s `previous_color` reads a display's shown colour; failed `*_logic_many` entries name what they asked for;
  a `logic_type` given as a number gets the range message; the gateway prefab has its footprint.
- **Air, solar and the rest.** A `move_gas` `transfer_id` being applied reads `queued`; a queued move with nothing to
  move fails `nothing_to_move`; a room refuses liquid it would freeze out or cannot boil (a 0.5 K margin over
  freezing); `water_sources` lists networks made while paused; `plant_genes` refuses a non-number gene before
  writing; burst pipes and burnt cables are `is_broken` / `broken` everywhere; a stored item's position is its
  outermost holder's. `dish_aim` turns the small dish and measures in double precision; `solar_aim` answers the
  pose nearer the panel's aim; dishes report `finished`, `powered`, `on` and `can_rotate`.

## 1.4.4

Fixes from the first live test of 1.4.3.

- **Meshes, not only cells.** A device's mesh can reach well past the small cells the game registers it in: the
  3x3 console "Coolant Monitor" registers 1 x 1 m and draws about 1.5 x 1.5 m. The mesh box now decides:
  - `visual_overlap` (`place_structure`) and `device_visual_overlap` (`lint_layout`): two mesh boxes running more
    than 0.1 m into each other, or one inside the other, clash. A small device placed under a console's overhang
    clashes; neighbours flush on one wall only touch, or overlap by a rim, and still do not.
  - `find_spot` drops spots whose mesh clashes with another thing's before any cursor check (with
    `no_visual_overlap`, the default), and reads `one_section` from the mesh: it no longer offers the spots over the
    console's frame.
  - Wall sections and seams (`crosses_section_seam`, `layout.sections`, `mount`, `device_crosses_seam`) come from the
    mesh box's rectangle; a rim 0.1 m or less past a seam does not count.
  - `wall_map` keys every cell a device's mesh covers, so the console shows as 3 x 3 and `free_rects` avoids it.
- **`undo_job` restores network pieces through their place tools.** Cable, pipe and chute pieces come back through
  `place_cables`, `place_pipes` and `place_chutes` (one call per tool and grade, each piece with the ends it had),
  so `would_bridge`, `would_split`, the burst and the gas guards apply; `place_structure` builds only the rest. A
  network piece no coil or kit lays is refused. New `allow_bridge` passes on to those calls.
- **`undo_job`'s dry run is ready only when every step is:** the removal's dry run, the piece runs' dry runs (checked
  as if the removal were done) and `place_structure`'s dry run when nothing is removed first. A real run makes the
  same checks first.
- **New `pieces` form** on `place_cables`, `place_pipes` and `place_chutes`: up to 256 separate pieces in one job,
  each as `piece` lays it.
- **`assume_removed` behind a queue:** a real run queued with `wait` behind another job is checked for things still
  standing when it starts, not when it is queued, so the job ahead may remove them; a run that is not queued is
  refused as before.
- **`bridging`** in a run's `networks_after` devices is true only for a port the edit joins to another port of its
  device; a port whose network does not change (a force field's two ports already on one network) is not.
- `wall_map`'s `top_left` is the world point on the plane (it read `z: 0`).
- `looking_at`'s `hit.face_plane` names the plane within 0.3 m of it, so a hit on a floor plate's top (0.13 m up)
  reads `y=...` instead of null.
- `lint_layout`: `pipe_along_door` is now `run_along_door` (it fires for cables and chutes too); `floating_run`
  leaves in-line tanks and passive vents alone (they are not runs).
- `move_gas`'s description says plainly that it is a cheat that bypasses the game's physics. Its behaviour is
  unchanged.

## 1.4.3

Placement and layout tools: doors and windows everywhere.

- **Doors keep their doorway.** A door's face (jambs, top edge, threshold) and a band either side of it inside the
  door's rectangle is its keep-out: new mod setting `[Layout] DoorKeepOutBand`, 0 to 2 m in 0.5 m steps, default
  0.5 m. Every door counts: doors, airlocks, blast and hangar doors, hatches, roll covers, robot arm doors, the
  Force-Field Door mod's 1x1, 2x1 and 4x2 doors (their faces from the door's own registered face points). A piece
  hidden inside the floor slab under a threshold is not in it, and a door's own port cells are released.
- **Route planners** (`plan_cable_route`, `plan_pipe_route`, `plan_chute_route`) never route through a keep-out
  unless `allow_door_keepout: true` (the route's own ends are released, with a note). A door's face no longer counts
  as a wall that holds pieces up, which drew routes onto door jambs and top edges.
- **Place tools** (`place_cables`, `place_pipes`, `place_chutes`, `place_structure`) refuse new pieces in a keep-out
  with `in_door_keepout` (`allow_door_keepout: true` makes it a warning).
- **Windows** (glass, composite, padded and shuttered windows, window shutters; not floor gratings): a run across a
  window's face costs the planners extra and warns `crosses_window`; so does a device standing on one. Never a
  refusal.
- **`looking_at` v2:** `view` (eye, forward/right/up, yaw and pitch, the world axes nearest your level forward and
  right, `ambiguous` near a diagonal, third person and seated), `hit` (the surface the look ray meets up to
  `max_distance_m`, default 10 m: point, normal, face, face plane, 2 m cell, small cell, support character, what was
  hit, the point in the target's frame), and for a structure `target.body` (render box, its offset from the origin,
  the small-grid footprint box) and `facing_me`.
- **`place_structure` layout preview:** each placement's dry run has `layout`: `footprint` (the small cells the game
  would register it in, the render and footprint boxes, the face plane it rests on and the rectangle it covers),
  `sections` (the 2 m wall sections it spans, `crosses_seam`), `conflicts` (`visual_overlap`,
  `crosses_section_seam`, `in_door_keepout`, `crosses_window`, `blocks_route_cells`, `front_blocked`,
  `faces_out_of_room`, `not_upright`) and `port_checks` (what stands in each port's joining cell, whether it joins on
  build and which network, flow direction, door keep-out). Warnings only, except the door keep-out.
- **Place by intent:** `place_structure`'s `orient` {`mount`, `upright`, `controls_toward`, `ports`, `flow`} tries
  every turn the cursor allows, scores each with the layout preview and uses the best; the reply echoes the choice,
  its reasons and the next three. A turbo volume pump is also scored with its flow reversed (`mode_flip`: write
  `Mode` 1).
- **Relative addressing:** `place_structure`'s `at` takes `{crosshair}`, `{relative_to: player|crosshair|id, frame
  player|world|target, right_m, up_m, forward_m, from origin|top|bottom|left|right|front|back}` and
  `{on_face_i_look_at, along_right_m, along_up_m}`; `facing` takes `toward_player`, `away_from_player`,
  `out_of_face`, `into_room`; `above_floor_m` sets the footprint's height over the floor. The reply echoes
  `resolved` (the point and facing, and how they were read); `ambiguous_axis` near a diagonal.
- **New `describe_prefab`:** a prefab in its own frame: placement, allowed turns, small cells and boxes from its
  origin, ports (joining cell offset, outward direction, flow), `visual_up` (with its source; a small table of live
  facts, guesses marked) and a Mode that reverses its flow.
- **New `wall_map`:** a text elevation of a wall plane as seen from one side (seams, W/G/D/F faces, devices by key,
  runs, door keep-out), its sections and things, and `free_rects` for a w x h rectangle.
- **New `find_spot`:** ranked spots for a prefab on a plane or a room's walls near a point: geometric filters first
  (`one_section`, `min_bottom_above_floor_m`, `avoid_doors`, `front_clear_m`), then capped cursor checks and the
  layout preview (`no_visual_overlap`, `ports_reachable`), each with ready `place_arguments`.
- **New `lint_layout`:** a room or box against the layout rules: `run_in_door_keepout`, `port_into_doorway`,
  `port_cell_foreign_network`, `floating_run`, `run_crosses_window`, `device_visual_overlap`,
  `mounted_faces_out_of_room`, `device_crosses_seam` (warnings), `pipe_along_door`, `controls_not_on_wall` (info).
- **New `undo_job`:** undoes a finished place or remove job: removes what it built and builds again what it removed,
  as it stood (a snapshot is taken when each job starts); refused when the world diverged. Dry run by default.
- **Print provenance:** items a machine makes are recorded as they are made (split stacks keep the record);
  `find_things` reports `made` and filters with `made_by` and `made_since`.
- **New `show_preview`:** timed wire boxes in your game for a placement's footprint, body and ports, or any cells and
  boxes; never the game's construction cursor, nothing built. 83 tools.
- **`grid_survey`** marks keep-out cells `x` and window cells `g` in `support`, names each face structure's `kind`
  (wall, window, door) and lists `doors` with their faces, plane, band and port cells.

## 1.4.2

Broken structures: find them, and remove them as the game does.

- **What broken means.** When a structure with a broken model (a vent, a pipe, many devices) reaches full damage, the
  game does not destroy it: it swaps in the broken model and heals the damage, so the wreck reads 0 damage and 100 %
  health, still takes its place, and cannot be repaired; the game only lets it be
  deconstructed, and that gives nothing back.
- **`remove_structure` `allow_broken`.** Removes broken structures as the game's deconstruction of a broken thing does:
  no refund, and the game's own deconstruct refusal is not asked (the game does not ask it there). Every other guard
  still applies (items or gas inside, a mounted device, a breach, network splits, the gas check). Without the flag a
  broken piece is refused as before, and the refusal now says to pass `allow_broken` instead of "repair it first",
  which the game cannot do.
- **`find_things`:** every thing reports `is_broken` and `condition` (`broken`, `damaged`, `intact`,
  `indestructible`, `none`); the new `broken` filter finds every wreck (`broken: true`) or leaves them out.
- **`thing_health`:** the scan now lists broken things (it missed them, since they read 0 damage), broken first;
  `broken_only: true` lists only them. Each thing reports `condition`, and structures `broken_build_state`,
  `custom_name` and the networks they are on (`networks`).
- `place_structure` onto the place of a broken structure stays refused (`cannot_place`), and the refusal now names the
  broken structure and how to remove it. `replace_walls` / `replace_frames` no longer say "repair it first" for one.

## 1.4.1

Fix: pipe jobs lost gas when one job merged pipe networks more than once.

- **The cause.** The game moves a merged network's gas to the survivor through a queued event, applied only at the
  start of the next game tick. A job holds the tick and builds a whole run in one frame, so a run that joined a
  gas-holding network and also joined a second network (a branch, an extra end, a loop closed) merged twice in one
  tick: the second merge copied a survivor that had not yet received the first merge's gas, and that gas then landed
  in a network with no pipes left. The same happened at an in-line tank's port: the tank network's gas ended up in a
  pipeless network its devices still read, while the tank's own network read 0 mol. Both jobs reported success.
- **The fix.** Every job that can change pipe networks (`place_pipes`, `remove_pipes`, `upgrade_pipes`,
  `clean_pipes`, `place_structure`, `remove_structure`) applies the game's queued gas changes right after each piece
  it builds, where the tick would apply them, so every merge and split sees the contents the one before left.
- **`gas_check`.** The same jobs read every pipe network before and after and compare each family of networks they
  changed, moles and energy: `gas_check {checked, ok, summary, families, ghosts, recovered, ghosts_cleared,
  old_ghosts}`. A family short of gas gets what it lacks put back (`recovered`) and pipeless networks left holding a
  copy are emptied and dropped (`ghosts_cleared`). If anything is still missing the job ends `gas_lost`, not
  `applied`, and every later pipe job is refused (`gas_check_failed`) until the world is loaded again.
- Removing a network's last pipes with `remove_structure` still deletes its contents as the game does (only with
  its `allow_contents`; `remove_pipes` has no such argument and refuses with `holds_contents`); the check reports
  that family as `emptied`, not as a loss.

## 1.4.0

Ingot Vault tools (needs the Ingot Vault mod, Workshop 3749011679; without it they answer
`ingot_vault_mod_required`).

- **`vault_contents`:** what each vault stores, from the vault's own store and exact to 1e-6: ingots as grams of their
  reagent (with the ingot a vend makes), ores and ices as counts; every Remote Vault and the vault it reaches.
- **`vault_deposit`:** ingots, ores and ices go straight into a vault's store from wherever they are (a player at any
  depth, a container, the ground), whole or part of a stack, with the vault's import bookkeeping (reagents times grams,
  or the ore count), the item destroyed as the import destroys it. By ids (`items`, `reference_ids`) or a
  `find_items`-style filter plus `kind` (ingot, ore, ice). The vault's own rule refuses anything else
  (`not_vault_material`). Reports each stock line before, change and after.
- **`vault_withdraw`:** an amount of one stored thing, taken off the store as the vault's vend takes it and made
  straight into a holder's slots (default the local player, `to_slot` auto: matching stacks first, then empty slots,
  never more than a full stack each). What does not fit is refused unless `allow_ground`.
- Both write tools are dry runs until `dry_run: false, confirm: true`, need the vault on and powered, host only, and
  take a Remote Vault id as the vault it reaches.
- **`move_item`** refuses an Ingot Vault's or Remote Vault's display slots (index 2 and up; `vault_display_slot`),
  and `to_slot: "auto"` skips them: an item put there was lost when the vault rebuilt its slots.

## 1.3.5

Layout helpers found missing during a live relayout.

- **Rotation readout.** `grid_survey` devices, `find_things` and `looking_at` for structures, and `connections` report
  `rotation {facing, up, euler {x, y, z}}` in the forms `place_structure` takes: `facing` (the front) with `up`, or
  `euler` as quarter turns for `rotation`. Either re-places a device as it stands; `facing` reversed turns it 180
  degrees. A piece off the grid's axes has `facing` and `up` null. `place_structure`'s own `orientation.euler` now
  gives the same quarter turns.
- **Port preview.** `place_structure`'s dry run lists, for a device (or an in-line tank, a passive vent), `ports`
  `[{index, at, toward, type, role, network_id}]` where they would land at the requested position and turn, in
  `grid_survey`'s shape (`network_id` null).
- **Route ends at in-line tanks and passive vents.** `plan_pipe_route` (and the other planners, for their kind) take
  `{reference_id}` of a pipe thing with its own ends that is neither a pipe piece nor a device as `from` or `to`: the
  cell beyond its free end, with the pipe there given an end toward it. `port` names the end when several are free;
  a joined or unknown end is refused with the free ends listed.
- **Reserved cells.** `plan_*_route` take `reserve_cells [[x, y, z], ...]` and `reserve_ports [{reference_id, port}]`:
  cells the search treats as blocked (a port's being the cell a piece joining it stands in), so one run cannot take
  another port's cell. A reserved cell that is one of the route's own ends is released; `notes` report both.
- **`move_gas` `dry_run: true`:** the same prediction after the same checks, nothing queued (`status: dry_run`,
  `transfer_id` null). Not with `from: "planet"`.
- **`label` on in-line tanks.** The pipe-size in-line tanks (class `InLineTank`) are refused with their own reason: the
  game's Labeller has no rename for that class, and StationGod keeps no names of its own. The big in-line tanks
  (`StructureInLineTank`) take a label as before; `find_things`' `labelable` already told them apart.

## 1.3.4

Rooms in move_gas.

- **`move_gas` takes a room as `from` or `to`:** `{"room_id": "<id>"}` (as `rooms` reports it) or
  `{"room_of": "<reference id>"}` (the room that thing is in). The room is every cell of it with air of its own. From a
  room, each named gas is taken from every cell in proportion to what the cell holds (`amount_mol` caps the room's
  total), with its share of heat, in one atmospherics tick; `gases` is required, so a room's air is never emptied by
  omission. Into a room, each gas is spread over the cells by volume, so no single cell spikes. The side reports
  `room {room_id, room_type, cell_count, cells_with_air, volume_l}` and the room's pressure before and after in
  `total`; room sides list no per-cell `members`. Every side now carries `room` (null for an atmosphere). The tank or
  network on the other side keeps every burst and matter check. New errors `room_not_found`, `not_in_room`.

## 1.3.3

Planet gas removal.

- **`move_gas from: "planet"`** with `delete: true` and named `gases` takes those gases out of the planet's own
  air, `PlanetaryAtmosphereSimulation._globalGasMix` (what Terraforming Reloaded reads through `GetGlobalGasMix`), and
  out of its liquid clouds, ice clouds and ice caps, under the game's `GlobalInteraction` lock on the atmospherics
  thread. Without `amount_mol` it repeats for 30 ticks, since outdoor cells hand a residual back. Needs Terraforming
  Reloaded (`terraforming_mod_required`): the stock game keeps the planet read-only. Refused without `gases`.

## 1.3.2

Landing pad atmospheres in the gas tools, and move_gas checks the receiving side by the game's matter rules.

- **Landing pads.** Every piece of a landing pad shares one atmosphere, the pad network's (the pad's gas storage
  tanks give it 500 L each). `atmosphere_contents` now reports it for any pad piece or the pad network's id
  (`source: landing_pad_network`), `find_things has_atmosphere` counts pad pieces, and `move_gas` takes a pad piece,
  the pad network or its atmosphere id as `from` or `to` (owner `kind: landing_pad_network`). Before, they were
  refused `no_atmosphere`. The pad's burst rating is the gas pipe rating, at every piece; its gas storage is damaged
  already at the rating. The pad holds liquids as liquids: its atmosphere never changes state.
- **The receiving side's matter rules.** `move_gas` is refused `would_burst` when the move would make the target
  worse by one of the game's own rules: liquid filling over 2% of a gas pipe network's volume (spread over the members
  joined for liquids), gas or liquid freezing in a network at the settled temperature, or the pressure once arriving
  liquid has boiled where the atmosphere changes state (each liquid turned into its gas, paying its latent heat).
  `force` still skips every check.
- **`total.after_boiling`** in `move_gas` replies: the pooled state once every liquid that would boil has, null when
  none would or the atmosphere never changes state.

## 1.3.1

Fixes from the first live run of 1.3.0 on a dedicated server.

- **`place_structure` places ordinary devices again.** Batteries, small transformers, passive vents, pipes, small
  tanks, radiators, gas tank storage, valves and portables connectors were refused as "a rocket part", because they
  may also be fitted in a rocket. Now only what the game places solely in a rocket (strictly internal pieces), the
  fuselage and the launch mount are refused, plus any placement into a rocket's cells. `remove_structure` had the same
  mistake: its `rocket` refusal now means a piece that is part of a rocket.
- **Walls back to back in one request.** Two plates on one face, one facing into each cell, were refused
  `overlaps_placement`. A face holds one wall per side, as in the game.
- **Devices aimed at a cell's centre stand on its floor.** A 0.5 m-grid device (a battery, a valve) given at a
  point inside a cell stayed in the air there and was refused "requires a Frame below". When the point as given cannot
  be built, the device is now set down on the surface behind it, as the placement cursor's ray lands on a surface: the
  floor plane below a standing device, the face at the back of a mounted one (a transformer facing up sits on the
  floor). A point as given that can be built is kept.
- **Positions in messages are in metres.** Problems and warnings printed cells ten times too large
  ("Cell (-13060, 2200, -7075)" for (-1306, 220, -707.5)).
- **`plan_removal` on a dedicated server.** With no local player and no `from_id` it reported a `no_local_player`
  problem; it only prices the refund, so that is now a warning.
- **Smaller fixes.** A tap's changed trunk piece is listed under `created_by_part` `joined`, not `run`; a plan lists
  the network `to` names once in `resolved_networks`, not as both `to` and `to.network_id`; two plates back to back
  removed together report their breach once.

## 1.3.0

Tools for refactoring a whole network, each replacing a step that had to be done by hand in a live cable refactor.

- **Splits name the devices cut off.** `would_split` (place, remove, `plan_removal`, `remove_structure`) now lists
  each network a split leaves with its devices (`components`), the devices that feed it (`root`: the request's
  `root`, else every supplier on it: an APC's, transformer's or battery's output, a generator, a solar panel) and
  `cut_off`, the devices no root reaches afterwards. The problem message names them too.
- **New clean operation `remove_redundant`** (`clean_cables`, `clean_pipes`; never by default): removes every piece
  no device needs, oldest first, keeping every remaining piece joined, so each device stays on the network with its
  root. `keep_ids` never go (for example a new run's `created_ids`); `only_ids` and `older_than_id` narrow the
  candidates; a piece joined to a device port never goes. It finds the loops `remove_loops` cannot: an old feed and
  a new drop meeting at a device's port piece. The report says why each candidate stays and which devices need it.
- **Tap check.** A run end that stops next to, or one free cell short of, a piece of another network warns
  `not_joined`. `join_to` names the network a run must end up on, `join_trunk: true` adds the missing tap
  (`tap_added`). The route planners set `join_to` from `to` and pass it on in `place_arguments`, so a saved plan that
  ends one cell short of its trunk is caught when it is built.
- **Network handles.** Every `network_id` (and `to: {network_id}`, `join_to`, `allow_bridge` entries) also takes the
  reference id of a piece or device on the network, or `{reference_id, port}` of a device port, resolved to the
  current id when the call runs. Replies list them in `resolved_networks`.
- **Busy job slot.** A real run that finds another job running answers status `busy` with `running_job_id` instead
  of a refusal; `wait: true` queues it (up to 8) and starts it once the slot is free, checked again from scratch.
- **Created ids.** A run job's log lists `created_ids` and `created_by_part` (run, each branch, joined neighbours).
- **Fix: `remove_structure`'s breach check.** It follows the game's air rule and judges the whole request at once.
  A wall plate on the face of a finished frame no longer counts as a breach (the frame still seals the face; 185
  false alarms on one base), and two plates back to back between rooms are flagged when both are removed together
  (38 missed before, each checked alone while the other still stood).

## 1.2.0

- **Plan as if old pieces were gone.** `assume_removed: [ids]` on `plan_cable_route`, `plan_pipe_route` and
  `plan_chute_route` plans through and beside pieces that are about to be removed: their cells are free and their
  links gone. The kind's own pieces among them go into the plan's `remove_ids`, so one job builds the new run and
  removes the old one, and the dry run's guards see the result. `route.assumed_removed.in_the_way` lists the pieces the
  new route needs gone. The place tools take `assume_removed` too, for a dry run of a run whose old pieces go in
  another job; a real run is refused (`assumed_present`) while any of them still stands.
- **Least visible routes.** `prefer: hidden` grades every cell by how much of a cable shows there: inside a frame
  costs 1, on a frame's surface 3, on a wall's plane 5, in air 9. Where `inside_frames` gives no route, this gives the
  least visible one. Every route now reports its new cells by class (`route.visibility`).
- **A trunk and its drops in one job.** `trunk: {waypoints}` instead of `to` lays that trunk as given and branches
  every start from it with junctions, so a bus that is not built yet can be planned, checked and built with all its
  drops at once.
- **16 starts.** A plan takes up to 16 starts (was 8), enough for a generator network's ports.
- **grid_survey:** a new support class `i` for cells inside a frame (`f` is now only a frame's face), and
  `network_visibility`: each network's cells by class with the floating ones listed. `include_refund` adds what
  removing each piece would give back.
- **New `feed_paths` tool.** From a root device such as an APC, the path to every device on its network and the rooms
  it crosses; devices fed through another room (daisy chains) and rooms fed at more than one place are flagged.
- **New `plan_removal` tool.** The dry run of a removal, refund and `would_split` included, as a read-only tool; it
  also takes a whole `network_id`. Plans report `removal_refund` for the pieces they remove. 74 tools now.
- **Fix: refunds no longer hit the player.** Every tool that gives materials back (`replace_walls`, `replace_frames`,
  `upgrade_*`, `clean_*`, `place_*`, `remove_*`, `remove_structure`) made the items at the player's position, so they
  were pushed out of the player's body and damaged the suit. They now go straight into the inventory: onto matching
  stacks anywhere in it first, then into empty slots that take them, and only what does not fit goes on the ground
  a metre in front of the player, at rest. Each part is reported as `merged`, `slot` or `ground`.

## 1.1.1

- **`inside_frames` accepts beam tops.** The route rule now judges a cell by every frame it sits in or on, the same
  way `frames_first` and `prefer: frame_edges` do, so the top of a frame beam and the outer faces of frames count as
  on the frame. It used to judge the top of a beam by the empty cell above and refuse it.
- **Reroute between an APC and its network.** `reroute: {between: [...]}` failed with `not_on_one_network` when an end
  was a device on several networks of the kind, such as an APC's input and output. It now uses the one network both
  ends share, and an end may name a device port as `{reference_id, port}`. When the ends share no network, or
  several (`ambiguous_port`), the error lists each end's ports and their networks.

## 1.1.0

- **Paste blueprints without a player.** With BlueprintMod loaded, the new `paste_blueprint` tool pastes a blueprint
  at a position and quarter turn you give, as the D.B.P.U. does, so pieces land on the grid. It works on a dedicated
  server, where the console's `bppaste` cannot (it needs a local player). It also reports how the paste went and
  undoes it. 72 tools now.
- **Choose the pipe name.** New setting `[Pipe] Name` (default `StationGodMCP`, or the environment variable
  `STATIONGODMCP_PIPE_NAME`) lets a second game or a test server on the same machine listen on its own pipe instead
  of racing the first for it. The sidecar takes the same variable when `--pipe` is not given, and `mod_info` now
  reports `pipe_name`. Restart the game after changing it.

## 1.0.0

- **First release.** An MCP server for Stationeers: an AI agent such as Claude Code or Codex reads and operates the
  running game through 71 tools.
- **Devices, logic and chips.** Find any device in the world, read and write logic values, slots and memory in bulk,
  record changes over time; read, write, compile, pause, step and restart IC10 programs (suit chips included) and,
  with StationeersLua, Lua chips on consoles, computers, tablets and visors; set IC Housing pins.
- **The base and the planet.** Rooms and their air, the planet's atmosphere and weather, plants and genes, damage,
  fire risk, water, food, vitals, the game clock and what the player is looking at.
- **Items, gas, solar and traders.** Find, count and move items, label and paint, move gas between canisters, tanks
  and pipe networks, aim solar panels and dishes, check landing pads, buy and sell with a landed trader.
- **Building.** Plan, lay, remove and reroute cable, pipe and chute runs; upgrade cable and pipe networks in place;
  clean up junctions, long straights, loops and dead ends; swap walls, windows and frames without opening a room;
  place and remove any kit-built structure. Each has a dry run, holds the game tick for the real run, charges and
  refunds as the game does, and checks the result.
- **Local and remote.** A named pipe for a local hosting game; authenticated TCP for dedicated servers.
