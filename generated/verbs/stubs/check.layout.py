def check(what: Literal['layout'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    room: str = None,  # A room id.
    box: list = None,  # Two corners; at most 4000 2 m cells.
    codes: list[str] = None,  # Only findings of these codes.
    exclude_codes: list[str] = None,  # Leave these codes out.
    id: str | list[str] = None,  # Narrows: only findings on these things (up to 256); room or box is still needed.
    since: str = None,  # Only findings on this id and higher ids (what was made after it).
    limit: int = 25,  # The first n in order (findings returned); left out, every one.
) -> dict:  # main list: r['findings']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Lint a room or a box against the layout rules: replaceability, doors, ports, floating runs, overlaps and more.
    Reply:
      region: str
      cells: int
      pieces: int
      devices: int
      structures: int
      doors: int
      counts: dict[str, int]
      findings: list[{code: str, level: str, message: str, reference_id: str | None, other_id: str | None, at: {x: float, y: float, z: float},
          subject: str | None, rule_id: str | None}]
      total: int
      filtered_out: int | None
      has_more: bool
      rule_source: {mod_file: str | None, save_file: str | None, rules: int, from_save: list[str], disabled: list[str], errors: list[{file: str,
          rule_id: str | None, field: str | None, line: int, column: int, position: int | None, message: str}]}
      elapsed_ms: float
    Example: sg.check('layout', room='17')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
