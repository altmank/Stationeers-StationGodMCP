def find(what: Literal['damaged'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    min_damage: float = 0,  # Only damage ratio above this (0.25 yellow, 0.75 red).
    broken_only: bool = False,  # Only things in the game's broken state.
    structures_only: bool = False,  # Leave items out.
    near: str | list = None,  # "player" only.
    radius: float = None,  # Metres around near.
    limit: int = 8,  # The first n in order (entries); left out, every one.
) -> dict:  # main list: r['things']; iterating or indexing it walks every page, in order
    """Damaged and broken things in the world, worst first.
    Reply, one of:
    (Health)
      reference_id: str
      prefab_name: str | None
      display_name: str | None
      custom_name: str | None
      build_state: {current: int, last: int, complete: bool, next: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] |
          None} | None
      condition: str
      kind: str
      type: str
      damage_state: str
      damage_state_class: str | None
      max_damage: float | None
      total_damage: float | None
      damage_ratio: float | None
      health_percent: int | None
      band: str | None
      damage: {brute: float, burn: float, oxygen: float, hydration: float, starvation: float, toxic: float, radiation: float, decay: float, stun:
          float} | None
      is_broken: bool
      broken_build_state: bool | None
      being_destroyed: bool
      pipe_burst: str | None
      damage_record: list[str] | None
      position: Vec3
      distance_m: float | None
      networks: list[{kind: str, id: str}] | None
    (BatchResult)
      results: list[{index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    (HealthNetwork)
      network_id: str
      kind: str
      pieces: int
      damaged_only: bool
      warning: str | None
      broken_neighbour_count: int
      broken_neighbours: list[{reference_id: str, prefab_name: str | None, position: Vec3, pipe_burst: str | None, network: {kind: str, id: str} |
          None, touches: str}] | None
      things: list[{reference_id: str, prefab_name: str | None, display_name: str | None, custom_name: str | None, build_state: {current: int, last:
          int, complete: bool, next: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] | None} | None, condition: str,
          kind: str, type: str, damage_state: str, damage_state_class: str | None, max_damage: float | None, total_damage: float | None, damage_ratio:
          float | None, health_percent: int | None, band: str | None, damage: {brute: float, burn: float, oxygen: float, hydration: float, starvation:
          float, toxic: float, radiation: float, decay: float, stun: float} | None, is_broken: bool, broken_build_state: bool | None, being_destroyed:
          bool, pipe_burst: str | None, damage_record: list[str] | None, position: Vec3, distance_m: float | None, networks: list[{kind: str, id:
          str}] | None}]
      count: int
      total: int
      offset: int
      limit: int
      has_more: bool
    (HealthScan)
      things: list[{reference_id: str, prefab_name: str | None, display_name: str | None, custom_name: str | None, build_state: {current: int, last:
          int, complete: bool, next: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] | None} | None, condition: str,
          kind: str, type: str, damage_state: str, damage_state_class: str | None, max_damage: float | None, total_damage: float | None, damage_ratio:
          float | None, health_percent: int | None, band: str | None, damage: {brute: float, burn: float, oxygen: float, hydration: float, starvation:
          float, toxic: float, radiation: float, decay: float, stun: float} | None, is_broken: bool, broken_build_state: bool | None, being_destroyed:
          bool, pipe_burst: str | None, damage_record: list[str] | None, position: Vec3, distance_m: float | None, networks: list[{kind: str, id:
          str}] | None}]
      count: int
      total: int
      structures: int
      broken: int
      scanned: int
      min_damage_ratio: float
      offset: int
      limit: int
      has_more: bool
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
    Types: Error = {code: str, message: str, data: Any | None, see: {tool: str | None, topic: str, subtopic: str | None} | None}; Vec3 = {x: float, y:
        float, z: float}
    Example: sg.find('damaged', min_damage=0.25, structures_only=True)
    """
