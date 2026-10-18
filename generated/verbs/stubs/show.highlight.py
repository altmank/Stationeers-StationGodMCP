def show(what: Literal['highlight'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    targets: list = None,  # Up to 64, each one of {id (one or a list), network, at} with color, label, pulse.
    seconds: float = 60,  # How long.
    keep: bool = False,  # Keep what earlier calls drew.
    clear: bool = None,  # Only remove what is drawn.
) -> dict:
    """Show the player where things, a network or far points are, tinted and labelled.
    Reply:
      targets: list[{index: int, kind: str, color: str, label: str | None, pulse: bool, network_id: str | None, things: int | None, missing: list[str]
          | None, truncated: bool | None, at: Vec3 | None, distance_m: float | None, bearing_deg: float | None, compass: str | None, rise_m: float |
          None}]
      cleared: int
      seconds: float
      renderer: str
      notes: list[str]
      drawn_on: list[str] | None
      view: {source: str, age_s: float} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.show('highlight', targets=[{'id': '1199811', 'label': 'this one'}])
    """
