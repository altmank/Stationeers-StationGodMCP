def inspect(what: Literal['reagents'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
) -> dict:  # main list: r['reagents']; iterating or indexing it walks every page, in order
    """The reagents a furnace, centrifuge, mixer or microwave holds.
    A list of ids answers {results: [{index, ok, reference_id, ...the reply below} or {index, ok, reference_id, error}], count, success_count, error_count}; fields paths are results.<key>.
    Reply:
      reference_id: str
      prefab_name: str | None
      display_name: str | None
      total: float
      reagents: list[{reagent: str, name: str | None, quantity: float, unit: str | None}]
    Example: sg.inspect('reagents', id='4191359')
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
