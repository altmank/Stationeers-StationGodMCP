def server(what: Literal['console'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    lines: int = 40,  # How many recent lines.
) -> dict:  # main list: r['lines']; iterating or indexing it walks every page, in order
    """The latest in-game console lines, oldest first.
    Reply:
      requested_lines: int
      line_count: int
      console_available: bool
      lines: list[{time: str | None, level: str | None, text: str | None}]
    Example: sg.server('console', lines=80)
    """
