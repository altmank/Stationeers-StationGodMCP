# Clean-up and refactoring networks

[Back to the README](../README.md)

Tidy a cable, pipe or chute network in place, find out how a network reaches its devices, price a removal, and rebuild a
whole base's wiring without cutting anything off. Every tool that changes something works as described in
[building.md](building.md#how-every-building-tool-works): report first, a held-tick job, materials and refunds from
your inventory.

## Tools

| Tool | What it does |
| --- | --- |
| `swap cable`, `swap pipe` (`tidy`) | Tidy a network in place with one or more operations. |
| `swap chute` | Remove the chute pieces no item can pass through to a consumer, and turn junctions, overflows and splitters that lose a branch into straights or corners. |
| `plan removal` | What removing pieces would give back and whether it would split a network. Read only. |
| `survey feeds` | How a network reaches each device from a root device, and which rooms each feed passes through. Read only. |

## Cleaning a network

Name a whole network with `network` (its id, or any piece or device on it, or `{id, port}`), or pieces
with `id`; only the named pieces change. `tidy` picks what happens; give `into` (an upgrade) or `tidy`, not both, and
with `tidy` left out `into` defaults to the kind's upgrade (`heavy` for cable, `insulated` for pipe). The operations
always run in this order, each on what the earlier ones leave:

| Operation | What it does |
| --- | --- |
| `remove_dead_ends` | Removes stubs (a piece with one connected end) and isolated pieces, round after round, since removing a stub can make its neighbour one. Stops at a stub whose only connection is a device. |
| `remove_loops` | Finds loops, pieces joined to the rest in more than one way, and breaks each by removing the shortest run of plain two-ended pieces whose ends stay joined without it (a ring hanging off one junction is one such run and goes whole). `spare` spares every loop holding one of those pieces. |
| `remove_redundant` | Removes every piece no device needs (below). |
| `split_long_straights` | Each 3-, 5- or 10-long straight becomes single pieces in the same line, so later runs can join anywhere along it. Costs coils. |
| `merge_straights` | Runs of single straights of one grade, colour and owner in one line become the fewest long straights that cover them. Gives coils back. Not together with `split_long_straights`. |
| `simplify_junctions` | A junction with open ends becomes the piece with only its connected ends: a 3-way joining two neighbours becomes a straight or a corner. What `tidy: true` means. |

`remove_loops` and `remove_redundant` never run unless asked: a loop may be redundancy kept on purpose against a burnt
cable.

What always holds:

- The grade stays (normal, heavy, super heavy; gas, liquid, insulated), and so do colour and owner.
- Every connection stays, except those of removed pieces; no device loses a link. The run is refused otherwise.
- Never removed: a piece with a fuse, analyser, pipe meter or other device mounted on it, indestructible and rocket
  pieces (the tidy swaps leave rockets alone; `remove cable`, `remove pipe` and `remove chute` take rocket pieces
  as a player does). Such dead ends are listed in `dead_end_pieces` with `stopped_by`.
- Pipes: the contents stay in the network. A removed pipe takes only its volume, so the pressure rises; the run is
  refused if it would exceed the weakest remaining pipe. The last pipes of a network still holding gas or liquid are
  never removed (`stopped_by: holds_contents`). This holds per network, not per stub: a gas line with two open ends
  keeps every stub, since trimming it round by round would end in emptying it.
- Pipes: a split long straight's singles are built from its connected tip, so they join the network as it stands. If
  the game still renumbers a network during a run, the verification follows the survivor and its entry names the old
  id in `renumbered_from`.
- Each listed piece shows its `operation`, its `ends` and `connected_ends`, and its `cost` and refund.

Tidy a network, `swap cable`:

```json
{ "what": "cable", "network": "140977", "tidy": ["remove_dead_ends", "simplify_junctions"] }
```

## Cleaning a chute network

`swap chute` follows the items. Name a whole network with `network`, pieces with `id`, or every chute
in a box with `box: [[x, y, z], [x, y, z]]` (corners in metres, at most a 32 m cube). The tool judges the whole networks those pieces
are on; only the named pieces change.

**Which pieces are dead.** Items move one way through a chute: a straight or corner passes them on, a junction merges
its two inputs into its output, an overflow or splitter sends them to its outputs. A piece serves a path when an item
can reach it from a source and go on from it to a consumer.

- Sources: a device port that pushes items out (a sorter, stacker or machine output, a chute bin, an inlet), a rocket
  chute umbilical (two-way), and any piece an item rides in.
- Consumers: a device port that takes items in (a machine input, a chute export bin, an outlet, a vending machine) and
  a two-way umbilical port.
- An open end is not a consumer: what leaves through it falls on the floor.

Each removed piece gives `why`: `orphan` (joined to nothing), `no_consumer` (nothing it leads to takes items) or
`no_source` (it leads to a consumer, but nothing ever puts an item into it). Devices are never removed, and a valve
counts as open whatever its setting.

**What replaces a junction.** A junction, overflow or splitter on a path that is left with one way in and one way out
(because its other branch is dead, or already led nowhere) becomes the straight or corner with those two ends, in the
same run, keeping its paint. The run never leaves an end where items would fall out: a dead piece that a piece or chute
device staying behind would push items into stays (`stopped_by: would_drop_items`) unless that piece is the junction
being replaced. An overflow left open on purpose, to spill surplus on the floor, counts as dead; spare it with
`spare`.

**Items riding in chutes.** The game destroys an item together with the chute it rides in, so a piece holding an item
is never removed or replaced. `riding: "skip"` (the default) leaves that piece, and the dead pieces it would push the
item into, and runs the rest; `riding: "refuse"` refuses the whole run while any piece it would change holds an item.
The report's `riding` list names each such piece and what it carries. Let the item pass, or take it out with
`move item`, and run again.

It reports first; `confirm: true` runs it as a job. Kit (Chute) pays for replacements and takes
removed pieces back. Chute networks take new ids after any removal; the run checks the links around every change
instead.

```json
{ "what": "chute", "network": {"id": "171332"} }
```

## Removing what no device needs

`remove_redundant` reads the whole network with its devices and removes pieces, oldest (lowest reference id) first,
whenever every remaining piece stays joined without it. A piece that holds the network together is tried again once a
neighbour has gone, so a dead branch goes whole.

It finds what `remove_loops` cannot: devices never carry a network, so an old feed and a new drop that meet at a
device's port piece form an ordinary loop here. That is exactly what is left after building a new layout beside an old
one.

- **Never removed:** a piece joined to a device port, `spare` (for example a new run's `created_ids`), anything
  `remove_dead_ends` would keep.
- **Pipes:** an in-line tank or passive vent is part of the network and stays joined, so the pipes that join it stay.
  The last pipes of a network still holding gas or liquid stay too (`blocked:holds_contents`), counting what earlier
  operations of the same request remove; a network without devices is therefore left whole while it holds contents.
- **Narrow the candidates** with `only`, or `older_than` (only pieces built before that one).
- **The report** (`redundant`) lists what goes and every candidate that stays with its `reason`: `device_port`,
  `keep_ids` (the wire's word for `spare`), `blocked:...`, or `needed`, with the `devices` it still keeps connected to
  the root (`root`, default every supplier on the network). A `needed` piece means those devices have no other feed
  yet. A candidate that stays is in `kept_pieces` too, with the same reason, unless a later operation of the request
  changes it.

## Pricing a removal

`plan removal` prices what `remove cable`, `remove pipe` or `remove chute` would do (`kind`: `cable` default, `pipe`,
`chute`) under a read-only name. It takes `id`, `waypoints`, `cells`, or a whole `network`, and returns
the refund (`materials.refund`), `would_split` and the networks before and after. It never changes anything. On a
dedicated server with no `materials` and `refund_to: "source"`, `no_local_player` is only a warning here: the refund is
still priced. `refund_to` and the report's `materials.refund_plan` work as in
[building.md](building.md#how-every-building-tool-works).

## Which devices a split cuts off

Every removal (`plan removal`, a build with `remove_ids`, `remove structure`) reports a split in
`would_split`:

- `components`: each network the split leaves, with its devices and whether it `holds_root`;
- `root`: the devices that feed the network (the request's `root`, else every supplier on it: an APC's, transformer's
  or battery's output, a generator, a solar panel, judged by what the device is, not what it supplies this tick);
- `cut_off`: the devices no root reaches afterwards (`null` when no root is on the network).

The problem message names them too, so the devices to re-feed need no tracing.

## Tracing feeds

`survey feeds {id: "<APC>", network: "<its output network>"}` walks the network from the root device (`kind`: cable
default, pipe or chute; `port` instead of `network` when the root is on several networks of the kind). For each
device it gives the pieces between it and the root and the rooms those pieces pass through in order, and `through`,
the rooms that are neither the root's nor the device's own. A device with a `through` room is fed through another room
(`daisy_chains` counts them). Each room lists where its feeds enter (`entries`); more than one is `multiple_feeds`.
`unreached` lists devices on the network that no run from the root reaches. Pieces in no room, inside a floor frame or
outdoors, are skipped.

## Refactoring a whole network

A way to rebuild a base's wiring, for example into one hidden trunk with one branch per room, without any device
losing power. The same steps work for pipes.

1. **Survey.** `find device` for the power devices, `inspect connections` on each for its ports and networks,
   `survey feeds` from each APC for how rooms are fed today, `survey area` for the frames the new runs can hide in.
2. **Name networks by a device, never by a stored id.** Ids change after almost every edit. Use
   `{id: "<APC>", port: <output port>}` wherever a network is asked for; `resolved_networks` shows the id it
   meant this time.
3. **Plan the trunk,** then plan the drops onto it: `plan route` with `trunk` for a trunk not built yet, or
   `end: {network}` for one that is. Give a device with several ports all of them in one plan. Use `style: "hidden"`
   or `style: "inside_frames"`, and check `route.air_cells`.
4. **Pay from a belt.** Pass `materials` of a belt or locker holding coils: each job takes from it and puts
   its refund back, so removing an old chunk funds the next new one.
5. **Build new beside old.** New drops join the existing port pieces, so each network briefly has a second path
   (`would_loop` is expected) and nothing loses power. Keep each job's `log.created_ids`.
6. **Let the tap check catch short runs.** The planners pass `join_to` to the build tool, which warns `not_joined` when
   a run stops one cell short of the trunk; `join_trunk: true` adds the missing piece. In bus mode give `join_to`
   yourself. Read `networks_after` before each real run.
7. **Remove the old layout with `remove_redundant`**, not piece by piece:

   ```json
   { "what": "cable", "network": { "id": "<APC>", "port": 1 }, "tidy": ["remove_redundant", "simplify_junctions"],
     "spare": ["<created_ids of the new runs>"], "older_than": "<first new piece id>" }
   ```

   In the report, `needed` pieces name the devices still fed only through old cable: give those a drop first.
8. **Re-feed what a removal would cut off** with one job: plan its drop with `assume_removed` set to the old pieces and
   build it with the plan's `remove_ids`. Devices listed together in one `cut_off` share an old feed: give them one
   multi-start plan, not one drop each.
9. **Re-plan rather than force a stale plan.** A plan saved before new cable was built beside it answers
   `would_bridge`, `nothing_to_do` or `no_route`; plan again.
10. **Finish** with `remove_dead_ends` and `simplify_junctions`, optionally `merge_straights` (which gives coils back),
    then `survey feeds` (`daisy_chains` 0, `unreached` empty) and `survey network` on each network (not overloaded).

Where every route between crowded devices would join two networks (`would_bridge` on every plan), leave those networks
as they are rather than merge them.
