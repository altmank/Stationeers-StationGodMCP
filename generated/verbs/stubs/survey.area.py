def survey(what: Literal['area'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    box: list = None,  # Two corners; at most 20000 2 m cells.
    room: str = None,  # A room id.
    sections: list[Literal['cells', 'pieces', 'devices', 'networks', 'network_visibility', 'doors']] = 'all',  # Only these parts. (default all)
    networks: bool = True,  # Include the networks of the pieces.
    include: list[Literal['refund', 'piece_cells', 'wall_planes']] = None,  # refund: each piece's removal refund; piece_cells: every small cell of a long piece; wall_planes: small marks its '.' cells on the cell's -x, -y and -z face planes with that face's 'w', 'g' or 'x' (door over window over wall).
    network: list[str] = None,  # Only pieces on these networks.
    kind: list[Literal['cable', 'pipe', 'chute']] = None,  # Only pieces of these kinds.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    cell_detail: Literal['occupancy', 'full'] = 'occupancy',  # occupancy: cells {at, empty, frame_id, faces, small}; full: cells {at, room_id, frame, walls, small, support} in place of those.
    occupied_only: bool = False,  # Only cells holding something.
    limit: int = 8,  # The first n in order (2 m cells); left out, every one.
    offset: int = 0,  # 2 m cells to skip: the page starts there; walk() and all() go on from it.
) -> dict:  # main list: r['cells']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """The 2 m cells of a box or room: what occupies each, the pieces, devices, ports and networks.
    A big area is many pages (1000 cells at most each); offset starts at a cell, and r['cells'].walk() goes through them all.
    Reply:
      legend: str | None
      cells: list[{at: Vec3, room_id: str | None, frame: {reference_id: str, prefab_name: str | None, build_state: int, build_states: int, blocks_air:
          bool, blocks_gravity: bool} | None, walls: list[{face: str, reference_id: str, prefab_name: str | None, blocks_air: bool, kind: str}],
          small: str | None, support: str | None} | {at: Vec3, empty: bool | None, frame_id: str | None, faces: str | None, small: str | None}] | None
      count: int
      offset: int
      limit: int
      total: int
      has_more: bool
      pieces: list[{reference_id: str, kind: str, prefab_name: str | None, at: Vec3, cell_count: int | None, cell_box: {min: {x: float, y: float, z:
          float}, max: {x: float, y: float, z: float}, size: {x: float, y: float, z: float}} | None, cells: list[Vec3] | None, ends: list[str],
          network_id: str | None, grade: str | None, flow: {in: list[str], out: list[str]} | None, carries: Thing | None, refund: list[{prefab_name:
          str | None, quantity: int}] | None}] | None
      devices: list[{reference_id: str, prefab_name: str | None, display_name: str | None, at: Vec3, rotation: {facing: str | None, up: str | None,
          euler: Vec3} | None, ports: list[{index: int, at: Vec3, toward: str, type: str, role: str, network_id: str | None}], umbilical: {role: str,
          open: bool, partner: Thing | None, partner_distance: int, search: {found: str | None, partner_distance: int, how: str, stops: list[{column:
          int, step: int, reason: str}] | None}} | None}] | None
      networks: list[Any] | None
      network_visibility: list[{network_id: str | None, kind: str, pieces: int, cells: {inside: int, frame_surface: int, wall: int, air: int}, air_at:
          list[Vec3] | None, refund: list[{prefab_name: str | None, quantity: int}] | None}] | None
      doors: list[{reference_id: str, prefab_name: str | None, display_name: str | None, faces: list[Vec3], plane: str, band_m: float, port_cells:
          list[Vec3]}] | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.survey('area', room='17', sections=['pieces', 'devices'])
    """
