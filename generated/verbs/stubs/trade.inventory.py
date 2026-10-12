def trade(what: Literal['inventory'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One contact; left out, every contact.
    card: str = None,  # The card whose holder's goods count; default the player's.
) -> dict:  # main list: r['contacts']; iterating or indexing it walks every page, in order
    """What each contact buys and sells: prices, stock, conditions, and what you hold that meets them.
    Reply:
      contacts: list[{reference_id: str, trader: str | None, display_name: str | None, shuttle: str, pad_size_tiles: list[int], contacted: bool, buys:
          list[{name: str | None, prefab_name: str | None, credits_each: float, wanted: int, gas: bool, conditions: list[str | None], have: int,
          sellable: int | None}], sells: list[{name: str | None, prefab_name: str | None, credits_each: float, stock: int, gas: bool, details: str |
          None}]}]
    Example: sg.trade('inventory', id='7')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
