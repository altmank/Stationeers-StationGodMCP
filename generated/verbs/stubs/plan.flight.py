def plan(what: Literal['flight'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A rocket's id, name, network or any part; left out when there is one rocket.
    node: str = None,  # Destination node: name, map code, id, or 'pad'; default the rocket's target, else its pad.
    profile: Literal['Low', 'Medium', 'High', 'Max'] = None,  # Re-entry profile; default the rocket's.
    park_s: float = None,  # Seconds at the destination.
    mine: bool = False,  # Mine while parked.
    fill_holds: bool = None,  # Every cargo slot filled by the end of the stop.
    add_cargo_slots: int = None,  # Cargo slots filled during the stop.
    park_load_w: float = None,  # Power draw per tick while parked. (default what auto shut-off leaves on, plus mining gear with mine) (default what auto shut-off leaves on, plus mining gear with mine)
    then_return: bool = False,  # After the stop, fly back and land.
    return_to: str = None,  # The mount to land on after the stop; default this rocket's pad.
    deploy_payload: bool = False,  # At a Deploy node the payloads leave.
    transfer_mol: float = None,  # Fuel received at the stop (negative: given).
    transfer_with: str = None,  # Another rocket at the stop whose umbilicals equalise with this one.
    transfer_battery_j: float = None,  # Battery charge received at the stop (negative: given).
    what_if: dict = None,  # Replace measured values: {fuel_mol, fuel_temperature_k, fuel_mix {gas: fraction}, thrust_scale 0-10, throttle 0-100, cargo_slots, add_cargo_kg, payload_kg, battery_j, battery_percent, extra_load_w}.
    min_confidence: float = 0.25,  # Confidence floor for the verdict.
    limits: bool = True,  # Landing limits, profiles and least fuel.
    column_check: bool = True,  # Scan the landing column above the pad.
    include: list[Literal['explain']] = None,  # explain: the model's fixed rules.
) -> dict:  # main list: r['legs']; iterating or indexing it walks every page, in order
    """Fly a copy of a rocket to a node or pad, tick by tick: launch, hops, re-entry, landing, fuel, power and the verdict; the landing check.
    Reply:
      rocket_id: str
      rocket_name: str
      start_state: str
      destination: {reference_id: str, name: str, code: str, code_value: int, type: str, charted: bool} | None
      route: list[{from: str, to: str, to_code: str, distance: float, done: float | None, charted: bool}]
      uncharted: list[str] | None
      assumptions: list[str]
      legs: list[{kind: str, from: str, to: str, distance: float | None, outcome: str, detail: str, seconds: float, burn_seconds: float, delta_v:
          float, fuel_start_mol: float, fuel_used_mol: float, fuel_left_mol: float, fuel_kpa_end: float, mass_start_kg: float, mass_end_kg: float,
          battery_start_j: float, battery_end_j: float, peak_thrust_n: float, max_recorded_thrust_n: float, landing: {confidence: float | None,
          confidence_band: str | None, min_required_thrust_n: float | None, max_expected_thrust_n: float, start_altitude_m: float, start_mass_kg:
          float, lowest_stop_altitude_m: float | None, touchdown_speed_ms: float, seconds: float, last_rule: str, power_lost: bool, fuel_ran_out:
          bool} | None}]
      succeeded: bool
      verdict: str
      fuel_now_mol: float
      least_fuel_mol: float | None
      landing_limits: {max_landing_mass_kg: float | None, max_fuel_plus_cargo_kg: float | None, min_fuel_mol: float | None, max_fuel_mol: float |
          None, survivable_thrust_drop_pct: float | None, confidence_floor: float, confident_mass_kg: float | None} | None
      profiles: list[{profile: str, altitude_m: float, outcome: str, fuel_used_mol: float, confidence: float | None, seconds: float | None,
          touchdown_speed_ms: float | None, survivable_thrust_drop_pct: float | None}] | None
      column: {clear: bool, from_y: float, to_y: float, blockers: list[{reference_id: str | None, what: str, y: float, swept_by: str}], more: int} |
          None
      combustion: {rate: float, source: str, note: str | None} | None
      mining: {site: str, kind: str, collectable: bool, units_per_hour: float, units_in_stop: float, slots_in_stop: int, problems: list[str] | None} |
          None
    Example: sg.plan('flight', id='4067059', node='pad')
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
