def build(what: Literal['finish'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # One structure or a list, in order.
    state: int = None,  # The build state to reach; default the next.
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    free: bool = False,  # Creative worlds: no materials. Cheat.
) -> dict:
    """Build a placed structure on to a later build state, paying each state's materials.
    Reports in a report run; acts in a confirmed run.
    Cheat when free: true: ask the user first.
    placed: list[str] on a job reply, the new ids (a run's log.placed, a structure job's result.placed; a brief poll counts them in log.placed_count); int on a run report, the cells it would build.
    Reply, one of:
    (FinishReport)
      ready: bool
      free: bool
      from: Thing | None
      materials: list[{prefab_name: str, display_name: str, quantity: int, available: int | None}]
      steps: list[{index: int, reference_id: str, name: str, from_state: int, to_state: int, last_state: int, cost: list[{prefab_name: str,
          display_name: str, quantity: int, available: int | None, paid_by: list[{reference_id: str, name: str | None, quantity: int, available: int}]
          | None}], tools: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}]}]
      warnings: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      problems: list[{code: str, message: str, index: int | None, reference_id: str | None}]
    (BatchResult)
      results: list[{index: int, ok: bool, reference_id: str, thing: Thing, from_state: int, to_state: int, last_state: int, cost: list[{prefab_name:
          str, display_name: str, quantity: int, available: int | None, paid_by: list[{reference_id: str, name: str | None, quantity: int, available:
          int}] | None}], tools: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}], build_state: {current: int, last: int,
          complete: bool, next: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] | None} | None} | {index: int, ok: bool,
          reference_id: str | None, error: Error}]
      count: int
      success_count: int
      error_count: int
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Error = {code: str, message: str, data: Any | None, see:
        {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} | None}
    Example: sg.build('finish', id='1199811', state=2)
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
