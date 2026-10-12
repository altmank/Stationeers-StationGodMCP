def inspect(what: Literal['connections'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
) -> dict:  # main list: r['ends']; iterating or indexing it walks every page, in order
    """One pipe, cable, chute or device: its ends and what each joins.
    A list of ids answers {results: [{index, ok, reference_id, ...the reply below} or {index, ok, reference_id, error}], count, success_count, error_count}; fields paths are results.<key>.
    Reply, one of:
    (Connections)
      thing: Thing
      position: Vec3
      rotation: {facing: str | None, up: str | None, euler: Vec3} | None
      own_network: {kind: str, id: str} | None
      color: Color | None
      ends: list[{index: int, type: str, type_name: str | None, role: str, role_name: str | None, position: Vec3 | None, network: {kind: str, id: str}
          | None, connected: list[Thing]}]
      count: int
    (NetworkMembers)
      network: {kind: str, id: str}
      summary: Any | None
      warning: str | None
      members: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, network_id: str | None}]
      count: int
      structure_count: int
      device_count: int
      offset: int
      limit: int
      total: int
      has_more: bool
    (NetworkOverview)
      network: {kind: str, id: str}
      summary: Any | None
      warning: str | None
      broken_neighbour_count: int
      broken_neighbours: list[{reference_id: str, prefab_name: str | None, position: Vec3, pipe_burst: str | None, network: {kind: str, id: str} |
          None, touches: str}] | None
      structure_count: int
      device_count: int
      by_prefab: list[{prefab_name: str, display_name: str | None, member: str, count: int, colors: dict[str, int] | None}]
      devices: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, network_id: str | None}]
      open_ends: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, network_id: str | None}]
      open_end_count: int
    (AreaOpenEnds)
      members: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, network_id: str | None}]
      count: int
      offset: int
      limit: int
      total: int
      has_more: bool
    Types: Color = {index: int | None, name: str | None, is_default: bool}; Thing = {reference_id: str, prefab_name: str | None, display_name: str |
        None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('connections', id=['2514519', '3575779'])
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
