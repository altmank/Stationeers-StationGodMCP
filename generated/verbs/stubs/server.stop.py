def server(what: Literal['stop'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str = None,  # The run (script-14) or job (place-41). Left out: every live run, answered as one combined list.
) -> dict:
    """Stop a script run or a job; with no id, every live run. The run gets sg.Stopped once, so its finally runs; held restores and logic writes to targets it already wrote act as CLEANUP until the kill (5 s, longer while a call is in flight, never past 30 s, or a second stop); other writes answer an error row.
    Reply:
      id: str
      state: str
      line: str | None
      left: list[{id: str, logic: str, value: float, line: int | None}]
      in_flight: list[{job_id: str, fate: str}]
      undo: str | None
    Example: sg.server('stop', id='script-14')
    """
