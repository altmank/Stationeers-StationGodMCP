def find(what: Literal['device'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    name: str = None,  # Part of the shown name or label, any case.
    limit: int = 25,  # The first n in order (entries); left out, every one.
) -> dict:  # main list: r['devices']; iterating or indexing it walks every page, in order
    """Devices with logic, in the world or on one gateway's data networks.
    Paging and prefab parts are applied by the verb layer to the handler's whole list (it walks every device either way).
    Reply:
      devices: list[{reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str | None, slot_count:
          int, is_gateway: bool, worn_by: str | None, worn_slot: str | None}]
      count: int
      total: int
      offset: int
      limit: int
      has_more: bool
      gateway_id: str
    Example: sg.find('device', prefab='*GasSensor*')
    """
