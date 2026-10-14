# Ingot Vault

[Back to the README](../README.md)

With [Ingot Vault](https://steamcommunity.com/sharedfiles/filedetails/?id=3749011679) loaded, four calls read a vault's
store and move material in and out of it, or from one vault to another, directly, as a move: what leaves one side is what the other gains. No import
slot, chute, door or vend queue is involved, so nothing waits in a queue, gets ejected or lands in front of the vault.

Ingot Vault is optional: the mod finds it by name at run time. Without it these calls answer `ingot_vault_mod_required`.
If an Ingot Vault update renames something they use, they answer `ingot_vault_changed` naming it, and change nothing.

## Tools

| Call | What it does | Main options |
| --- | --- | --- |
| `inspect vault` | What each vault stores, exactly: ingots in grams of their reagent, ores and ices as counts. Also every Remote Vault and the vault it reaches. | `id` (optional) |
| `move item` (into a vault) | Put ingots, ores and ices into a vault's store from wherever they are: your inventory at any depth, a container, the ground. Whole items or part of a stack. | `to` (the vault); `items: [{id, quantity}]`, `id`, or a filter (`prefab`, `location`, `within`, `category`, ...) |
| `move item` (out of a vault) | Take an amount out of a vault's store, made straight into your inventory or another holder's slots. | `source` (the vault), `prefab` or `reagent`, `quantity`, `to`, `slot` |
| `move item` (vault to vault) | Move stored ingots, ores and ices from one vault's store straight into another's. No item is made. | `source`, `to`; `items: [{prefab or reagent, quantity}]` (default everything) |

The `id` of `inspect vault`, and the `source` and `to` of a move, may be a Remote Vault: the calls then use the one vault
on its data network, as the Remote Vault does.

## How the store is kept

The vault keeps ingots as reagents (1 g of reagent = 1 g of ingot) and ores and ices as a count per item type. The
tools use the vault's own bookkeeping:

- **Deposit** adds what the vault's import adds: an ingot's reagents times its grams, or an ore stack's count. The item
  is then destroyed, as the import destroys it; for part of a stack only that part is taken off it.
- **Withdraw** takes the store down as the vault's vend does (an ore type the vault then holds 0.01 or less of drops
  from its list) and makes the same amount as items: the ingot the vault vends for that reagent, or the ore itself.

The vault's totals, power use, screen, save and `inspect vault` agree at once. The vault takes only ingots and ores
(ices, slag and organics count as ores); anything else is refused with `not_vault_material`. Items sitting in a vault's
own import or export slot are refused (`in_vault_slot`): the vault is busy with them.

## Deposit

```json
{ "to": "132000", "prefab": "*Ore*", "location": "player", "category": "ore" }
```

Every ore you carry, nearest first. Three ways to name the items: `items: [{id, quantity}]` (up to 256,
`quantity` optional for part of a stack), `id: [...]` (whole items), or a filter as `find item` takes it
plus `category` (`ingot`, `ore` (not ice), `ice`, `any`) and `at_most` (default 256). A filter leaves out what the vault does
not take and counts it as `skipped`.

The reply lists each item (`from` slot, `quantity`, `left_in_source`) or why it was refused, and `stock`: each stored
type the deposit touched with `before`, `change` and `after`.

## Withdraw

```json
{ "source": "132000", "prefab": "ItemIronIngot", "quantity": 5 }
```

Five grams of iron ingot into your inventory. Name what to take with `prefab` (as `inspect vault` gives it),
or `reagent` for ingots (`Iron`, `Steel`, ...). `quantity` is grams of ingot or a whole count of ore,
at most what the vault holds.

Where it goes: `to` (default you) and `slot`, a slot index or `auto` (the default). `auto` on a player tops up
matching stacks anywhere in your inventory first, then makes new stacks in empty slots that take the item, never more
than a full stack each; it never puts a new stack into your hands or suit slots unless you name one by index. On any
other holder it uses that holder's own slots. What does not fit is refused (`no_room`, nothing changed) unless
`allow: ["ground"]`, which puts the rest on the ground a metre in front of the holder.

The reply lists `placed` (`merged`, `slot` or `ground`, with the stack's id) and the `stock` line before and after.

## Transfer

```json
{ "source": "132000", "to": "140000", "items": [{ "prefab": "ItemIce", "quantity": 200 }] }
```

Two hundred ice from one vault's store into another's. Leave `items` out to move everything the first vault holds.
Each entry names a line by `prefab` or `reagent` (ingots), with `quantity` in grams of ingot or a
whole count of ore or ice (default all of it). Asking for more than the source holds moves what it holds and marks the
entry `partial`. A vault has no capacity limit, so the target takes everything.

Each line comes off the source as a vend takes it and goes onto the target as an import adds it. No item is made, and
nothing passes a slot, chute, locker or room on the way, so ice cannot melt into the air between the vaults. The tool
skips carrying the material from one vault to the other, as deposit and withdraw skip carrying it to the vault, but it
creates nothing.

The reply lists each entry with `quantity`, `partial`, and both vaults' amounts: `from {before, after}` and
`to {before, after}`. An entry the source does not hold is refused (`not_in_vault`), as is part of an ore or ice
(whole counts only). Both ids reaching one vault is refused (`same_vault`).

## Good to know

- **Reports first, unless the call names its things by id.** A deposit of typed ids (`id`) or `items` lines, with no
  filter, and a vault-to-vault move with `items`, act at once; a withdrawal, a deposit picked by a filter, and a
  vault-to-vault move of everything only report until called with `confirm: true`. `confirm: false` always reports.
- **The vault must be on and powered**, as for its own import and vend (`vault_unpowered` otherwise); a transfer needs
  both vaults on and powered.
- **`move item` refuses a vault's display slots.** A vault shows its store as extra slots past its import and export
  slots; an item moved into one would be lost when the slot goes (`vault_display_slot`). Use a deposit: `move item`
  with the vault as `to`.
- **Space ice keeps no gas in a vault.** The vault stores ice as a count and drops what gas the ice held; a transfer
  moves the count and changes nothing about that.
- **`find item`** lists a vault's ingots only as machine stock of kind `processing` and does not see
  its ores; `inspect vault` is the reliable count.

## Multiplayer

Host only (`not_host` on a client). The store changes reach other players the way the vault's own import and vend
changes do.
