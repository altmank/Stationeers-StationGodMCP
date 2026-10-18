# Items

[Back to the README](../README.md)

Find, count and move items anywhere in the world, see what any container holds, and rename or paint things. None of
these tools need a gateway.

## Tools

| Call | What it does | Main options |
| --- | --- | --- |
| `find item` | Items anywhere: on the ground, in lockers and machines, carried by players at any depth. Each with its quantity, location, chain of holders and distance. Also material loaded into machines as stock, and what SDB Silos store. | `prefab` (a whole name, a hash, a list, or parts with `*`), `name`, `location` (`ground`, `player`, `stored`, `machine_stock`, `silo`), `within`, `near` + `radius`, `box`, `limit`, `offset`, `order`, `group_by` (`holder`, `prefab`, `place`), `exclude_in_use` |
| `find item` (`group_by: "place"`) | Total quantity of each item type, split into on the ground, carried, stored, machine stock and silo, with the holder that holds the most (`holders` holders; one by default). | as `find item`, and `holders` (0 leaves the holders out) |
| `find thing` | Anything by name, not only items: tanks, canisters, crates, structures, devices, players, animals. Matches the Labeller name and the game's own name. A stack carries its `quantity`. | `name`, `prefab` (a whole name, a hash, a list, or parts with `*`), `category`, `runtime_type`, `labelled`, `broken`, `has_atmosphere`, `near` + `radius`, `box`, `made_by`, `made_since`, `location`, `order` |
| `find container` | Every outermost holder with at least one item in it, not carried, nearest first. A crate in a lander counts towards the lander. | `prefab`, `name`, `near` + `radius`, `order` |
| `inspect contents` | The slots of one thing and what is in them, nested. `player` is your whole inventory. Each occupied slot is `{slot, reference_id, prefab_name, quantity}`, with what it holds itself under `slots` (a backpack's items). Empty slots are left out at every depth; `empty_slots_omitted` counts the thing's own. `include: ["detail"]` gives each slot's name and class and its occupant whole (`slots[].occupant`), `include: ["empty"]` the same with the empty slots too (to find a free slot). Shape it with paths through the slots: `fields: ["slots.slot", "slots.prefab_name"]`, or `slots.slots.prefab_name` for what a backpack holds. | `id`, `depth` (default 3), `include: ["detail"]` or `["empty"]`, `prefab`, `name` (slots holding a match at any depth) |
| `find food` | Every food and drink in the world, with nutrition, hydration, food quality and time until it decays; packages counted by content. | none |
| `move item` | Move an item, or part of a stack, into a slot. | `id`, `quantity`, `to`, `slot`, `merge`, `allow: ["in_use"]`, `force` (cheat); or `moves: [...]` |
| `write label` | Rename things as the hand Labeller does. | `id`, `label`; or `labels: [...]` |
| `write paint` | Paint things as a spray can does, no paint used; or list the colours. | `id` with `color`, or `items: [{id, color}]` |

## Finding things

- `find item` answers "where is my iron"; `find item` with `group_by: "place"` answers "how much iron do I have".
  A prefab filter makes these lists cheap for the game: with `prefab` (a whole name, a hash, a list, or parts with
  `*`) the mod reads only the things of those prefabs from an index it keeps in step with the game's own lists,
  instead of every thing in the world.
  `prefab: ["ItemIronIngot", "ItemSteelIngot"]` counts several items in one call; each item's `prefab_name` tells
  them apart. Ingots loaded into a fabricator count as the ingots it would eject (`location: machine_stock`). Stock
  cannot be moved with `move item`: open the fabricator to eject it.
- `group_by` sums the matches instead of listing each with its holder chain and position: `holder` gives one entry per
  outermost holder and prefab (`quantity`, `entries`, `location`, `holder`, its `position` and `distance_m`; loose items
  of a prefab make one entry with no holder), `prefab` one per prefab (`quantity`, `entries`, `holders`), and `place`
  one per prefab split into ground, carried, stored, machine stock and silo, with its top `holders`. 360 stacks in
  a row of 12 lockers come to about 25 KB by holder and under 1 KB by prefab, against about 130 KB listed one by one.
  Groups come in the order of their first entry; `limit` and `offset` page the groups (`place` takes neither: its
  totals are not paged).
- `exclude_in_use: true` leaves out parts a device is using and what is inside them (the filters of a running
  filtration machine, chips in their housings; see *Moving items*), counted in `in_use_left_out`, so a search for
  spare filters lists the spares.
- `find thing` finds what `find item` does not: a tank labelled `T1` is found by `T1` and by `Portable Liquid Tank`.
- `find thing` pages with `limit` and `offset`; `order: "id"` keeps the pages still while the world changes, so a
  whole list can be paged without gaps. In a program or a script the whole list is walked to the end for you (`help
  {topic: "scripts"}`; architecture/hub.md, *Streams*).
  `box` keeps only things in a box (`category: "structure"` with a box gives the walls of one building). A
  rocket's part reports `rocket_state`: its position moves while the rocket flies. A paintable thing reports
  `color {index, name, is_default}` as `write paint` reads it; a structure short of its last build state reports
  `build_state {current, last, complete}` (`build finish` finishes it).
  `location` keeps things where they are: `ground` (loose), `player` (carried), `stored` (in any other slot), `built`
  (structures) or `world` (players, animals). For many hits, ask only the keys you need with `fields` (for example
  `["reference_id", "position"]`), or write the reply to a file with `output_file` (README, *Large replies*).
- Print provenance (1.4.3+): every item a fabricator, printer or other machine makes is recorded as it is made, and a
  stack split off a printed one keeps the record. `find thing` reports `made {maker_id, maker_prefab, maker_name,
  game_time_s, quantity, split_from}` and filters with `made_by` (a maker's id, or text in its name) and `made_since`
  (a game time, or negative seconds before now). The record lives in memory since the world was loaded (1.9.1+: leaving a world clears it).
  `runtime_type: "DynamicGasCanister"` finds every portable tank whatever its prefab or label; `has_atmosphere: true`
  every thing that holds gas; `labelled: true` every label in the world. Each result says whether the Labeller can
  rename it (`labelable`), whether the device tools take it (`is_device`) and whether `inspect atmosphere` has
  something for it; a structure also reports how it stands turned (`rotation`). Every result has `is_broken` and
  `condition` (`broken`, `damaged`, `intact`, `indestructible`, `none`); `broken: true` finds every wreck, such as
  fire-burnt vents, which read 100 % health, burst pipes, which read 0 damage, and burnt cables an overload left (see
  `inspect health`).
- `write label` renames what the hand Labeller renames. The pipe-size in-line tanks (`StructureInLineTankGas1x1` and the
  rest, insulated too) are not among them: the game has no rename for them, and StationGod keeps no names of its own,
  so `write label` refuses them with `not_labelable`. The big in-line tanks take a label. To name a small one, label a sign
  or a device beside it.
- Results are sorted nearest first and paged (`limit`, `offset`). Nearest first is measured at each call, so pages
  taken while you walk overlap and leave gaps; to page a whole list, pass `order: "id"` (also on
  `find container` and `find frame`).
- `within` must name something that exists: a mistyped id answers `thing_not_found`, not an empty result.
- `find container` lists only the outermost holder: a crate in the lander, or a box in a locker, is not listed on its
  own, and its items count towards the lander or locker. Look inside with `inspect contents` or
  `find item {within}`.

## Moving items

`move item` uses the game's own moves, as an inventory click does: slot to slot, never through the world, so ice and
other perishables are never loose in the air.

- `to` is the thing holding the slot (a locker, a belt, a suit, a player); `slot` is its index, as
  `inspect contents` gives it for any thing (`inspect slots` only takes devices), or `"auto"`: matching stacks
  topped up to their most first, then the rest into the first empty slot that takes the item. A move spread over
  several slots answers each in `filled` (`to`, the stack's `reference_id`, how many were `added`, and its
  `quantity_now` read back after the move). With no empty slot, the matching stacks must take all of it.
  Into a slot given by index, the whole moved stack must fit in the stack there.
- Only slots you could click in the game are used. A hidden slot (a cable coil's internal slot, a vending machine's
  store) is refused with `slot_refuses`: the game keeps it for itself, and a coil destroys whatever is in it when it is
  used up. An item already in a hidden slot can still be moved out, to rescue one put there by mistake. The same
  goes for a package's items and a vending machine's store, and nothing goes back into a hidden slot, so taking an
  item out of a package or a vending store cannot be undone.
- `force: true` is a cheat (ask first) for the slots the game itself fills: it puts one whole item into an empty slot,
  hidden or not, with the game's own move, as a wrench mounts a rocket payload in a payload bay. The slot must still
  be empty and unlocked and its class must take the item; it needs a slot index, and is not taken in a `moves` list.
  Nothing checks that the game uses what is put there.
- `quantity` takes that many off a stack; the rest stays. An item that is not a stack (a water packet, a canister)
  moves whole and counts as 1, whatever it holds. `merge` (default true) lets items join a matching stack.
- A grower's plant and fertiliser slots follow what you do by hand instead, hidden or not: a planter's slots and a
  hydroponics station's fertiliser slots are hidden in the inventory window, but you plant and fertilise them by hand.
- A seed or plant moved into a plant slot (a hydroponics tray, planter, station or device) is planted as you plant it
  by hand: one is used off the stack and a new plant grows in the slot with its genes. `quantity` must be 1 (or the
  stack hold one); an occupied plant slot is refused. A plant slot takes nothing else (`slot_refuses`): fertiliser
  you hold at a plant goes into the fertiliser slot, and `"auto"` puts it there.
- A grower's fertiliser slot takes only fertiliser, one at a time into an empty slot, as you add it by hand. Anything
  else is refused (`slot_refuses`); a seed or plant because the game would take it there for the tray's plant.
  `"auto"` never puts anything else there.
- A plant growing in a plant slot is never moved out (`planted`): by hand you only harvest its fruit or seeds, or
  clear it. A seed bag left in a plant slot can be moved out.
- A part a device is using is not taken out (`in_use`, naming the device, its label and the slot): a chip in an IC
  housing or any other chip holder and a console's motherboard always; a filtration machine's filter, an area power
  control's or battery-run machine's battery and a Stirling engine's or portable machine's canister while the device
  is switched on. Switch it off first, take a spare, or pass `allow: ["in_use"]`. Battery chargers, canister docks
  and the batteries, tanks and filters of carried gear are not guarded.
- If the game throws part way through a move but the slot holds the result, the move is reported done with the
  game's error in `warning`: do not repeat it.
- `moves` applies up to 64 moves in order, each with its own result.
- Refusals name the reason: `slot_refuses` (the game's slot rules, with its message; a crate or portable tank is
  refused because the game only drags those into a slot), `slot_occupied`, `stack_full`,
  `no_free_slot`, `planted`, `in_use`, `slot_locked`, `not_movable` (a structure) and others. A refused move changes nothing.

Put 50 iron ingots into a locker's first free slot, `move item`:

```json
{ "id": "160455", "quantity": 50, "to": "148870", "slot": "auto" }
```

## Labels and paint

- `write label` writes a name as the Labeller writes it: an empty name restores the game's name, names are cut to 200
  characters. Only things the Labeller can rename are accepted (portable things, every device, in-line tanks, trays,
  plants, IC chips, flags); pipes, cables, frames and ordinary items answer `not_labelable`. Up to 64 renames per call.
- `write paint` with no targets lists the colours. `color` is a name, an index, or `default` for the thing's own colour. Each
  result carries `previous_color`, so a later call can put it back. A thing whose colour is a state set through the
  `Color` logic type, such as the LED display, cannot be painted (`has_color_state`), as with a spray can; its
  `previous_color` is the state colour it shows. Lights paint. A thing with no colour has `index` and `name` null.
  Up to 256 `items` entries per call.
- Both sync to other players and are saved with the world.

## SDB Silo

`inspect contents` on an SDB Silo lists its store (entries are save data, not things, so `reference_id` is null),
`find item` counts it at `location: silo`, and `move item` moves things
straight out of and into it with the silo's own bookkeeping: see [silo.md](silo.md).

## Ingot Vault

With the Ingot Vault mod, `move item` moves ingots, ores and ices straight into and out of a
vault's store, and `inspect vault` reads it exactly: see [ingot-vault.md](ingot-vault.md).

## Multiplayer

`move item` works on the host only (`not_host` on a client). `write label` and `write paint` use the game's own calls,
which sync to every player.
