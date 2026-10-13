def write(what: Literal['flight_log'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    action: Literal['start', 'stop', 'clear'],  # start, stop or clear the recorder.
    id: str = None,  # A rocket's id, name, network or any part; left out when there is one rocket.
    interval_s: float = 1,  # start: game seconds between samples.
    capacity: int = 3600,  # start: samples kept.
    csv: bool = False,  # start: also write a CSV next to the save.
) -> dict:  # main list: r['events']; iterating or indexing it walks every page, in order
    """Start, stop or clear a rocket flight recorder (server state; never the world); read it with read flight_log.
    One id only: a list is refused (this write cannot be undone).
    Reply, one of:
    (FlightLog)
      rocket_id: str
      rocket_name: str
      recording: bool
      interval_s: float
      csv_path: str | None
      capacity: int
      held: int
      recorded: int
      from_t: float | None
      to_t: float | None
      fuel_used_mol: float | None
      lowest_altitude_m: float | None
      fastest_descent_ms: float
      peak_thrust_n: float
      lowest_confidence: float | None
      lowest_battery_j: float | None
      last: {t: float, state: str, node: str, target: str, progress: float, altitude_m: float, velocity_ms: float, acceleration_ms2: float, mass_kg:
          float, fuel_mol: float, fuel_k: float, fuel_kpa: float, throttle: float, thrust_n: float, max_recorded_thrust_n: float, confidence: float,
          battery_j: float, cargo_slots: int, burn_time_s: float, rule: str} | None
      events: list[{t: float, what: str}]
      events_dropped: int
      rows: list[{t: float, state: str, node: str, target: str, progress: float, altitude_m: float, velocity_ms: float, acceleration_ms2: float,
          mass_kg: float, fuel_mol: float, fuel_k: float, fuel_kpa: float, throttle: float, thrust_n: float, max_recorded_thrust_n: float, confidence:
          float, battery_j: float, cargo_slots: int, burn_time_s: float, rule: str}]
      offset: int
      every: int
      has_more: bool
    (FlightLogCleared)
      cleared: bool
    (FlightLogList)
      logs: list[{rocket_id: str, rocket_name: str, recording: bool, interval_s: float, csv_path: str | None, capacity: int, held: int, recorded: int,
          from_t: float | None, to_t: float | None, fuel_used_mol: float | None, lowest_altitude_m: float | None, fastest_descent_ms: float,
          peak_thrust_n: float, lowest_confidence: float | None, lowest_battery_j: float | None, last: {t: float, state: str, node: str, target: str,
          progress: float, altitude_m: float, velocity_ms: float, acceleration_ms2: float, mass_kg: float, fuel_mol: float, fuel_k: float, fuel_kpa:
          float, throttle: float, thrust_n: float, max_recorded_thrust_n: float, confidence: float, battery_j: float, cargo_slots: int, burn_time_s:
          float, rule: str} | None, events: list[{t: float, what: str}], events_dropped: int, rows: list[{t: float, state: str, node: str, target:
          str, progress: float, altitude_m: float, velocity_ms: float, acceleration_ms2: float, mass_kg: float, fuel_mol: float, fuel_k: float,
          fuel_kpa: float, throttle: float, thrust_n: float, max_recorded_thrust_n: float, confidence: float, battery_j: float, cargo_slots: int,
          burn_time_s: float, rule: str}], offset: int, every: int, has_more: bool}]
      count: int
    Example: sg.write('flight_log', action='start', id='Tanker')
    """
