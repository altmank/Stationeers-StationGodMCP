def survey(what: Literal['wall'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    plane: str = None,  # A face plane, "x=716": an axis and an even metre.
    side: Literal['+x', '-x', '+y', '-y', '+z', '-z'] = None,  # The side you view from.
    looking: bool = None,  # Map the face your look ray hits instead of plane.
    at: list = None,  # The map's centre.
    half_width: float = 4,  # Half-width of the map in metres.
    free_rects: dict = None,  # {w, h, one_section, require_wall, limit}: where a w x h rectangle fits.
) -> dict:  # main list: r['things']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A text elevation of one 2 m face plane, 0.5 m per character; free_rects finds where a rectangle fits.
    Reply:
      plane: str
      side: str
      right: str
      up: str
      rows: list[str]
      top_left: {x: float, y: float, z: float}
      sections: list[{face: {x: float, y: float, z: float}, look: str, reference_id: str | None, prefab_name: str | None}]
      things: list[{key: str, reference_id: str, prefab_name: str | None, display_name: str | None, position: Vec3}]
      free_rects: list[{row: int, column: int, centre: {x: float, y: float, z: float}, cells_wide: int, cells_high: int}] | None
      legend: str
      view: {source: str, age_s: float} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.survey('wall', plane='x=716', side='-x', at=[716, 202, 668])
    """
