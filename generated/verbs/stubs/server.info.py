def server(what: Literal['info'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    include: list[Literal['counters']] = None,  # counters: job holds, print log, lint chip programs, batch console, prefab index, frame_ms and update_ms (StationGod's whole frame, job steps included).
) -> dict:  # main list: r['methods']; iterating or indexing it walks every page, in order
    """The running mod: version, per-method calls and times, game members, its cost to the game.
    Reply:
      mod_id: str
      mod_version: str
      assembly_version: str | None
      informational_version: str | None
      pipe_name: str
      methods: list[{method: str, calls: int, errors: int, total_ms: float, mean_ms: float | None, max_ms: float}]
      count: int
      reflection: list[{member: str, resolved: bool, optional: bool}]
      missing_count: int
      runtime: {uptime_s: float, world_epoch: int, request_budget_ms: float | None, frames: {busy_frames: int, requests_served: int,
          requests_per_frame_mean: float | None, requests_per_frame_max: int, frame_ms: {total: float, mean: float | None, max: float}, budget_stops:
          int, expired: int}, memory: {gc_collections: int, gc_max_generation: int, gc_total_memory_bytes: int, mono_used_bytes: int | None,
          mono_heap_bytes: int | None}, methods: list[{method: str, calls: int, errors: int, main_thread_ms: float, handler_ms: {total: float, mean:
          float | None, max: float}, serialize_ms: {total: float, mean: float | None, max: float}, queue_wait_ms: {total: float, mean: float | None,
          max: float}, reply_bytes: {total: float, mean: float | None, max: float}}], method_count: int, connections: list[{client_id: str, client:
          str | None, label: str | None, transport: str, protocol: int | None, in_flight: int, served: int, bytes_sent: int, subscriptions: int,
          methods: list[{method: str, calls: int, errors: int, total_ms: float}], method_count: int}] | None, job_settles: {run: int, skipped: int,
          unchecked: int}, prefab_index: {ready: bool, verify: bool, things: int, names: int, verified: int, differences: int, arrived: int | None,
          left: int | None}, job_holds: {under_100ms: int, under_1s: int, under_10s: int, under_60s: int, over_60s: int} | None, print_log: {records:
          int, capacity: int} | None, lint_chip_programs: int | None, batch_console: {lines: int, lines_per_minute: float} | None, frame_ms: {frames:
          int, p50: float | None, p90: float | None, p99: float | None, over_8ms: int, buckets: list[int]} | None, update_ms: {frame: int, frames:
          int, worst_ms: float | None, worst_frame: int | None, over_budget: int, buckets: list[int], recent: list[{frame: int, ms: float}]} | None,
          profiling: {since_s: float, slow_frame_ms: float, frame_ms: {p50: float | None, p95: float | None, max: float | None}, worst_frame_ms: float
          | None, top: list[{name: str, ms: float}]} | None}
    Example: sg.server('info')
    """
