# SDB Silo

[Back to the README](../README.md)

An SDB Silo keeps what it imports as entries, not as things in the world: one entry per imported thing, with
everything that thing held (a backpack's contents, a canister's gas), at most 600 entries, exported front first. The
silo tools read that store and move things in and out of it directly, with the silo's own bookkeeping. No chute,
import or export slot or door is involved, so nothing lands on the ground, rolls away or goes down a chute.

## Tools

| Call | What it does | Main options |
| --- | --- | --- |
| `inspect contents` | On a silo, after its own two slots, its store: each entry with its prefab, quantity, contents and place in the store. | `id` (the silo), `limit` (default 10), `offset`, `prefab`, `name` |
| `find item` | Count what silos store with everything else, at `location: silo`. | as for any item |
| `move item` (out of a silo) | Take items of one prefab out of a silo, made straight into your inventory or another holder's slots. | `source` (the silo), `prefab`, `quantity`, `to`, `slot`, `allow: ["ground"]` |
| `move item` (into a silo) | Put things into a silo from wherever they are: your inventory at any depth, a container, the ground. Whole things or part of a stack. | `to` (the silo); `items: [{id, quantity}]`, `id`, or a filter (`prefab`, `location`, `within`, ...) |

## Reading a silo

`inspect contents {id: "<silo>"}` adds `silo`: `count` and `capacity` (entries), `importing`,
`exporting`, `dispense_slot` (the entry an IC asked to dispense, -1 for none), `total`, `offset` and `entries`:

```json
{ "index": 0, "reference_id": null, "prefab_name": "ItemIronIngot", "display_name": "Iron Ingot",
  "quantity": 50.0, "max_quantity": 50.0, "children": 0, "contents": [], "rots_when_taken": false }
```

`index` 0 is the entry the silo exports next. `children` counts the things stored inside the entry's thing, at any
depth; `contents` sums them per prefab. `rots_when_taken` marks food: the silo's export spoils stored food (it comes out
rotten), and a `move item` withdrawal does the same. An entry is not a thing in the world, so `reference_id` is
always null and `move item` cannot take it; a `move item` call naming the id a stored thing had answers
`thing_not_found` naming the silo and the `source` that takes it out.

`find item` counts every entry and every thing stored inside one, with `location: silo`,
`held_in` naming the silo (slot index -1) and `silo {entry, inside, movable: false}`; `find item` with
`group_by: "place"` adds `silo` to each row and `silo_entries` to the reply. `within` set to a silo narrows to its
store.

## Withdraw

```json
{ "source": "5001", "prefab": "ItemIronIngot", "quantity": 120 }
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

Where it goes: `to` (default you) and `slot`, a slot index or `auto` (the default), as for a vault withdrawal:
on a player, matching stacks anywhere in the inventory, then empty slots that take the item, never the hands or suit
slots unless named by index. What does not fit is refused (`no_room`, nothing changed) unless `allow: ["ground"]`
puts the rest on the ground a metre in front of the holder.

The reply lists `taken` (each entry, how much, what it keeps, `whole`), `placed` (`merged`, `slot` or `ground`, with
the item's id), `stock` (the prefab's total in the silo before and after) and `count` (entries before and after).

## Deposit

```json
{ "to": "5001", "prefab": "*Ore*", "location": "player" }
```

Every ore you carry, nearest first, each becoming one entry at the back of the store. Three ways to name the things:
`items: [{id, quantity}]` (up to 256, `quantity` for part of a stack), `id: [...]` (whole
things), or a filter as `find item` takes it plus `at_most` (default 64; the silo holds at most 600 entries). A thing
is stored as the silo's import stores it: its save data and everything inside it, then the thing and its contents are
destroyed. The silo takes
items whose slot class fits its import slot; a filter leaves out the rest, and parts a device is using, and counts
them as `skipped`.

Refused per thing: something in a silo's own slot (`silo_busy`, the silo is importing or exporting it), in a vault's
slot, in a locked slot, a growing plant, a part a device is using (`in_use`: a chip in its holder, a console's
motherboard, a running machine's filter, battery or canister; `allow: ["in_use"]` takes it, see `move item`), a thing
holding a player, and a thing named twice or inside another thing the deposit takes. Past 600 entries (an import the silo is saving counts) each further thing is `silo_full`.

## Good to know

- **Reports first, unless the call names its things by id.** A deposit whose things are typed ids (`id`) or `items`
  lines, with no filter, acts at once; a withdrawal of one prefab, and a deposit picked by a filter, only report until
  called with `confirm: true`. `confirm: false` always reports.
- **The silo must be built, on and powered**, as for its own import and export (`silo_unpowered` otherwise).
- **A dispense an IC asked for is kept.** `DispenseSlot` names an entry by its place in the store; a withdrawal that
  would take that entry or one before it is refused (`silo_busy`) until the silo has exported it.
- **The silo agrees at once.** Its count (logic `Quantity`, the tooltip), the contents stack an IC reads (rebuilt on
  the silo's next logic tick) and the save follow every move. A withdrawal that a game call stops part way leaves in
  the silo whatever it did not make.
- **Things come out at once.** The silo's export lets a thing's contents out one every 0.2 s; a `move item` withdrawal loads
  them in the same call, into the same slots.
- Not yet tested in game: `inspect contents` on a silo, `find item` with `location: silo`,
  a `move item` withdrawal (pooled stacks, a whole backpack with contents, stored food, `allow: ["ground"]`, a
  DispenseSlot refusal), a `move item` deposit (whole things, part of a stack, a backpack with contents, `silo_full`),
  and a client watching both.

## Multiplayer

Host only: both write tools answer `not_host` on a client, and only the host keeps a silo's store, so on a client
`inspect contents` shows the silo's count with `entries_known: false` and `find item` lists no silo stock.
Everything reaches other players through the game's own sync, with no message of StationGod's: a thing made from an
entry is sent as the silo's export sends it (the game's new-thing list), a stack topped up and a deposited thing
destroyed reach clients the usual way, and the silo's count travels in its own network update. Players without the mod
see items appear in slots and the silo's count change.
