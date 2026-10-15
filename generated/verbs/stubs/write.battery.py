def write(what: Literal['battery'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Batteries or cells (up to 256).
    rocket: str = None,  # Every battery of this rocket.
    within: str = None,  # Every cell in this holder's slots, nested; "player" for the player.
    ratio: float = 'full',  # Charge as a share of capacity. (default full)
    joules: float = 'full',  # Charge in joules, clamped. (default full)
) -> dict:  # main list: r['results']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Set the charge of batteries and cells: full by default, or a ratio or joules.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      goal: {kind: str, value: float | None}
      found_in: Thing | None
      results: list[{index: int, ok: bool, reference_id: str, prefab_name: str | None, display_name: str | None, kind: str, held_by: Thing | None,
          capacity_j: float, before_j: float, after_j: float, before_ratio: float | None, after_ratio: float | None, clamped: bool | None} | {index:
          int, ok: bool, reference_id: str | None, error: Error} | {index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
      added_j: float
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}; Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('battery', within='player')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
