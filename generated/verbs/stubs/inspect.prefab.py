def inspect(what: Literal['prefab'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    prefab: str | int,  # Whole prefab name or hash.
    include: list[Literal['small_cells']] = None,  # small_cells: each cell's offset.
) -> dict:  # main list: r['ports']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A buildable prefab before it stands anywhere: placement kind, turns, cells, ports.
    Reply:
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
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('prefab', prefab='StructureWallLight')
    """
