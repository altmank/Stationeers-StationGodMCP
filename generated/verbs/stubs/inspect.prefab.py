def inspect(what: Literal['prefab'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    prefab: str | int | list,  # A whole prefab name or hash; or a list, or parts with * ("*Door*"): a row per loaded prefab, by name, at most 64.
    include: list[Literal['small_cells', 'data', 'recipes']] = None,  # small_cells: each cell's offset. data: the prefab's own game data, every field the game serializes on it (maxPressureDelta, -1 for no limit; flashpointTemperature, autoignitionTemperature; MaxQuantity; UsedPower; BuildStates with each state's tool and cost) and mass_kg; fields: ["data.maxPressureDelta"] narrows it, output_file takes it whole. recipes: what makes it: each machine, its inputs, time and energy.
) -> dict:  # main list: r['ports']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A prefab before it stands anywhere: placement kind, turns, cells, ports; its game data and recipes; a list or pattern compares a family.
    One buildable prefab answers its placement view; any other prefab (an item, a kit) its facts; a list or a pattern a row of facts per prefab (prefabs, matched, listed).
    recipes inputs are the game's Recipe amounts by name: grams of an ingot (Iron, Copper, Solder), units of a reagent; a furnace recipe adds its temperature_k and pressure_kpa range.
    Reply, one of:
    (DescribePrefabView)
      prefab_name: str | None
      prefab_hash: int | None
      display_name: str | None
      build_states: int | None
      runtime_type: str
      placement: str
      grid_size_m: float
      small_grid: bool
      rotation_axes: str
      allowed_rotations: list[{facing: str | None, up: str | None, euler: Vec3}]
      small_cell_count: int
      small_cells: list[{x: float, y: float, z: float}] | None
      render_box: {min: {x: float, y: float, z: float}, max: {x: float, y: float, z: float}, size: {x: float, y: float, z: float}}
      grid_box: {min: {x: float, y: float, z: float}, max: {x: float, y: float, z: float}, size: {x: float, y: float, z: float}} | None
      ports: list[{index: int, type: str, role: str, flow: str | None, at: {x: float, y: float, z: float}, outward: str}]
      visual_up: {local: str, lying_allowed: bool, source: str, verified: bool}
      reversible_flow: {logic_type: str, value: int, reason: str} | None
      has_cursor: bool
      controls: {local: str, votes: int, considered: int, source: str, fallback: bool} | None
      engine: {name: str, max_thrust_n: float, max_exhaust_velocity_ms: float, specific_impulse_s: float, max_fuel_flow_rate: float, efficiency:
          float, efficiency_percent: float, mass_kg: float, chamber_volume_l: float, feed: str | None, inputs: list[{input: int, role: str, pipe: str
          | None, pipe_max_kpa: float | None}] | None, max_moles_per_tick: float | None, litres_per_tick: float | None, max_pressure_per_tick_kpa:
          float | None, flow_rate_min_l: float | None, flow_rate_max_l: float | None} | None
      data: dict[str, Any] | None
      recipes: list[{maker: str | None, maker_display_name: str | None, time_s: float, energy_j: float, inputs: dict[str, float], temperature_k:
          {start: float, stop: float} | None, pressure_kpa: {start: float, stop: float} | None, source: str | None}] | None
    (PrefabFactsView)
      prefab_name: str | None
      prefab_hash: int
      display_name: str | None
      runtime_type: str
      buildable: bool
      data: dict[str, Any] | None
      recipes: list[{maker: str | None, maker_display_name: str | None, time_s: float, energy_j: float, inputs: dict[str, float], temperature_k:
          {start: float, stop: float} | None, pressure_kpa: {start: float, stop: float} | None, source: str | None}] | None
    (PrefabListView)
      prefabs: list[{prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str, buildable: bool, data: dict[str, Any] |
          None, recipes: list[{maker: str | None, maker_display_name: str | None, time_s: float, energy_j: float, inputs: dict[str, float],
          temperature_k: {start: float, stop: float} | None, pressure_kpa: {start: float, stop: float} | None, source: str | None}] | None}]
      matched: int
      listed: int
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('prefab', prefab='ItemKitAirlock', include=['recipes'])
    """
