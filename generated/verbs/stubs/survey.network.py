def survey(what: Literal['network'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    network: str | dict = None,  # A network: its id, a piece or device on it, or {id, port}.
    box: list = None,  # Box form: the pieces in it (alone: open ends in it).
    kind: Literal['pipe', 'cable', 'chute'] = None,  # Box form: only this kind. With network, left out, it is read from the network.
    prefab: str | int | list = None,  # Only members whose prefab holds this part.
    summarize: bool = False,  # Counts by prefab and colour, devices and open ends instead of members; summary has a pipe network's volume_l, pressure_kpa, temperature_k, total_mol and gases.
    open_ends: bool = False,  # Only members with an end nothing is attached at. member says device (its open ends are free ports) or the network's kind (a piece); open_end_details names each open end's type, role, joining cell and direction.
    near: str | list = None,  # Centre: [x, y, z], "player", "looking_at" or a thing's id (its position); with radius.
    radius: float = None,  # Metres around near.
    limit: int = None,  # The first n in order (members); left out, every one. (default 30; box 25; summarize 10)
) -> dict:  # main list: r['members']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A cable, pipe or chute network's members (or the pieces in a box), with filters or a summary.
    Reply, one of:
    (NetworkMembers)
      network: {kind: str, id: str}
      summary: {required_w: float, potential_w: float, actual_w: float, shortfall_w: float, lowest_cable_max_w: float | None, lowest_fuse_break_w:
          float | None, overloaded: bool, fuse_overloaded: bool, cable_count: int, fuse_count: int} | {content: str, volume_l: float, pressure_kpa:
          float, temperature_k: float, total_mol: float, liquid_volume_l: float, gases: list[{gas: str, state: str, amount_mol: float}]} |
          {member_count: int} | None
      warning: str | None
      members: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, open_end_details: list[{index: int, type: str, role: str, at: Vec3 | None, toward: str | None}] | None,
          network_id: str | None}]
      count: int
      structure_count: int
      device_count: int
      offset: int
      limit: int
      total: int
      has_more: bool
    (NetworkOverview)
      network: {kind: str, id: str}
      summary: {required_w: float, potential_w: float, actual_w: float, shortfall_w: float, lowest_cable_max_w: float | None, lowest_fuse_break_w:
          float | None, overloaded: bool, fuse_overloaded: bool, cable_count: int, fuse_count: int} | {content: str, volume_l: float, pressure_kpa:
          float, temperature_k: float, total_mol: float, liquid_volume_l: float, gases: list[{gas: str, state: str, amount_mol: float}]} |
          {member_count: int} | None
      warning: str | None
      broken_neighbour_count: int
      broken_neighbours: list[{reference_id: str, prefab_name: str | None, position: Vec3, pipe_burst: str | None, network: {kind: str, id: str} |
          None, touches: str}] | None
      structure_count: int
      device_count: int
      by_prefab: list[{prefab_name: str, display_name: str | None, member: str, count: int, colors: dict[str, int] | None}]
      devices: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, open_end_details: list[{index: int, type: str, role: str, at: Vec3 | None, toward: str | None}] | None,
          network_id: str | None}]
      open_ends: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, open_end_details: list[{index: int, type: str, role: str, at: Vec3 | None, toward: str | None}] | None,
          network_id: str | None}]
      open_end_count: int
    (AreaOpenEnds)
      members: list[{reference_id: str, prefab_name: str | None, display_name: str | None, member: str, position: Vec3, color: Color | None,
          open_ends: list[int] | None, open_end_details: list[{index: int, type: str, role: str, at: Vec3 | None, toward: str | None}] | None,
          network_id: str | None}]
      count: int
      offset: int
      limit: int
      total: int
      has_more: bool
    Types: Color = {index: int | None, name: str | None, is_default: bool}; Thing = {reference_id: str, prefab_name: str | None, display_name: str |
        None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.survey('network', network='3768865', kind='cable', summarize=True)
    """
