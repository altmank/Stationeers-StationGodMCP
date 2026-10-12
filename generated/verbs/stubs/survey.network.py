def survey(what: Literal['network'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    network: str | dict = None,  # A network: its id, a piece or device on it, or {id, port}.
    box: list = None,  # Box form: the pieces in it (alone: open ends in it).
    kind: Literal['pipe', 'cable', 'chute'] = None,  # Box form: only this kind. With network, left out, it is read from the network.
    prefab: str | int | list = None,  # Only members whose prefab holds this part.
    summarize: bool = False,  # Counts by prefab and colour, devices and open ends instead of members.
    open_ends: bool = False,  # Only members with an end nothing is attached at.
    near: str | list = None,  # Centre: [x, y, z], "player", "looking_at" or a thing's id (its position); with radius.
    radius: float = None,  # Metres around near.
    limit: int = None,  # The first n in order (members); left out, every one. (default 30; box 25; summarize 10)
) -> dict:  # main list: r['members']; iterating or indexing it walks every page, in order
    """A cable, pipe or chute network's members (or the pieces in a box), with filters or a summary.
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
    Example: sg.survey('network', network='3768865', kind='cable', summarize=True)
    """
