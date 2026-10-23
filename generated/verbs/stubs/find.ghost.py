def find(what: Literal['ghost'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    ids: list = None,  # Only these ghosts, by id.
    near: str | list = None,  # Only ghosts near this: [x, y, z], "player", "looking_at" or a thing's id; nearest first.
    radius: float = None,  # Metres from near; left out, every ghost, nearest first.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    owner: str = None,  # Only ghosts this player placed (their name, any case), or "agent".
    source: Literal['player', 'agent'] = None,  # Only ghosts players placed, or agents.
    limit: int = 50,  # The first n in order (entries); left out, every one.
) -> dict:  # main list: r['ghosts']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Ghosts: structures players (ghost mode, the ghost menu) or agents (show ghost) planned in the world but did not build, with build_arguments to build them.
    Each ghost carries build_arguments for build structure; the reply's build_arguments place every listed ghost (up to 64) in one build. from_plan takes a find ghost ref: "r5" builds the listed ghosts, "r5.ghosts.2" one. A ghost goes off the board by itself once a structure of its prefab is built where it stands in its turn; show ghost {remove} takes ghosts off.
    Reply:
      ghosts: list[{id: int, prefab_name: str, display_name: str | None, position: Vec3, rotation_deg: Vec3, build_state: int, owner: str, source:
          str, label: str | None, tint: list[float], placed_at: str, distance_m: float | None, build_arguments: {what: str, prefab: str, at:
          list[float], rotation: list[float], build_state: int}}]
      count: int
      total: int
      offset: int
      limit: int | None
      has_more: bool
      build_arguments: {what: str, placements: list[{prefab: str, at: list[float], rotation: list[float], build_state: int}]} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('ghost', near='player', radius=20)
    """
