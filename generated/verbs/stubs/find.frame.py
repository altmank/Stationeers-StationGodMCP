def find(what: Literal['frame'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    near: str | list = None,  # "player" only.
    radius: float = None,  # Metres around near.
    include: list[Literal['inner']] = None,  # inner: list inner frames too.
    limit: int = 20,  # The first n in order (entries); left out, every one.
    order: Literal['nearest', 'id'] = 'nearest',  # nearest (default) or id (stable pages).
) -> dict:  # main list: r['frames']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Outer frames (a face on outside air), with exposed faces and airtightness.
    Reply:
      frames: list[{reference_id: str, prefab_name: str | None, display_name: str | None, position: Vec3, distance_m: float | None, exposed_faces:
          list[str], exposed_face_count: int, blocks_air: bool, build_state: int, color: {index: int | None, name: str | None}}]
      count: int
      total_frames: int
      total_outer: int
      offset: int
      limit: int
      total: int
      has_more: bool
      include_inner: bool
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('frame', near='player', radius=20)
    """
