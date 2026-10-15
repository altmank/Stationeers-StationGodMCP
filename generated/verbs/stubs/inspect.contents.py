def inspect(what: Literal['contents'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # A holder's id or "player".
    depth: int = 3,  # Levels of nested slots.
    include: list[Literal['empty']] = None,  # empty: keep empty slots.
    prefab: str | int | list = None,  # Only slots holding this prefab (part with *) at any depth.
    name: str = None,  # Part of the shown name or label, any case.
    limit: int = 10,  # The first n in order (a silo's stored entries listed); left out, every one.
) -> dict:  # main list: r['slots']; iterating or indexing it walks every page, in order
    """Any holder's slots and what is in them, nested (a locker, machine, suit; 'player' for the player).
    A list of ids answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply below; fields name its keys.
    Iteration goes over slots; a silo's stored entries are not iterated.
    Reply:
      reference_id: str
      prefab_name: str | None
      display_name: str | None
      position: Vec3
      distance_m: float | None
      slots: list[{index: int, display_name: str | None, slot_class: str, occupant: {reference_id: str, prefab_name: str | None, display_name: str |
          None, quantity: float, max_quantity: float | None, slots: list[SlotView] | None, slots_not_shown: int | None} | None}]
      empty_slots_omitted: int | None
      silo: {count: int, entries_known: bool, importing: bool | None, exporting: bool | None, dispense_slot: int | None, total: int, offset: int,
          entries: list[{index: int, prefab_name: str | None, display_name: str | None, quantity: float, max_quantity: float | None, children: int,
          contents: list[{prefab_name: str | None, quantity: float}], rots_when_taken: bool}], cursor_id: str | None} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('contents', id='player')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
