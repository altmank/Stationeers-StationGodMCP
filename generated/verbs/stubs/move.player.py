def move(what: Literal['player'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    player: str = None,  # The player to move, by name; default the player.
    at: list = None,  # Destination point.
    to: str = None,  # Destination: beside this thing (an id).
    to_player: str = None,  # Destination: beside this player (a name).
    safe_ground: bool = False,  # Drop the point onto the highest safe surface, 4 m above it.
) -> dict:
    """Move a player anywhere at once: to a point, beside a thing or another player.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      player: Thing
      moved_by: str
      before: {position: Vec3, room_id: str | None, room_type: str | None, outside: bool | None}
      after: {position: Vec3, room_id: str | None, room_type: str | None, outside: bool | None}
      destination: {form: str, next_to: Thing | None, safe_ground: bool | None}
      distance_m: float
      note: str | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.move('player', to_player='xceled')
    """
