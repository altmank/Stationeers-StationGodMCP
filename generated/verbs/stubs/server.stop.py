def server(what: Literal['stop'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str = None,  # The run (script-14), or a job (place-41) still waiting in the queue: it is dropped; a job that has started is refused (job_not_waiting) and runs to its end. Left out: every live run, answered as one list with one combined left.
) -> dict:
    """Stop a script run, or drop a job still waiting in the queue; with no id, every live run. The run gets sg.Stopped once, so its finally runs; held restores and logic writes to targets it already wrote act as CLEANUP until the kill (5 s, longer while a call is in flight, never past 30 s, or a second stop); other writes answer an error row. The reply lists what the run left changed (left), each job it started that is still in flight with its fate (a started job is not stopped: it runs to its end), and the undo of its writes.
    Reply, one of:
    ()
      id: str
      first_line: str | None
      state: str
      line: int | None
      left: list[{id: str, logic: str, value: float, line: int | None}]
      in_flight: list[{job_id: str, fate: str}]
      undo: str | None
    ()
      runs: list[{id: str, first_line: str, state: str, line: int | None, left: list, in_flight: list, undo: str | None}]
      count: int
      left: list[{run: str, id: str, logic: str, value: float, line: int | None}]
    Example: sg.server('stop', id='script-14')
    """
