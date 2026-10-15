def trade(what: Literal['inventory'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One contact; left out, every contact.
    card: str = None,  # The card whose holder's goods count; default the player's.
) -> dict:  # main list: r['contacts']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """What each contact buys and sells: prices, stock, conditions, and what you hold that meets them.
    Reply, one of:
    (Inventory)
      contacts: list[{reference_id: str, trader: str | None, display_name: str | None, shuttle: str, pad_size_tiles: list[int], contacted: bool, buys:
          list[{name: str | None, prefab_name: str | None, credits_each: float, wanted: int, gas: bool, conditions: list[str | None], have: int,
          sellable: int | None}], sells: list[{name: str | None, prefab_name: str | None, credits_each: float, stock: int, gas: bool, details: str |
          None}]}]
    (BatchResult)
      results: list[{index: int, ok: bool, reference_id: str, contacts: list[{reference_id: str, trader: str | None, display_name: str | None,
          shuttle: str, pad_size_tiles: list[int], contacted: bool, buys: list[{name: str | None, prefab_name: str | None, credits_each: float,
          wanted: int, gas: bool, conditions: list[str | None], have: int, sellable: int | None}], sells: list[{name: str | None, prefab_name: str |
          None, credits_each: float, stock: int, gas: bool, details: str | None}]}]} | {index: int, ok: bool, reference_id: str | None, error: Error}]
      count: int
      success_count: int
      error_count: int
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}
    Example: sg.trade('inventory', id='7')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
