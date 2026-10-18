def inspect(what: Literal['health'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Up to 256 things.
    network: str | dict = None,  # A network: its id, a piece or device on it, or {id, port}.
    kind: Literal['pipe', 'cable', 'chute'] = 'pipe',  # The network's kind.
    damaged_only: bool = False,  # Network: only damaged and broken pieces.
    limit: int = 8,  # The first n in order (entries); left out, every one.
) -> dict:  # main list: r['things']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Damage state of things, or of every piece of a network, worst first.
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
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('health', network='5012345', kind='pipe', damaged_only=True)
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
