def find(what: Literal['plant'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One plant, in full.
    include: list[Literal['unplanted']] = None,  # unplanted: crops and seeds not planted.
    verbose: bool = False,  # The long form of the reply.
) -> dict:  # main list: r['plants']; iterating or indexing it walks every page, in order
    """Plants in trays, planters and stations: stage, health, problems, time to harvest.
    Reply:
      game_time_s: float
      day_length_s: int
      debug_fast_growth: bool
      plants: list[Any]
      count: int
    Example: sg.find('plant')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
