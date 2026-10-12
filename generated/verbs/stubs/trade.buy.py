def trade(what: Literal['buy'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The trader contact.
    card: str = None,  # The credit card; default the one the player carries.
    items: list,  # Up to 32 lines {name or prefab, quantity}, in order.
) -> dict:  # main list: r['delivered']; iterating or indexing it walks every page, in order
    """Buy from the landed trader as the trade window's Buy does.
    Reports in a report run; acts in a confirmed run.
    Reply:
      reference_id: str
      name: str | None
      dry_run: bool
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
    Types: Error = {code: str, message: str, data: Any | None, see: {tool: str | None, topic: str, subtopic: str | None} | None}; Thing =
        {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.trade('buy', id='7', items=[{'prefab': 'ItemIronIngot', 'quantity': 50}])
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
