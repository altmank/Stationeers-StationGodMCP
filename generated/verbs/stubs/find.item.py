def find(what: Literal['item'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    name: str = None,  # Part of the shown name or label, any case.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    location: Literal['any', 'ground', 'player', 'stored', 'machine_stock', 'silo'] = 'any',  # Where the item is.
    within: str = None,  # Only items inside this holder at any depth (and its machine stock or silo store).
    near: str | list = None,  # Centre: [x, y, z], "player", "looking_at" or a thing's id (its position); with radius.
    radius: float = None,  # Metres around near; at most 1000 around a point, no limit around "player".
    box: list = None,  # Two corners [[x, y, z], [x, y, z]].
    group_by: Literal['holder', 'prefab', 'place'] = None,  # holder: sum per holder and prefab; prefab: sum per prefab; place: per prefab split into ground, carried, stored, machine stock and silo, with top holders.
    holders: int = 1,  # group_by place: holders listed per prefab, most first.
    exclude_in_use: bool = False,  # Leave out parts a device is using (chips in holders, a running filter's filters).
    limit: int = None,  # The first n in order (items (or groups)); left out, every one. (default 10; group_by place 12)
    order: Literal['nearest', 'id'] = 'nearest',  # nearest (default) or id (stable pages).
) -> dict:  # main list: r['items']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Stackable items and kits with quantity and holders, anywhere: ground, carried, stored, machine stock, silos; a kit such as Kit (Battery Large) is an item to find and move, a built structure is not; group_by sums them.
    group_by place takes no order or exclude_in_use (the totals are not paged).
    Reply, one of:
    (FindItems)
      items: list[{prefab_name: str | None, display_name: str | None, reagent: str | None, quantity: float, entries: int, location: str, holder: Thing
          | None, position: Vec3 | None, distance_m: float | None} | {reference_id: str, prefab_name: str | None, display_name: str | None, quantity:
          float, max_quantity: float | None, location: str, carried_by: str | None, held_in: list[{reference_id: str, prefab_name: str | None,
          display_name: str | None, slot_index: int, slot_name: str | None}], position: Vec3, distance_m: float | None} | {prefab_name: str | None,
          display_name: str | None, reagent: str | None, quantity: float, entries: int, holders: int} | {prefab_name: str | None, display_name: str |
          None, quantity: float, max_quantity: float | None, held_in: list[{reference_id: str, prefab_name: str | None, display_name: str | None,
          slot_index: int, slot_name: str | None}], position: Vec3, distance_m: float | None, silo: {entry: int, inside: str | None}} | {prefab_name:
          str | None, display_name: str | None, quantity: float, held_in: list[{reference_id: str, prefab_name: str | None, display_name: str | None,
          slot_index: int, slot_name: str | None}], position: Vec3, distance_m: float | None, machine_stock: {reagent: str, reagent_name: str | None,
          kind: str}}]
      count: int
      total: int
      offset: int
      limit: int
      has_more: bool
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
      in_use_left_out: int | None
    (ItemTotals)
      totals: list[{prefab_name: str | None, display_name: str | None, items: int, quantity: float, on_ground: float, carried: float, stored: float,
          machine_stock: float, silo: float, reagent: str | None, top_holders: list[{reference_id: str, prefab_name: str | None, display_name: str |
          None, quantity: float, kind: str, position: Vec3}] | None}]
      prefab_count: int
      item_count: int
      machine_stock_entries: int
      silo_entries: int
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.find('item', prefab='ItemIronIngot', group_by='place')
    """
