def server(what: Literal['profiling'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    action: Literal['on', 'off', 'reset', 'report'],  # on, off, reset, or report the scopes so far.
    slow_frame_ms: float = 8,  # on: frames over this are logged.
    csv: bool = None,  # on: True starts the rolling CSV, False stops it.
) -> dict:  # main list: r['scopes']; iterating or indexing it walks every page, in order
    """Profile StationGod's main-thread cost: on, off, reset, report.
    Reply:
      enabled: bool
      since_s: float | None
      slow_frame_ms: float | None
      csv_path: str | None
      csv: {path: str, writing: bool, rows: int, dropped: int, error: str | None} | None
      failure: str | None
      timed_methods: int
      untimed_methods: list[str]
      frames: {recorded: int, window: int, stationgod_ms: {p50: float | None, p95: float | None, max: float | None}, heap_delta_bytes: {p50: float |
          None, p95: float | None, max: float | None}, heap_deltas_dropped: int, slow: int} | None
      scopes: list[{name: str, kind: str, total_ms: float, self_ms: float, count: int, mean_ms: float, p95_frame_ms: float | None, max_frame_ms: float
          | None}] | None
      worst_frame: {frame: int, ms: float, heap_delta_bytes: int | None, scopes: list[{name: str, ms: float}], scopes_not_listed: int, calls:
          list[{name: str, ms: float}], calls_not_listed: int} | None
      calls: list[{method: str, calls: int, errors: int, queue_wait_ms: {total: float, mean: float | None, max: float}, execute_ms: {total: float,
          mean: float | None, max: float}, serialize_ms: {total: float, mean: float | None, max: float}, reply_bytes: {total: float, mean: float |
          None, max: float}}] | None
      tick_holds: {count: int, total_ms: float, max_ms: float, recent: list[{job: str, tool: str | None, ms: float, frames: int, holding: bool}]} |
          None
    Example: sg.server('profiling', action='report')
    """
