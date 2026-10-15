def trade(what: Literal['buy'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The trader contact. A list is one row per id; a contact that is not landed answers an error row, and the others still trade.
    card: str = None,  # The credit card; default the one the player carries.
    items: list,  # Up to 32 lines {name or prefab, quantity}, in order.
) -> dict:  # main list: r['delivered']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Buy from the landed trader as the trade window's Buy does.
    Reports in a report run; acts in a confirmed run.
    Reply, one of:
    (Trade)
      reference_id: str
      name: str | None
      credit_card_id: str
      credits_before: float
      credits_after: float
      results: list[{index: int, ok: bool, name: str | None, prefab_name: str | None, quantity: int, credits: float, error: Error} | {index: int, ok:
          bool, name: str | None, prefab_name: str | None, gas: bool, quantity: int, credits_each: float, credits_spent: float | None, credits_earned:
          float | None, stock_after: int | None, wanted_after: int | None} | {index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
      delivered: list[{reference_id: str, slot: int, item: Thing, quantity: float}]
      gas_atmosphere_id: str | None
    (BatchResult)
      results: list[{index: int, ok: bool, reference_id: str, name: str | None, credit_card_id: str, credits_before: float, credits_after: float,
          results: list[{index: int, ok: bool, name: str | None, prefab_name: str | None, quantity: int, credits: float, error: Error} | {index: int,
          ok: bool, name: str | None, prefab_name: str | None, gas: bool, quantity: int, credits_each: float, credits_spent: float | None,
          credits_earned: float | None, stock_after: int | None, wanted_after: int | None} | {index: int, ok: bool, error: Error}], count: int,
          success_count: int, error_count: int, delivered: list[{reference_id: str, slot: int, item: Thing, quantity: float}], gas_atmosphere_id: str
          | None} | {index: int, ok: bool, reference_id: str | None, error: Error}]
      count: int
      success_count: int
      error_count: int
    (TradeReport)
      reference_id: str
      name: str | None
      credit_card_id: str
      credits_before: float
      credits_after: float
      results: list[{index: int, ok: bool, name: str | None, prefab_name: str | None, quantity: int, credits: float, error: Error} | {index: int, ok:
          bool, name: str | None, prefab_name: str | None, gas: bool, quantity: int, credits_each: float, credits_spent: float | None, credits_earned:
          float | None, stock_after: int | None, wanted_after: int | None} | {index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
      delivered: list[{reference_id: str, slot: int, item: Thing, quantity: float}]
      gas_atmosphere_id: str | None
      report_only: bool
      targets: list[{id: str | None, cell: list[float] | None, name: str | None, current: Any | None, new: Any | None, undo: str, passed: list[str] |
          None, driven_by: Any | None, text: str}]
      problems: list[{index: int | None, reference_id: str | None, code: str, message: str, data: Any | None}]
    (TradesReport)
      trades: list[{reference_id: str, name: str | None, credit_card_id: str, credits_before: float, credits_after: float, results: list[{index: int,
          ok: bool, name: str | None, prefab_name: str | None, quantity: int, credits: float, error: Error} | {index: int, ok: bool, name: str | None,
          prefab_name: str | None, gas: bool, quantity: int, credits_each: float, credits_spent: float | None, credits_earned: float | None,
          stock_after: int | None, wanted_after: int | None} | {index: int, ok: bool, error: Error}], count: int, success_count: int, error_count:
          int, delivered: list[{reference_id: str, slot: int, item: Thing, quantity: float}], gas_atmosphere_id: str | None}]
      report_only: bool
      targets: list[{id: str | None, cell: list[float] | None, name: str | None, current: Any | None, new: Any | None, undo: str, passed: list[str] |
          None, driven_by: Any | None, text: str}]
      problems: list[{index: int | None, reference_id: str | None, code: str, message: str, data: Any | None}]
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}; Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.trade('buy', id='7', items=[{'prefab': 'ItemIronIngot', 'quantity': 50}])
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
