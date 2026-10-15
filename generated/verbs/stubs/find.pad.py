def find(what: Literal['pad'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:  # main list: r['pads']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Landing pads for traders and rockets (not power or teleport pads), by the game's checks: size, runway, which ships fit.
    No options.
    Reply:
      pads: list[{reference_id: str, prefab_name: str | None, display_name: str | None, position: Vec3, forward: str, is_network_center: bool,
          piece_count: int, extent: {x_tiles: int, z_tiles: int} | None, largest_square_tiles: int, runway_ok: bool | None, fits_by_ship:
          list[{shuttle_type: str, pad_size_tiles: list[int], runway_tiles: int, needs_threshold: bool, fits: bool}], contacts: list[{reference_id:
          str, name: str | None, shuttle_type: str, pad_size_tiles: list[int], fits: bool, can_land: bool, reason: str}]}]
      count: int
      total: int
      offset: int
      has_more: bool
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('pad')
    """
