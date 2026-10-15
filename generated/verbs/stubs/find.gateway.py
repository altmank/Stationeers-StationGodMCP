def find(what: Literal['gateway'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:  # main list: r['gateways']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """The device scopes: 'world' and each StationGod Gateway with its status.
    No options.
    Reply:
      gateways: list[{gateway_id: str, display_name: str | None, prefab_name: str | None, available: bool, status: str, data_network_count: int,
          visible_device_count: int, worn_item_count: int}]
      count: int
      total: int
      offset: int
      has_more: bool
    Example: sg.find('gateway')
    """
