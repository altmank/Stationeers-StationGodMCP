def move(what: Literal['item'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # The item(s); for a vault or silo deposit, whole things by id.
    quantity: float = None,  # Slot move: how many off the stack, default the whole item. Out of a vault: grams of ingot or a count of ore or ice; out of a silo: an item count; required for both.
    to: str = None,  # The holder that gets them: a container, belt, suit, "player", a vault or a silo.
    slot: int | str = 'auto',  # A slot index of to, or "auto" (the default). Given, the move is a slot move even into a vault's or silo's own slots; force needs an index.
    merge: bool = True,  # Join a matching stack in the slot.
    force: bool = False,  # Into an empty slot by index even when hidden. Cheat.
    allow: list[Literal['in_use', 'ground']] = None,  # in_use: take a part its device is using; ground: what does not fit goes on the ground.
    moves: list = None,  # Up to 64 moves in order, each {id, to, slot, quantity, merge, allow}; slot auto when left out.
    source: str = None,  # A vault or silo to take from.
    prefab: str | int | list = None,  # Into a store: filter; out of a store: the item to take (whole name or hash).
    reagent: str = None,  # Out of a vault: the stored reagent (Iron, Steel).
    items: list | str = None,  # Store moves: lines {id, quantity} (deposit) or {prefab, reagent, quantity} (vault to vault: grams of ingot, a count of ore or ice; "all" moves every line; required vault to vault, where a top-level prefab and quantity become one line).
    name: str = None,  # Part of the shown name or label, any case.
    location: Literal['any', 'ground', 'player', 'stored'] = 'any',  # Into a store: filter.
    within: str = None,  # Into a store: only things inside this holder.
    near: str | list = None,  # "player" only.
    radius: float = None,  # Metres around near.
    category: Literal['any', 'ingot', 'ore', 'ice'] = 'any',  # Into a vault: filter.
    at_most: int = None,  # Into a store: move at most this many things, nearest first. (default vault 256, silo 64) (default vault 256, silo 64)
) -> dict:  # main list: r['results']; iterating or indexing it walks every page, in order
    """Items and kits (a kit such as Kit (Battery Large) is an item; a built structure is not) into a slot or the player's hands with the game's own moves; into or out of a vault or silo store.
    Which handler runs: with slot or moves, a slot move. Otherwise a vault or silo as to or source makes a store move; everything else is a slot move. The reply's first key route names it: slot, into_vault, out_of_vault, vault_to_vault, into_silo or out_of_silo.
    Filters (prefab parts, name, location, within, near, category) work for moves into a store; slot moves take ids.
    Out of a vault: sg.move('item', source='4191359', prefab='ItemIronIngot', quantity=500, to='player').
    Into a silo by filter: sg.move('item', to='5200100', prefab='*Ore*', location='player').
    Cheat when force: true: ask the user first.
    Reply, one of:
    (ItemMoved)
      route: str
      index: int
      ok: bool
      reference_id: str
      from: {id: str, slot: int} | None
      to: {id: str, slot: int}
      quantity_moved: int
      merged_into: str | None
      destination_reference_id: str
      warning: str | None
    (BatchResult)
      route: str
      results: list[{index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    (VaultDeposit)
      route: str
      vault: Thing
      via: Thing | None
      matched: int | None
      skipped: int
      truncated: bool
      items: {results: list[{index: int, ok: bool, reference_id: str, prefab_name: str | None, display_name: str | None, kind: str, from: {id: str,
          slot: int} | None, quantity: float, left_in_source: float} | {index: int, ok: bool, reference_id: str | None, prefab_name: str | None,
          error: Error} | {index: int, ok: bool, asked: str, error: Error} | {index: int, ok: bool, kind: str, prefab_name: str | None, display_name:
          str | None, reagent: str | None, requested: float | None, quantity: float, partial: bool, from: {before: float, after: float}, to: {before:
          float, after: float}} | {index: int, ok: bool, error: Error}], count: int, success_count: int, error_count: int}
      stock: list[{kind: str, prefab_name: str | None, display_name: str | None, reagent: str | None, before: float, change: float, after: float}]
    (VaultWithdraw)
      route: str
      vault: Thing
      via: Thing | None
      to: Thing
      quantity: float
      placed: list[{where: str, slot: {id: str, slot: int} | None, quantity: float, reference_id: str | None}]
      stock: list[{kind: str, prefab_name: str | None, display_name: str | None, reagent: str | None, before: float, change: float, after: float}]
    (VaultTransfer)
      route: str
      from_vault: Thing
      from_via: Thing | None
      to_vault: Thing
      to_via: Thing | None
      items: {results: list[{index: int, ok: bool, reference_id: str, prefab_name: str | None, display_name: str | None, kind: str, from: {id: str,
          slot: int} | None, quantity: float, left_in_source: float} | {index: int, ok: bool, reference_id: str | None, prefab_name: str | None,
          error: Error} | {index: int, ok: bool, asked: str, error: Error} | {index: int, ok: bool, kind: str, prefab_name: str | None, display_name:
          str | None, reagent: str | None, requested: float | None, quantity: float, partial: bool, from: {before: float, after: float}, to: {before:
          float, after: float}} | {index: int, ok: bool, error: Error}], count: int, success_count: int, error_count: int}
      partial_count: int
    (SiloDeposit)
      route: str
      silo: Thing
      on_off: bool
      powered: bool
      matched: int | None
      skipped: int
      truncated: bool
      items: {results: list[{index: int, ok: bool, reference_id: str, prefab_name: str | None, display_name: str | None, from: {id: str, slot: int} |
          None, quantity: float, left_in_source: float, children: int, entry: int} | {index: int, ok: bool, error: Error}], count: int, success_count:
          int, error_count: int}
      count: {before: int, after: int}
    (SiloWithdraw)
      route: str
      silo: Thing
      on_off: bool
      powered: bool
      to: Thing
      quantity: float
      taken: list[{index: int, quantity: float, left: float, whole: bool, children: int}]
      placed: list[{where: str, slot: {id: str, slot: int} | None, quantity: float, reference_id: str | None}]
      stock: {prefab_name: str | None, display_name: str | None, before: float, change: float, after: float}
      count: {before: int, after: int}
    Types: Error = {code: str, message: str, data: Any | None, see: {tool: str | None, topic: str, subtopic: str | None} | None}; Thing =
        {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.move('item', id='3311223', to='player')
    """
