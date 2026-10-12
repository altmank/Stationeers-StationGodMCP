def plan(what: Literal['spot'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    prefab: str | int,  # Whole prefab name or hash.
    near: str | list = 'looking_at',  # [x, y, z], "player", "looking_at" (the crosshair) or an id.
    plane: str = None,  # A face plane, "z=668".
    side: Literal['+x', '-x', '+y', '-y', '+z', '-z'] = None,  # The side you view the plane from.
    looking: bool = None,  # Search the face your look ray hits.
    around: list = None,  # A point near the plane, to pick the side in a room.
    room: str = None,  # Search this room's walls.
    facing: Literal['+x', '-x', '+y', '-y', '+z', '-z'] = None,  # Try every spot with this facing.
    radius: float = 4,  # Search radius.
    require: dict = None,  # {one_section, min_bottom_above_floor_m, ports_reachable, avoid_doors, front_clear_m, no_visual_overlap}.
    limit: int = 5,  # The first n in order (spots returned); left out, every one.
    max_checks: int = 40,  # Spots checked with the cursor.
) -> dict:  # main list: r['spots']; iterating or indexing it walks every page, in order
    """Free places to put one prefab you are about to build, near a point, on a face plane or a room's walls, checked by the game's cursor; answers build_arguments.
    Reply:
      prefab_name: str | None
      planes: list[str]
      spots: list[{position: {x: float, y: float, z: float}, orientation: {facing: str | None, up: str | None, euler: Vec3}, distance_m: float,
          penalty: int, plane: str, conflicts: list[{code: str, level: str, message: str, reference_id: str | None}], place_arguments: Any}]
      tried: int
      filtered: int
      checked: int
      rejected: int
      reasons: list[{reason: str, count: int}]
      view: {source: str, age_s: float} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.plan('spot', prefab='StructureWallLight', looking=True)
    """
