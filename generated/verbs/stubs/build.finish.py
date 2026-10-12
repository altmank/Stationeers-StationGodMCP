def build(what: Literal['finish'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # One structure or a list: one job per id, in order.
    state: int = None,  # The build state to reach; default the next.
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    free: bool = False,  # Creative worlds: no materials. Cheat.
) -> dict:
    """Build a placed structure on to a later build state, paying each state's materials.
    Reports in a report run; acts in a confirmed run.
    Cheat when free: true: ask the user first.
    Every build reply has placed: list[str] at the top: the new ids ([] in any report: a report run builds nothing, and writes on what it would build are not shown).
    Reply:
      dry_run: bool
      thing: Thing
      from_state: int
      to_state: int
      last_state: int
      cost: list[{prefab_name: str, display_name: str, quantity: int, available: int | None, paid_by: list[{reference_id: str, name: str | None,
          quantity: int, available: int}] | None}]
      tools: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}]
      ready: bool
      problems: list[str]
      build_state: {current: int, last: int, complete: bool, next: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] |
          None} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.build('finish', id='1199811', state=2)
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
