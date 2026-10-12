def find(what: Literal['container'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    name: str = None,  # Part of the shown name or label, any case.
    near: str | list = None,  # "player" only.
    radius: float = None,  # Metres around near.
    limit: int = 12,  # The first n in order (entries); left out, every one.
    order: Literal['nearest', 'id'] = 'nearest',  # nearest (default) or id (stable pages).
) -> dict:  # main list: r['containers']; iterating or indexing it walks every page, in order
    """Outermost holders with at least one item: lockers, crates, machines, a backpack on the floor.
    Reply:
      containers: list[{reference_id: str, prefab_name: str | None, display_name: str | None, slot_count: int, used_slot_count: int, item_count: int,
          items: list[{prefab_name: str, quantity: float}], position: Vec3, distance_m: float | None}]
      count: int
      total: int
      offset: int
      limit: int
      has_more: bool
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('container', prefab='*Locker*', near='player', radius=15)
    """
