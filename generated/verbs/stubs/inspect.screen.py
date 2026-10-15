def inspect(what: Literal['screen'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # One id, or "looking_at" (the thing the crosshair is on).
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    surface: str = None,  # The surface; default the main screen's.
    type: str = None,  # Only elements of this type (button, label, gauge...).
    text: str = None,  # Only elements whose text, label, title or value holds this.
    shown_only: bool = True,  # Leave out hidden elements.
    include: list[Literal['style', 'image']] = None,  # style: colours and fonts; image: a PNG (not on a dedicated server).
    limit: int = 25,  # The first n in order (entries); left out, every one.
) -> dict:  # main list: r['elements']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """What a ScriptedScreens screen shows: surfaces and one surface's elements.
    Reply:
      holder: Thing
      chip_id: str
      surfaces: list[{name: str, element_count: int, shown_on: list[int]}]
      surface: str
      width: float | None
      height: float | None
      elements: list[{id: str, type: str, box: {x: float, y: float, w: float, h: float} | None, shown: bool, touch: bool, props: dict, style: dict |
          None}]
      count: int
      total: int
      offset: int
      limit: int
      has_more: bool
      image: {path: str, surface: str, width: int, height: int} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.inspect('screen', id='4860097', text='error')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
