def find(what: Literal['thing'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    name: str = None,  # Part of the shown or game name, any case.
    prefab: str | int | list = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    category: Literal['any', 'item', 'dynamic', 'structure', 'entity', 'other'] = 'any',  # Only this kind of thing.
    labelled: bool = False,  # Only things with a Labeller name.
    runtime_type: str = None,  # Only things of this game class or one built on it ('Device', 'Pipe').
    broken: bool = None,  # True: only broken things; False: only whole ones.
    color: str = None,  # Only things showing this colour: a name or an index, as write paint takes it.
    painted: bool = None,  # True: only paintable things painted off their own colour; False: only those still in it.
    has_atmosphere: bool = None,  # True: only things holding gas or liquid; False: only without.
    made_by: str = None,  # Only items the print log has from this maker (id, or part of its name).
    made_since: float = None,  # Only items made at or after this game time (s); negative: that many seconds back.
    location: Literal['any', 'ground', 'player', 'stored', 'built', 'world'] = 'any',  # ground, player, stored, built or world (players, animals).
    near: str | list = None,  # Centre: [x, y, z], "player", "looking_at" or a thing's id (its position); with radius.
    radius: float = None,  # Metres around near; at most 1000 around a point, no limit around "player".
    box: list = None,  # Two corners [[x, y, z], [x, y, z]].
    limit: int = 8,  # The first n in order (entries); left out, every one.
    offset: int = 0,  # Entries to skip: the page starts there; walk() and all() go on from it.
    order: Literal['nearest', 'id'] = 'nearest',  # nearest (default) or id (stable pages).
) -> dict:  # main list: r['things']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Any object: structures, devices, tanks, loose items, players, animals.
    Reply:
      things: list[{made: {maker_id: str | None, maker_prefab: str | None, maker_name: str | None, game_time_s: float, quantity: int, split_from: str
          | None} | None, reference_id: str, prefab_name: str | None, display_name: str | None, custom_name: str | None, quantity: float | None,
          game_name: str, kind: str, runtime_type: str, labelable: bool, location: str, carried_by: str | None, held_in: list[{reference_id: str,
          prefab_name: str | None, display_name: str | None, slot_index: int, slot_name: str | None}], position: Vec3, distance_m: float | None,
          is_device: bool, has_atmosphere: bool, is_broken: bool, condition: str, rotation: {facing: str | None, up: str | None, euler: Vec3} | None,
          controls_facing: str | None, rocket_state: str | None, color: Color | None, build_state: {current: int, last: int, complete: bool, next:
          list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] | None} | None}]
      count: int
      total: int
      offset: int
      limit: int
      has_more: bool
      scanned: int
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
    Types: Color = {index: int | None, name: str | None, is_default: bool}; Vec3 = {x: float, y: float, z: float}
    Example: sg.find('thing', prefab='*Battery*', near='player', radius=30)
    """
