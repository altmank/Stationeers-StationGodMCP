def check(what: Literal['replaceable'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Things to check (1 to 1024).
) -> dict:  # main list: r['results']; iterating or indexing it walks every page, in order
    """Could a player place each thing again exactly where it stands, every neighbour present.
    Reply:
      results: list[{reference_id: str, prefab_name: str | None, replaceable: bool | None, rule: str | None, reason: str | None}]
    Example: sg.check('replaceable', id='r12.things')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
