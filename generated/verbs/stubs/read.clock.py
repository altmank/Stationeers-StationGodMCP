def read(what: Literal['clock'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:
    """Game time, paused, time of day, days past.
    No options.
    Reply:
      game_time_s: float
      paused: bool
      time_of_day_ratio: float
      days_past: int
    Example: sg.read('clock')
    """
