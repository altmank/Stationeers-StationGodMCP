# Items

[Back to the README](../README.md)

Find, count and move items anywhere in the world, see what any container holds, and rename or paint things. None of
these tools need a gateway.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `find_items` | Items anywhere: on the ground, in lockers and machines, carried by players at any depth. Each with its quantity, location, chain of holders and distance. Also material loaded into machines as stock. | `prefab_contains`, `name_contains`, `location`, `within_id`, `near_player_m`, `limit`, `offset` |
| `item_totals` | Total quantity of each item type, split into on the ground, carried, stored and machine stock, with the five holders that hold the most. | as `find_items` |
| `find_things` | Anything by name, not only items: tanks, canisters, crates, structures, devices, players, animals. Matches the Labeller name and the game's own name. | `name_contains`, `prefab_contains`, `kind`, `runtime_type`, `labelled_only`, `has_atmosphere`, `near_player_m` |
| `list_containers` | Every holder with at least one item in it, not carried, nearest first. | `prefab_contains`, `name_contains`, `near_player_m` |
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
  `runtime_type: "DynamicGasCanister"` finds every portable tank whatever its prefab or label; `has_atmosphere: true`
  every thing that holds gas; `labelled_only: true` every label in the world. Each result says whether the Labeller can
  rename it (`labelable`), whether the device tools take it (`is_device`) and whether `atmosphere_contents` has
  something for it; a structure also reports how it stands turned (`rotation`).
- `label` renames what the hand Labeller renames. The pipe-size in-line tanks (`StructureInLineTankGas1x1` and the
  rest, insulated too) are not among them: the game has no rename for them, and StationGod keeps no names of its own,
  so `label` refuses them with `not_labelable`. The big in-line tanks take a label. To name a small one, label a sign
  or a device beside it.
- Results are sorted nearest first and paged (`limit`, `offset`).

## Moving items

`move_item` uses the game's own moves, as an inventory click does: slot to slot, never through the world, so ice and
other perishables are never loose in the air.

- `to_id` is the thing holding the slot (a locker, a belt, a suit, a player); `to_slot` is its index, as
  `container_contents` and `inspect_slots` give it, or `"auto"`: a matching stack first, else the first empty slot that
  takes the item.
- `quantity` takes that many off a stack; the rest stays. `merge` (default true) lets items join a matching stack.
- `moves` applies up to 64 moves in order, each with its own result.
- Refusals name the reason: `slot_refuses` (the game's slot rules, with its message), `slot_occupied`, `stack_full`,
  `no_free_slot`, `slot_locked`, `not_movable` (a structure) and others. A refused move changes nothing.

Put 50 iron ingots into a locker's first free slot, `move_item`:

```json
{ "reference_id": "160455", "quantity": 50, "to_id": "148870", "to_slot": "auto" }
```

## Labels and paint

- `label` writes a name as the Labeller writes it: an empty name restores the game's name, names are cut to 200
  characters. Only things the Labeller can rename are accepted (portable things, every device, in-line tanks, trays,
  plants, IC chips, flags); pipes, cables, frames and ordinary items answer `not_labelable`. Up to 64 renames per call.
- `paint` with no targets lists the colours. `color` is a name, an index, or `default` for the thing's own colour. Each
  result carries `previous_color`, so a later call can put it back. Lights and other things whose colour is a state
  cannot be painted (`has_color_state`), as with a spray can. Up to 256 things per call.
- Both sync to other players and are saved with the world.

## Ingot Vault

With the Ingot Vault mod, `vault_deposit` and `vault_withdraw` move ingots, ores and ices straight into and out of a
vault's store, and `vault_contents` reads it exactly: see [ingot-vault.md](ingot-vault.md).

## Multiplayer

`move_item` works on the host only (`not_host` on a client). `label` and `paint` use the game's own calls, which sync
to every player.
