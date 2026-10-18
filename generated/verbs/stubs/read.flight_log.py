def read(what: Literal['flight_log'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    action: Literal['read', 'list'] = 'read',  # read: a page of a recording's rows; list: the recordings.
    id: str | list[str] = None,  # A rocket's id, name, network or any part; left out when there is one rocket.
    every: int = 1,  # read: every n-th row.
    limit: int = 12,  # The first n in order (read: rows); left out, every one.
) -> dict:  # main list: r['rows']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A rocket flight recorder's samples: a page of rows, or the list of recordings (start and stop: write flight_log).
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
    Example: sg.read('flight_log', limit=20)
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
