def read(what: Literal['snapshot'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Only these devices.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    name: str = None,  # Part of the shown name or label, any case.
    logic: list = None,  # Logic types; left out, every one.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    include: list[Literal['zero_ratios']] = None,  # zero_ratios: keep gas and liquid ratios reading 0.
    limit: int = 2,  # The first n in order (devices returned); left out, every one.
) -> dict:  # main list: r['devices']; iterating or indexing it walks every page, in order
    """Every readable logic value of matching devices, in one frame.
    Reply:
      gateway_id: str
      devices: list[{device: {reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str | None,
          slot_count: int, is_gateway: bool, worn_by: str | None, worn_slot: str | None}, logic_values: list[Any], logic_value_count: int,
          zero_ratios_omitted: int | None}]
      count: int
      matched_count: int
      truncated: bool
    Example: sg.read('snapshot', prefab='StructureBatteryLarge', limit=10)
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
