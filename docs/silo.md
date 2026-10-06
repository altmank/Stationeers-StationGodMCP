# SDB Silo

[Back to the README](../README.md)

An SDB Silo keeps what it imports as entries, not as things in the world: one entry per imported thing, with
everything that thing held (a backpack's contents, a canister's gas), at most 600 entries, exported front first. The
silo tools read that store and move things in and out of it directly, with the silo's own bookkeeping. No chute,
import or export slot or door is involved, so nothing lands on the ground, rolls away or goes down a chute.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `container_contents` | On a silo, after its own two slots, its store: each entry with its prefab, quantity, contents and place in the store. | `reference_id` (the silo), `entries_limit` (default 10), `entries_offset`, `prefab_contains`, `name_contains` |
| `find_items`, `item_totals` | Count what silos store with everything else, at `location: silo`. | as for any item |
| `silo_withdraw` | Take items of one prefab out of a silo, made straight into your inventory or another holder's slots. | `silo_id`, `prefab_name` or `prefab_hash`, `quantity`, `to_id`, `to_slot`, `allow_ground` |
| `silo_deposit` | Put things into a silo from wherever they are: your inventory at any depth, a container, the ground. Whole things or part of a stack. | `silo_id`; `items: [{reference_id, quantity}]`, `reference_ids`, or a filter (`prefab_contains`, `location`, `within_id`, ...) |

## Reading a silo

`container_contents {reference_id: "<silo>"}` adds `silo`: `count` and `capacity` (entries), `importing`,
`exporting`, `dispense_slot` (the entry an IC asked to dispense, -1 for none), `total`, `offset` and `entries`:

```json
{ "index": 0, "reference_id": null, "prefab_name": "ItemIronIngot", "display_name": "Iron Ingot",
  "quantity": 50.0, "max_quantity": 50.0, "children": 0, "contents": [], "rots_when_taken": false }
```

`index` 0 is the entry the silo exports next. `children` counts the things stored inside the entry's thing, at any
depth; `contents` sums them per prefab. `rots_when_taken` marks food: the silo's export spoils stored food (it comes out
rotten), and `silo_withdraw` does the same. An entry is not a thing in the world, so `reference_id` is always null and
`move_item` cannot take it; `move_item` given the id a stored thing had answers `thing_not_found` naming the silo and
`silo_withdraw`.

`find_items` and `item_totals` count every entry and every thing stored inside one, with `location: silo`,
`held_in` naming the silo (slot index -1) and `silo {entry, inside, movable: false}`; `item_totals` adds `silo` to
each row and `silo_entries` to the reply. `within_id` set to a silo narrows to its store.

## Withdraw

```json
{ "silo_id": "5001", "prefab_name": "ItemIronIngot", "quantity": 120 }
```

120 iron ingots into your inventory. `quantity` counts items: a stack counts its quantity, anything else 1. Entries
are taken front to back, the order the silo exports them.

- **Stacks** (ingots, ores, any stack holding nothing that does not spoil) are pooled: the amount is made as items
  into the holder, topping up matching stacks first and then new full stacks in empty slots, and the store goes down
  entry by entry, the last one keeping the rest.
- **Anything else** (a thing that is not a stack, a thing holding things, stored food) comes out whole, as the silo's
  export loads it: the thing itself with its own name, damage, gas and contents, into one empty slot of its own.
  Such an entry is never split: a quantity ending inside one is refused, and the message gives the quantities that
  do not split it.

Where it goes: `to_id` (default you) and `to_slot`, a slot index or `auto` (the default), as for `vault_withdraw`:
on a player, matching stacks anywhere in the inventory, then empty slots that take the item, never the hands or suit
slots unless named by index. What does not fit is refused (`no_room`, nothing changed) unless `allow_ground: true`
puts the rest on the ground a metre in front of the holder.

The reply lists `taken` (each entry, how much, what it keeps, `whole`), `placed` (`merged`, `slot` or `ground`, with
the item's id), `stock` (the prefab's total in the silo before and after) and `count` (entries before and after).

## Deposit

```json
{ "silo_id": "5001", "prefab_contains": "Ore", "location": "player" }
```

Every ore you carry, nearest first, each becoming one entry at the back of the store. Three ways to name the things:
`items: [{reference_id, quantity}]` (up to 256, `quantity` for part of a stack), `reference_ids: [...]` (whole
things), or a filter as `find_items` takes it plus `limit` (default 64, max 600). A thing is stored as the silo's
import stores it: its save data and everything inside it, then the thing and its contents are destroyed. The silo takes
items whose slot class fits its import slot; a filter leaves out the rest and counts them as `skipped`.

Refused per thing: something in a silo's own slot (`silo_busy`, the silo is importing or exporting it), in a vault's
slot, in a locked slot, a growing plant, a thing holding a player, and a thing named twice or inside another thing the
deposit takes. Past 600 entries (an import the silo is saving counts) each further thing is `silo_full`.

## Good to know

- **Dry run by default.** Both write tools only report until called with `dry_run: false` and `confirm: true`.
- **The silo must be built, on and powered**, as for its own import and export (`silo_unpowered` otherwise).
- **A dispense an IC asked for is kept.** `DispenseSlot` names an entry by its place in the store; a withdrawal that
  would take that entry or one before it is refused (`silo_busy`) until the silo has exported it.
- **The silo agrees at once.** Its count (logic `Quantity`, the tooltip), the contents stack an IC reads (rebuilt on
  the silo's next logic tick) and the save follow every move. A withdrawal that a game call stops part way leaves in
  the silo whatever it did not make.
- **Things come out at once.** The silo's export lets a thing's contents out one every 0.2 s; `silo_withdraw` loads
  them in the same call, into the same slots.
- Not yet tested in game: `container_contents` on a silo, `find_items`/`item_totals` with `location: silo`,
  `silo_withdraw` (pooled stacks, a whole backpack with contents, stored food, `allow_ground`, a DispenseSlot refusal),
  `silo_deposit` (whole things, part of a stack, a backpack with contents, `silo_full`), and a client watching both.

## Multiplayer

Host only: both write tools answer `not_host` on a client, and only the host keeps a silo's store, so on a client
`container_contents` shows the silo's count with `entries_known: false` and `find_items` lists no silo stock.
Everything reaches other players through the game's own sync, with no message of StationGod's: a thing made from an
entry is sent as the silo's export sends it (the game's new-thing list), a stack topped up and a deposited thing
destroyed reach clients the usual way, and the silo's count travels in its own network update. Players without the mod
see items appear in slots and the silo's count change.
