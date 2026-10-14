def find(what: Literal['plant'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One plant, in full.
    include: list[Literal['unplanted']] = None,  # unplanted: crops and seeds not planted.
    verbose: bool = False,  # The long form of the reply.
) -> dict:  # main list: r['plants']; iterating or indexing it walks every page, in order
    """Plants in trays, planters and stations: stage, health, problems, time to harvest.
    Reply:
      plants: list[Any]
      count: int
      total: int
      offset: int
      has_more: bool
      game_time_s: float
      day_length_s: int
      debug_fast_growth: bool
    Example: sg.find('plant')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
