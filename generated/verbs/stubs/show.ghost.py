def show(what: Literal['ghost'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    placements: list = None,  # As build structure's placements; each may give its own tint and label.
    prefab: str | int = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    at: list | dict | str = None,  # A point, "looking_at", or a relative or look-face form (help positions).
    rotation: list = None,  # [x, y, z] degrees, multiples of 90.
    facing: str = None,  # Where the front points (as build structure).
    up: str = None,  # Where the top points (as build structure).
    face: str = None,  # Face-placed pieces: the cell face it sits on.
    orient: dict = None,  # {mount, upright, controls_toward, ports: [{role, index, type, toward}], flow: {from, to}}: choose the turn by intent. mount: wall, floor, ceiling or an axis; a target (controls_toward, toward, from, to): an axis, room, player, a point or a thing's id.
    above_floor: float = None,  # Bottom this many metres above the floor below at.
    build_state: str | int = None,  # finished (default), first, or a state index.
    from_plan: str = None,  # A plan spot reply (p = sg.plan('spot', ...)) or one spot (p['spots'][2]): put it on the board.
    tint: str | list = None,  # The ghosts' colour, yours to choose: a name (cyan, magenta, yellow, green, orange, blue, red, white, purple, pink, khaki, brown, black, gray), [r, g, b] or [r, g, b, a] each 0 to 1.
    label: str = None,  # What the ghosts are, shown as a tooltip when the crosshair is on one, and in find ghost; a placement's own label wins.
    remove: list = None,  # Ghost ids to take off the board (find ghost lists them).
    clear: Literal['agent', 'all'] = None,  # Take off every ghost agents placed, or every ghost.
    allow: list[Literal['door_keepout']] = None,  # Guards to lift, by name.
) -> dict:
    """Put planned structures on the world's ghost board, where every player sees them until they are built or taken off; or take ghosts off.
    A ghost lasts until it is taken off or a structure of its prefab is built where it stands, kept with the save; players see it, move around it, take it off (RemoveGhostKey) and place their own (ghost mode, the ghost menu). The placements are planned as build structure plans them (nothing built, materials not checked) and each goes on the board where the plan put it, problems or not; the reply's plan says why one would not build, and gives each placement's position, orientation, and a device's ports and layout.port_checks as build structure's report does (no build call needed to plan runs to them). find ghost lists the board. With Lattice installed the ghosts are Lattice's: the agent's go into a Lattice plan of their own (StationGod agent), Lattice saves and syncs them and Loom shows them; board says which.
    Reply:
      placed: list[{id: int, prefab_name: str, position: Vec3}]
      removed: list[int]
      total: int
      board: str
      plan: {ready: bool, problems: list[{code: str, message: str, index: int | None, reference_id: str | None}], warnings: list[{code: str, message:
          str, index: int | None, reference_id: str | None}], placements: list[{index: int, prefab_name: str | None, position: Vec3 | None,
          orientation: {facing: str | None, up: str | None, euler: Vec3} | None, build_state: int | None, ports: list[{index: int, at: Vec3, toward:
          str, type: str, role: str, network_id: str | None}] | None, layout: {port_checks: list[{index: int, at: Vec3, toward: str, type: str, role:
          str, flow: str | None, occupant: Thing | None, joins: bool, would_join_network: str | None, blocked: str | None, in_door_keep_out: bool}]} |
          None}]} | None
      notes: list[str]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.show('ghost', prefab='StructureVolumePump', at=[716, 203, 668], facing='+x', tint='orange', label='second pump')
    """
