def write(what: Literal['screen'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # One id, or "looking_at" (the thing the crosshair is on).
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    surface: str = None,  # The surface; default the main screen's.
    element: str = None,  # The element to touch (inspect screen lists them).
    pixel: list = None,  # Or a point [x, y] in pixels from the top left.
    event: Literal['click', 'change', 'toggle'] = 'click',  # The touch: click, change (a slider or field) or toggle.
    text: str = None,  # With change: the new value as text.
    as_player: str = None,  # The name the handler receives; default the player's.
) -> dict:
    """Touch a control on a ScriptedScreens screen, as a player's touch does.
    One id only: a list is refused (x-list one: this write cannot be undone).
    Reply:
      holder: Thing
      surface: str
      element: {id: str, type: str, box: {x: float, y: float, w: float, h: float} | None, shown: bool, touch: bool, props: dict, style: dict | None}
      event: str
      value: str | None
      player: str
      accepted: bool
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('screen', id='4860097', element='start')
    """
