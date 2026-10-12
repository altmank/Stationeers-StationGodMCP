def inspect(what: Literal['genes'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Up to 256 plants, seeds or produce.
    unit: int = None,  # With one id: which gene set, 0-based; default the top.
    include: list[Literal['notes']] = None,  # notes: the tool's fixed text.
) -> dict:  # main list: r['genes']; iterating or indexing it walks every page, in order
    """The genes of plants, seeds and produce.
    Reply, one of:
    (PlantGenes)
      thing: Thing
      holder: str
      gene_set_count: int
      unit: int
      is_top: bool
      genes: list[{gene: str, value: float | None, stability: float | None, meaning: str | None, effect: Any | None}]
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
    Types: Error = {code: str, message: str, data: Any | None, see: {tool: str | None, topic: str, subtopic: str | None} | None}; Thing =
        {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.inspect('genes', id='3311223')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
