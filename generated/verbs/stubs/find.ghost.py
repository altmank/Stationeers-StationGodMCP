def find(what: Literal['ghost'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    ids: list = None,  # Only these ghosts, by id.
    near: str | list = None,  # Only ghosts near this: [x, y, z], "player", "looking_at" or a thing's id; nearest first.
    radius: float = None,  # Metres from near; left out, every ghost, nearest first.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    owner: str = None,  # Only ghosts this player placed (their name, any case), or "agent".
    source: Literal['player', 'agent'] = None,  # Only ghosts players placed, or agents.
    check: bool = False,  # With Lattice: each listed ghost's check by Lattice's validation (passes, waits for other ghosts, fails or warns, and why), in build order with the ghosts before it assumed built.
    bill: bool = False,  # With Lattice: what the listed ghosts take to build (items needed, given back, tools).
    limit: int = 50,  # The first n in order (entries); left out, every one.
) -> dict:  # main list: r['ghosts']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Ghosts: structures players (ghost mode, the ghost menu) or agents (show ghost) planned in the world but did not build, with build_arguments to build them.
    Each ghost carries its orientation, a device's ports and layout.port_checks (what stands in each joining cell now; other ghosts are not seen there) and build_arguments for build structure; the reply's build_arguments place every listed ghost (up to 64) in one build. from_plan takes a find ghost ref: "r5" builds the listed ghosts, "r5.ghosts.2" one. A ghost goes off the board by itself once a structure of its prefab is built where it stands in its turn; show ghost {remove} takes ghosts off.
    board says who keeps the ghosts: lattice (Lattice is installed: its build ghosts, from every mod and player, Loom's included, saved and synced by Lattice; ids are Lattice's) or stationgod (StationGod's own board, saved beside the save). Lattice's remove, swap and finish ghosts are not listed. check needs Lattice; while Lattice is still working a check out, check_note says so: ask again.
    Reply:
      ghosts: list[{id: int, prefab_name: str, display_name: str | None, position: Vec3, rotation_deg: Vec3, build_state: int, owner: str, source:
          str, label: str | None, tint: list[float], placed_at: str, distance_m: float | None, orientation: {facing: str | None, up: str | None,
          euler: Vec3}, ports: list[{index: int, at: Vec3, toward: str, type: str, role: str, network_id: str | None}] | None, layout: {port_checks:
          list[{index: int, at: Vec3, toward: str, type: str, role: str, flow: str | None, occupant: Thing | None, joins: bool, would_join_network:
          str | None, blocked: str | None, in_door_keep_out: bool}]} | None, check: {verdict: str, why: str | None, waits_for: list[int] | None} |
          None, build_arguments: {what: str, prefab: str, at: list[float], rotation: list[float], build_state: int}}]
      count: int
      total: int
      offset: int
      limit: int | None
      has_more: bool
      build_arguments: {what: str, placements: list[{prefab: str, at: list[float], rotation: list[float], build_state: int}]} | None
      board: str
      bill: {needed: list[{prefab_name: str | None, quantity: int}], given_back: list[{prefab_name: str | None, quantity: int}], tools: list[str |
          None]} | None
      check_note: str | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.find('ghost', near='player', radius=20)
    """
