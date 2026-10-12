def read(what: Literal['sample'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
    logic: str | int | list = None,  # Logic type(s) to watch on every id: name or number.
    reads: list = None,  # Separate {id, logic} pairs.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    seconds: float = 5,  # How long to watch.
    interval: float = 0.5,  # Least time between samples (s).
) -> dict:
    """Watch up to 32 logic values for up to 30 s: first readings and every change with its time.
    A list of ids keeps this reply: one series of changes over all of them (x-list native-shape).
    Reply:
      gateway_id: str
      started_at_utc: str
      duration_seconds: float
      interval_seconds: float
      sample_count: int
      change_count: int
      changes: list[{reference_id: str, logic_type: str, value: float, at_s: float}]
    Example: sg.read('sample', id='1199811', logic='Pressure', seconds=10)
    """
