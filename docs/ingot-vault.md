# Ingot Vault

[Back to the README](../README.md)

With [Ingot Vault](https://steamcommunity.com/sharedfiles/filedetails/?id=3749011679) loaded, three tools read a vault's
store and move material in and out of it directly, as a move: what leaves one side is what the other gains. No import
slot, chute, door or vend queue is involved, so nothing waits in a queue, gets ejected or lands in front of the vault.

Ingot Vault is optional: the mod finds it by name at run time. Without it these tools answer `ingot_vault_mod_required`.
If an Ingot Vault update renames something they use, they answer `ingot_vault_changed` naming it, and change nothing.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `vault_contents` | What each vault stores, exactly: ingots in grams of their reagent, ores and ices as counts. Also every Remote Vault and the vault it reaches. | `vault_id` (optional) |
| `vault_deposit` | Put ingots, ores and ices into a vault's store from wherever they are: your inventory at any depth, a container, the ground. Whole items or part of a stack. | `vault_id`; `items: [{reference_id, quantity}]`, `reference_ids`, or a filter (`prefab_contains`, `location`, `within_id`, `kind`, ...) |
| `vault_withdraw` | Take an amount out of a vault's store, made straight into your inventory or another holder's slots. | `vault_id`, `prefab_name` or `reagent`, `quantity`, `to_id`, `to_slot` |

`vault_id` may be a Remote Vault: the tools then use the one vault on its data network, as the Remote Vault does.

## How the store is kept

The vault keeps ingots as reagents (1 g of reagent = 1 g of ingot) and ores and ices as a count per item type. The
tools use the vault's own bookkeeping:

- **Deposit** adds what the vault's import adds: an ingot's reagents times its grams, or an ore stack's count. The item
  is then destroyed, as the import destroys it; for part of a stack only that part is taken off it.
- **Withdraw** takes the store down as the vault's vend does (an ore type the vault then holds 0.01 or less of drops
  from its list) and makes the same amount as items: the ingot the vault vends for that reagent, or the ore itself.

The vault's totals, power use, screen, save and `vault_contents` agree at once. The vault takes only ingots and ores
(ices, slag and organics count as ores); anything else is refused with `not_vault_material`. Items sitting in a vault's
own import or export slot are refused (`in_vault_slot`): the vault is busy with them.

## Deposit

```json
{ "vault_id": "132000", "prefab_contains": "Ore", "location": "player", "kind": "ore" }
```

Every ore you carry, nearest first. Three ways to name the items: `items: [{reference_id, quantity}]` (up to 256,
`quantity` optional for part of a stack), `reference_ids: [...]` (whole items), or a filter as `find_items` takes it
plus `kind` (`ingot`, `ore` (not ice), `ice`, `any`) and `limit` (default 256). A filter leaves out what the vault does
not take and counts it as `skipped`.

The reply lists each item (`from` slot, `quantity`, `left_in_source`) or why it was refused, and `stock`: each stored
type the deposit touched with `before`, `change` and `after`.

## Withdraw

```json
{ "vault_id": "132000", "prefab_name": "ItemIronIngot", "quantity": 5 }
```

Five grams of iron ingot into your inventory. Name what to take with `prefab_name` (as `vault_contents` gives it),
`prefab_hash`, or `reagent` for ingots (`Iron`, `Steel`, ...). `quantity` is grams of ingot or a whole count of ore,
at most what the vault holds.

Where it goes: `to_id` (default you) and `to_slot`, a slot index or `auto` (the default). `auto` on a player tops up
matching stacks anywhere in your inventory first, then makes new stacks in empty slots that take the item, never more
than a full stack each; it never puts a new stack into your hands or suit slots unless you name one by index. On any
other holder it uses that holder's own slots. What does not fit is refused (`no_room`, nothing changed) unless
`allow_ground: true`, which puts the rest on the ground a metre in front of the holder.

The reply lists `placed` (`merged`, `slot` or `ground`, with the stack's id) and the `stock` line before and after.

## Good to know

- **Dry run by default.** Both write tools only report until called with `dry_run: false` and `confirm: true`.
- **The vault must be on and powered**, as for its own import and vend (`vault_unpowered` otherwise).
- **`move_item` refuses a vault's display slots.** A vault shows its store as extra slots past its import and export
  slots; an item moved into one would be lost when the slot goes (`vault_display_slot`). Use `vault_deposit`.
- **`find_items` and `item_totals`** list a vault's ingots only as machine stock of kind `processing` and do not see
  its ores; `vault_contents` is the reliable count.

## Multiplayer

Host only (`not_host` on a client). The store changes reach other players the way the vault's own import and vend
changes do.
