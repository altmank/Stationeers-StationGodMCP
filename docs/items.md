# Items

[Back to the README](../README.md)

Find, count and move items anywhere in the world, see what any container holds, and rename or paint things. None of
these tools need a gateway.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `find_items` | Items anywhere: on the ground, in lockers and machines, carried by players at any depth. Each with its quantity, location, chain of holders and distance. Also material loaded into machines as stock. | `prefab_contains`, `name_contains`, `location`, `within_id`, `near_player_m`, `limit`, `offset` |
| `item_totals` | Total quantity of each item type, split into on the ground, carried, stored and machine stock, with the holders that hold the most (five by default). | as `find_items`, and `holders_limit` (0 leaves the holders out) |
| `find_things` | Anything by name, not only items: tanks, canisters, crates, structures, devices, players, animals. Matches the Labeller name and the game's own name. | `name_contains`, `prefab_contains`, `kind`, `runtime_type`, `labelled_only`, `broken`, `has_atmosphere`, `near_player_m`, `made_by`, `made_since`, `location` |
| `list_containers` | Every outermost holder with at least one item in it, not carried, nearest first. A crate in a lander counts towards the lander. | `prefab_contains`, `name_contains`, `near_player_m` |
| `container_contents` | The slots of one thing and what is in them, nested. `player` is your whole inventory. | `reference_id`, `depth` (default 3) |
| `consumables` | Every food and drink in the world, with nutrition, hydration, food quality and time until it decays; packages counted by content. | none |
| `move_item` | Move an item, or part of a stack, into a slot. | `reference_id`, `quantity`, `to_id`, `to_slot`, `merge`; or `moves: [...]` |
| `label` | Rename things as the hand Labeller does. | `reference_id`, `name`; or `labels: [...]` |
| `paint` | Paint things as a spray can does, no paint used; or list the colours. | `reference_ids` with `color`, or `items: [{reference_id, color}]` |

## Finding things

- `find_items` answers "where is my iron"; `item_totals` answers "how much iron do I have". Ingots loaded into a
  fabricator count as the ingots it would eject (`location: machine_stock`). Stock cannot be moved with `move_item`:
  open the fabricator to eject it.
- `find_things` finds what `find_items` does not: a tank labelled `T1` is found by `T1` and by `Portable Liquid Tank`.
  `location` keeps things where they are: `ground` (loose), `player` (carried), `stored` (in any other slot), `built`
  (structures) or `world` (players, animals). For many hits, ask only the keys you need with `fields` (for example
  `["reference_id", "position"]`), or write the reply to a file with `output_file` (README, *Large replies*).
- Print provenance (1.4.3+): every item a fabricator, printer or other machine makes is recorded as it is made, and a
  stack split off a printed one keeps the record. `find_things` reports `made {maker_id, maker_prefab, maker_name,
  game_time_s, quantity, split_from}` and filters with `made_by` (a maker's id, or text in its name) and `made_since`
  (a game time, or negative seconds before now). The record lives in memory since the game started.
  `runtime_type: "DynamicGasCanister"` finds every portable tank whatever its prefab or label; `has_atmosphere: true`
  every thing that holds gas; `labelled_only: true` every label in the world. Each result says whether the Labeller can
  rename it (`labelable`), whether the device tools take it (`is_device`) and whether `atmosphere_contents` has
  something for it; a structure also reports how it stands turned (`rotation`). Every result has `is_broken` and
  `condition` (`broken`, `damaged`, `intact`, `indestructible`, `none`); `broken: true` finds every wreck, such as
  fire-burnt vents, which read 100 % health, burst pipes, which read 0 damage, and burnt cables an overload left (see
  `thing_health`).
- `label` renames what the hand Labeller renames. The pipe-size in-line tanks (`StructureInLineTankGas1x1` and the
  rest, insulated too) are not among them: the game has no rename for them, and StationGod keeps no names of its own,
  so `label` refuses them with `not_labelable`. The big in-line tanks take a label. To name a small one, label a sign
  or a device beside it.
- Results are sorted nearest first and paged (`limit`, `offset`).
- `within_id` must name something that exists: a mistyped id answers `thing_not_found`, not an empty result.
- `list_containers` lists only the outermost holder: a crate in the lander, or a box in a locker, is not listed on its
  own, and its items count towards the lander or locker. Look inside with `container_contents` or
  `find_items {within_id}`.

## Moving items

`move_item` uses the game's own moves, as an inventory click does: slot to slot, never through the world, so ice and
other perishables are never loose in the air.

- `to_id` is the thing holding the slot (a locker, a belt, a suit, a player); `to_slot` is its index, as
  `container_contents` gives it for any thing (`inspect_slots` only takes devices), or `"auto"`: a matching stack
  first, else the first empty slot that takes the item.
- Only slots you could click in the game are used. A hidden slot (a cable coil's internal slot, a vending machine's
  store) is refused with `slot_refuses`: the game keeps it for itself, and a coil destroys whatever is in it when it is
  used up. An item already in a hidden slot can still be moved out, to rescue one put there by mistake. The same
  goes for a package's items and a vending machine's store, and nothing goes back into a hidden slot, so taking an
  item out of a package or a vending store cannot be undone.
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
- If the game throws part way through a move but the slot holds the result, the move is reported done with the
  game's error in `warning`: do not repeat it.
- `moves` applies up to 64 moves in order, each with its own result.
- Refusals name the reason: `slot_refuses` (the game's slot rules, with its message; a crate or portable tank is
  refused because the game only drags those into a slot), `slot_occupied`, `stack_full`,
  `no_free_slot`, `planted`, `slot_locked`, `not_movable` (a structure) and others. A refused move changes nothing.

Put 50 iron ingots into a locker's first free slot, `move_item`:

```json
{ "reference_id": "160455", "quantity": 50, "to_id": "148870", "to_slot": "auto" }
```

## Labels and paint

- `label` writes a name as the Labeller writes it: an empty name restores the game's name, names are cut to 200
  characters. Only things the Labeller can rename are accepted (portable things, every device, in-line tanks, trays,
  plants, IC chips, flags); pipes, cables, frames and ordinary items answer `not_labelable`. Up to 64 renames per call.
- `paint` with no targets lists the colours. `color` is a name, an index, or `default` for the thing's own colour. Each
  result carries `previous_color`, so a later call can put it back. A thing whose colour is a state set through the
  `Color` logic type, such as the LED display, cannot be painted (`has_color_state`), as with a spray can; its
  `previous_color` is the state colour it shows. Lights paint. A thing with no colour has `index` and `name` null.
  Up to 256 things per call.
- Both sync to other players and are saved with the world.

## Ingot Vault

With the Ingot Vault mod, `vault_deposit` and `vault_withdraw` move ingots, ores and ices straight into and out of a
vault's store, and `vault_contents` reads it exactly: see [ingot-vault.md](ingot-vault.md).

## Multiplayer

`move_item` works on the host only (`not_host` on a client). `label` and `paint` use the game's own calls, which sync
to every player.
