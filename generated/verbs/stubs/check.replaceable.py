def check(what: Literal['replaceable'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Things to check (1 to 1024).
) -> dict:  # main list: r['results']; iterating or indexing it walks every page, in order
    """Could a player place each thing again exactly where it stands, every neighbour present.
    Reply, one of:
    (Replaceable)
      prefab_name: str | None
      replaceable: bool | None
      rule: str | None
      reason: str | None
    (BatchResult)
      results: list[{index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}
    Example: sg.check('replaceable', id='r12.things')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
