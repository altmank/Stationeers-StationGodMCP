def move(what: Literal['player'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    player: str = None,  # The player to move, by name; default the player.
    at: list = None,  # Destination point.
    to: str = None,  # Destination: beside this thing (an id).
    to_player: str = None,  # Destination: beside this player (a name).
    safe_ground: bool = False,  # Drop the point onto the highest safe surface, 4 m above it.
    stand: dict = None,  # With to or at: stand the player within reach of it, clear of every body and on a floor: {reach_m: 2.5 (at most 3), clear_m: 0.1, floor: required|any, on: false (stand on top of it)}. The reply's stand names the floor; no_clear_stand counts why none passed. It does not turn the player.
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
      stand: {position: Vec3, distance_m: float, eye_to_target_m: float, floor: str, tried: int, rejected: list[{reason: str, count: int}]} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.move('player', to_player='xceled')
    """
