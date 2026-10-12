def server(what: Literal['stop'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str = None,  # The run (script-14) or job (place-41). Left out: every live run, answered as one combined list.
) -> dict:
    """Stop a script run or a job; with no id, every live run of this session. A script run gets sg.Stopped (a BaseException) once, at its next sg call (at once inside sg.wait or sg.watch), so its finally blocks run: after it, reads and writes act, while waits and watches raise at once. The worker is killed 5 s after the stop (longer while an sg call is in flight, never past 30 s), or on a second stop. A running job is allowed to settle.
    Reply:
      id: str
      state: str
      line: str | None
      left: list[{id: str, logic: str, value: float, line: int | None}]
      in_flight: list[{job_id: str, fate: str}]
      undo: str | None
    Example: sg.server('stop', id='script-14')
    """
