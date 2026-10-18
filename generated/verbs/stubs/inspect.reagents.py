def inspect(what: Literal['reagents'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
) -> dict:  # main list: r['reagents']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """The reagents a furnace, centrifuge, mixer or microwave holds.
    A list of ids answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply below; fields name its keys.
    Reply:
      reference_id: str
      prefab_name: str | None
      display_name: str | None
      total: float
      reagents: list[{reagent: str, name: str | None, quantity: float, unit: str | None}]
    Example: sg.inspect('reagents', id='4191359')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
