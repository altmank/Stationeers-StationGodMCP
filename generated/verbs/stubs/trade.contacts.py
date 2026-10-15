def trade(what: Literal['contacts'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:  # main list: r['contacts']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Every trader contact in the sky and every satellite dish with where it points.
    No options.
    Reply:
      game_time_s: float
      contacts: list[{reference_id: str, trader: str | None, display_name: str | None, shuttle: str, pad_size_tiles: list[int], direction:
          list[float], elevation_deg: float, min_power_to_resolve_w: float, min_power_to_contact_w: float, contacted: bool, time_left_s: float |
          None}]
      dishes: list[{reference_id: str, display_name: str | None, prefab_name: str | None, forward: list[float], transform_up: list[float] | None,
          horizontal: float, vertical: float, finished: bool, powered: bool, on: bool, can_rotate: bool, min_power_w: int, max_power_w: int,
          field_of_view_deg: float}]
    Example: sg.trade('contacts')
    """
