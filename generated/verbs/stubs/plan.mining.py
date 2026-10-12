def plan(what: Literal['mining'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A rocket's id or name; left out when there is one.
    node: str = None,  # A node: its deposit and sites; left out, every charted node with a deposit.
    collectable_only: bool = False,  # Only sites this loadout collects at.
    include: list[Literal['explain']] = None,  # explain: how Mine mode and the yield work.
) -> dict:  # main list: r['sites']; iterating or indexing it walks every page, in order
    """What a rocket can collect per site, by the game's formulas: yield per cycle and hour, what stops it.
    Reply:
      rocket_id: str
      rocket_name: str
      loadout: {miners: list[{reference_id: str, name: str, on: bool, head: {name: str, speed: float, ice_yield: float, ore_yield: float, health:
          float, power_factor: float, durability: float, max_durability: float, expected_cycles_left: float, mines: str} | None, power_w: float,
          ore_speed: float, ice_speed: float, hold_on_board: bool}], collectors: list[{reference_id: str, name: str, on: bool, has_pipe_network:
          bool}], scanners: list[{reference_id: str, name: str, on: bool, head_kind: str, head: str | None, points: int, cycle_s: float, does: str}],
          cargo: {holds: int, slots: int, filled: int, free: int}, gas_tank_litres: float, liquid_tank_litres: float, collects: list[str]}
      collectable: list[str]
      not_collectable: list[str]
      sites: list[{node: {reference_id: str, name: str, code: str, code_value: int, type: str, charted: bool}, parent: str | None, kind: str, density:
          float, richness: float, size: float, survey_percent: float, depleted: bool, cycle_time_s: float, base_units_per_cycle: int, stack_size: int,
          materials: list[{kind: str, prefab: str, weight: float, per_unit: dict[str, float] | None}], machines: list[{machine: str, collects: str,
          collected: bool, units_per_cycle: int, cycle_s: float | None, units_per_hour: float, slots_per_hour: float | None, cycles_to_deplete: float
          | None, total_units_left: float | None, head_cycles_left: float | None, problems: list[str] | None}], collectable: bool, units_per_hour:
          float, problems: list[str] | None}]
      count: int
      notes: list[str]
    Example: sg.plan('mining', collectable_only=True)
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
