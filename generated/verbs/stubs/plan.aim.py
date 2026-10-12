def plan(what: Literal['aim'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The solar panel or the satellite dish; a list answers one entry each.
    contact: str = None,  # Dish only: the trader contact.
) -> dict:
    """Horizontal and Vertical that point a solar panel at the sun, or a satellite dish at a trader contact.
    A list of ids answers {results: [{index, ok, reference_id, ...the reply below} or {index, ok, reference_id, error}], count, success_count, error_count}; fields paths are results.<key>.
    Write the answer with write logic (Horizontal, Vertical).
    Reply, one of:
    (SolarFixed)
      reference_id: str
      prefab_name: str | None
      can_turn: bool
      operable: bool
      sun: {x: float, y: float, z: float, above_horizon: bool, eclipse: bool}
      current: {horizontal: float, vertical: float, ratio: float}
    (SolarTurn)
      reference_id: str
      prefab_name: str | None
      can_turn: bool
      operable: bool
      sun: {x: float, y: float, z: float, above_horizon: bool, eclipse: bool}
      current: {horizontal: float, vertical: float, ratio: float}
      horizontal: float
      vertical: float
      off_sun_deg: float
      alignment_ratio: float
    (DishAim)
      dish_id: str
      contact_id: str
      finished: bool
      powered: bool
      on: bool
      can_rotate: bool
      horizontal: float
      vertical: float
      error_deg: float
      pointing: list[float]
      target: list[float]
      current: {horizontal: float, vertical: float, forward: list[float], error_deg: float | None}
      stale_pose_deg: float
      samples: int
    Example: sg.plan('aim', id='3300111')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
