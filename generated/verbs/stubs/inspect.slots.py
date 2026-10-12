def inspect(what: Literal['slots'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    slot: int = None,  # One slot index; left out, every slot.
) -> dict:  # main list: r['slots']; iterating or indexing it walks every page, in order
    """A device's slots: occupants, what each takes, slot logic values.
    A list of ids answers {results: [{index, ok, reference_id, ...the reply below} or {index, ok, reference_id, error}], count, success_count, error_count}; fields paths are results.<key>.
    Reply:
      gateway_id: str
      reference_id: str
      device: {reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str | None, slot_count: int,
          is_gateway: bool, worn_by: str | None, worn_slot: str | None}
      slots: list[{index: int, underlying_slot_index: int | None, display_name: str | None, string_key: str | None, slot_class: {id: int, name: str |
          None} | None, empty: bool, interactable: bool, locked: bool, swappable: bool, hides_occupant: bool, specific_prefab_hashes: list[int] |
          None, occupant: {reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str | None} | None,
          logic_values: list[{logic_slot_type: LogicType, value: float}], logic_value_count: int}]
      count: int
      slot_count: int
    Types: LogicType = {id: int, name: str | None}
    Example: sg.inspect('slots', id='1199811')
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
