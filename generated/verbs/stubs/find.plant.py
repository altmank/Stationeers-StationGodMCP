def find(what: Literal['plant'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One plant, in full.
    include: list[Literal['unplanted']] = None,  # unplanted: crops and seeds not planted.
    verbose: bool = False,  # The long form of the reply.
) -> dict:  # main list: r['plants']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Plants in trays, planters and stations: stage, health, problems, time to harvest.
    Reply:
      plants: list[{reference_id: str, prefab_name: str | None, display_name: str | None, planted: bool, tray: {reference_id: str, prefab_name: str |
          None, display_name: str | None, lit_by_grow_light: bool} | None, stage: int, stage_count: int, maturity_ratio: float, ready_to_harvest:
          bool, dead: bool, health_percent: int | None, problems: list[str], harvest_in_s: float | None}] | list[{reference_id: str, prefab_name: str
          | None, name: str | None, display_name: str | None, planted: bool, tray: {reference_id: str, prefab_name: str | None, display_name: str |
          None, lit_by_grow_light: bool} | None, stage: {index: int, count: int, first_mature_index: int, first_seeding_index: int, dead_index: int,
          mature_index: int, seeding_index: int, progress_s: float | None, length_s: float | None}, stages: list[{index: int, length_s: float, mature:
          bool, seeding: bool, dead: bool}], maturity_ratio: float, seeding_ratio: float, mature: bool, seeding: bool, dead: bool, perennial: bool |
          None, ready_to_harvest: bool, efficiency: {growth_factor: float | None, growth_percent: int, breathing_factor: float, temperature_factor:
          float, hydration_factor: float, pressure_factor: float, light_factor: float, random_factor: float, gene_speed_factor: float | None},
          problems: list[str], active_states: list[str], conditions: list[{state: str, words: str, active: bool, time_s: float | None, damage_after_s:
          float | None, damaging: bool, damage_in_s: float | None}] | None, can_heal: bool | None, health: {damage_ratio: float, health_percent: int,
          total: float, max: float, brute: float, burn: float, oxygen: float, hydration: float, toxic: float, starvation: float, radiation: float,
          stun: float, decay: float} | None, harvest: {quantity: int, max: int, seeds: int, fertiliser_bonus: float, forecast: {expected: float,
          stress: float} | None, nutrition_each: float}, fertiliser: {fertilised: bool, growth_boost: float | None, harvest_bonus: float, waiting:
          {reference_id: str, prefab_name: str | None, growth_speed: float, harvest_boost: float, cycles: float} | None}, light: {exposure: float,
          lit: bool, lit_ratio: float | None, dark_ratio: float | None, stress: float | None}, atmosphere: {pressure_kpa: float, temperature_k: float,
          total_mol: float, ratios: dict[str, float | None]} | None, water: {liquid_mol: float, water_mol: float, temperature_k: float} | None, needs:
          {profile: str | None, temperature_k: {ideal: list[float], survivable: list[float]}, pressure_kpa: {ideal: list[float], survivable:
          list[float]}, takes_in: list[{gas: str, mol_per_tick: float, needs_ratio: float, ratio_now: float | None}], gives_out: list[{gas: str,
          mol_per_tick: float}], water_mol_per_tick: float | None, light_per_day_s: float, dark_per_day_s: float, harmful: list[{gas: str, limit_kpa:
          float, now_kpa: float | None}]} | None, record: {age_s: float, dry_s: float, cold_s: float, hot_s: float, suffocated_s: float,
          low_pressure_s: float, high_pressure_s: float, polluted_s: float} | None, genes: dict[str, float] | None, forecast: {growth_rate: float |
          None, next_stage_s: float | None, harvest_in_s: float | None, seeds_in_s: float | None, regrow_s: float | None, regrow_full_speed_s: float |
          None}}]
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
