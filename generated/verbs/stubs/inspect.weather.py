def inspect(what: Literal['weather'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:
    """The storm schedule, weather events and season.
    No options.
    Reply:
      world_has_weather: bool
      state: str
      current: {id: str | None, name: str | None, running: bool, starts_in_s: float, length_s: float} | None
      days_past: int
      days_since_last: int
      cooldown_days: int
      world_start_delay_days: float
      can_schedule_now: bool
      schedulable_in_days: int
      events: list[{id: str | None, name: str | None, cooldown_days_min: int, cooldown_days_max: int, start_delay_s_min: float, start_delay_s_max:
          float, duration_s_min: float, duration_s_max: float, temperature_offset_day_k: float | None, temperature_offset_night_k: float | None,
          solar_ratio: float | None, active_in_orbit: bool}]
      season: {true_anomaly_deg: float, year_length_days: float, solar_energy_percent: float, day_of_year: float} | None
    Example: sg.inspect('weather')
    """
