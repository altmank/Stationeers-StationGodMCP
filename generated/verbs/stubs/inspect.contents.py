def inspect(what: Literal['contents'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # A holder's id or "player".
    depth: int = 3,  # Levels of nested slots.
    include: list[Literal['detail', 'empty']] = None,  # detail: each slot's name and class and its occupant whole; empty: the same, empty slots too.
    prefab: str | int | list = None,  # Only slots holding this prefab (part with *) at any depth.
    name: str = None,  # Part of the shown name or label, any case.
    limit: int = 10,  # The first n in order (a silo's stored entries listed); left out, every one.
) -> dict:  # main list: r['slots']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Any holder's slots and what is in them, nested (a locker, machine, suit; 'player' for the player).
    Each occupied slot is {slot, reference_id, prefab_name, quantity}, with its occupant's own occupied slots under slots; empty_slots_omitted counts the holder's own empty slots.
    A list of ids answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply below; fields name its keys.
    Iteration goes over slots; a silo's stored entries are not iterated.
    Reply, one of:
    (compact)
      reference_id: str
      prefab_name: str | None
      display_name: str | None
      position: Vec3
      distance_m: float | None
      slots: list[{slot: int, reference_id: str, prefab_name: str | None, quantity: float, slots: list[ContentSlot] | None, slots_not_shown: int |
          None}]
      empty_slots_omitted: int | None
      silo: {count: int, entries_known: bool, importing: bool | None, exporting: bool | None, dispense_slot: int | None, total: int, offset: int,
          entries: list[{index: int, prefab_name: str | None, display_name: str | None, quantity: float, max_quantity: float | None, children: int,
          contents: list[{prefab_name: str | None, quantity: float}], rots_when_taken: bool}], cursor_id: str | None} | None
    (include detail or empty)
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
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
