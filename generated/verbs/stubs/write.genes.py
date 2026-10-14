def write(what: Literal['genes'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # One id, or "looking_at" (the thing the crosshair is on).
    unit: int = None,  # Which gene set; default the top.
    genes: dict,  # Gene name to new value, -1 to 1 ({"GrowthSpeedMultiplier": 1}).
    allow: list[Literal['out_of_range']] = None,  # out_of_range: write values outside -1..1 too.
) -> dict:  # main list: r['results']; iterating or indexing it walks every page, in order
    """Edit plant genes as the Gene Splicer does.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply, one of:
    (BatchResult)
      results: list[{index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    (GenesWritten)
      thing: Thing
      holder: str
      unit: int
      is_top: bool
      results: list[{index: int, ok: bool, gene: str, previous_value: float | None, error: Error} | {index: int, ok: bool, gene: str, previous_value:
          float | None, value: float} | {index: int, ok: bool} | {index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}; Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('genes', id='3311223', genes={'GrowthSpeedMultiplier': 1})
    """
