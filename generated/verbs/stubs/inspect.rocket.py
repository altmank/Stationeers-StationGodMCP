def inspect(what: Literal['rocket'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A rocket's id, name, network or any part; left out, every rocket.
    compact: bool = True,  # Short form (default): no mass parts, tanks, power devices or checks; False: the whole report.
    self_test: bool = None,  # self_test (boolean, default True (False with compact)): Compare the forecast's formulas with the game's numbers. (default true (false with compact))
    include: list[Literal['parts', 'explain']] = None,  # parts: every part with pose and slots; explain: what the numbers mean.
) -> dict:  # main list: r['rockets']; iterating or indexing it walks every page, in order
    """Every rocket now, or one by id or name ('Tanker'): position, target, mass, fuel, engines, landing confidence, cargo, power. For a flight or landing check use plan flight.
    Reply:
      rockets: list[{reference_id: str, network_id: str, name: str, automated_landing: bool, auto_shut_off: bool, manned: bool, re_entry_profile: str,
          re_entry_altitude_m: float, where: {state: str, mode: str, current: {reference_id: str, name: str, code: str, code_value: int, type: str,
          charted: bool} | None, target: {reference_id: str, name: str, code: str, code_value: int, type: str, charted: bool} | None, progress: float,
          altitude_m: float | None, velocity_ms: float, acceleration_ms2: float, flight_rule: str, route: list[{from: str, to: str, to_code: str,
          distance: float, done: float | None, charted: bool}], uncharted: list[str] | None}, mass: {total_kg: float, structure_kg: float, gas_kg:
          float, cargo_kg: float, payload_kg: float, parts: list[{name: str, count: int, kg: float}] | None}, fuel: list[{network_id: str, engines:
          int, pipe_litres: float, pipe_mol: float, total_mol: float, temperature_k: float, liquid_mol: float, liquid_litres: float, pipe_gas_kpa:
          float, feeds: list[str] | None, mix: dict[str, float], tanks: list[{reference_id: str, name: str, litres: float, mol: float, temperature_k:
          float, pressure_kpa: float, counts_for_mass: bool}] | None}], pipe_networks: list[{network_id: str, volume_l: float, total_mol: float,
          pressure_kpa: float, temperature_k: float, gases_mol: dict[str, float], feeds: list[str]}], engines: list[{reference_id: str, name: str,
          kind: str, engine: str | None, feed: str | None, input2_network_id: str | None, heat_exchange_network_id: str | None, missing_input: str |
          None, on: bool, powered: bool, throttle: float, thrust_n: float, passed_mol: float, exhaust_velocity_ms: float, exhaust_k: float,
          prefab_max_thrust_n: float, used_w: float, input_network_id: str | None, modelled: bool}], thrust: {current_n: float, max_recorded_n: float,
          prefab_max_n: float, max_expected_n: float, achievable_full_n: float | None, recorded_exceeds_achievable: bool, how_recorded: str | None,
          combustion: {rate: float, source: str, note: str | None} | None}, landing_check: {confidence: float, band: str, min_required_thrust_n: float
          | None, expected_thrust_n: float, mass_kg: float, altitude_m: float, delta_v: float, gravity: float, source: str}, landing_at_pad: {pad:
          str, orbital: bool, confidence: float, band: str, min_required_thrust_n: float | None, expected_thrust_n: float, mass_kg: float, altitude_m:
          float, delta_v: float, gravity: float} | None, collects: list[str] | None, cargo: list[{reference_id: str, name: str, filled: int, slots:
          int, kg: float}], power: {charge_j: float, capacity_j: float, percent: float, load_w: float, seconds_left: float | None, engine_w: float,
          devices: list[{reference_id: str, name: str, on: bool, now_w: float, on_w: float, stays_on_at_arrival: bool}] | None}, burn_time:
          {game_estimate_s: float, full_throttle_s: float | None, current_throttle_s: float | None}, checks: list[{name: str, game: float | None,
          ours: float | None, ok: bool}] | None, notes: list[str], parts: list[{reference_id: str, prefab: str, name: str | None, role: str, offset:
          dict[str, float], rotation: {facing: str | None, up: str | None, euler: Vec3}, contents: list[str] | None}] | None}]
      count: int
      explanations: list[str] | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('rocket', compact=True)
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
