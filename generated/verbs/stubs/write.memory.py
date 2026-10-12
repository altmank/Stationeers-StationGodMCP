def write(what: Literal['memory'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # One id, or "looking_at" (the thing the crosshair is on).
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    start: int,  # First address.
    values: list,  # Values written from start.
) -> dict:
    """Up to 512 values into a device's memory from start, as a chip's put does.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      gateway_id: str
      reference_id: str
      start_address: int
      requested_values: list[float]
      previous_values: list[float] | None
      current_values: list[float] | None
      count: int
      stack_size: int | None
    Example: sg.write('memory', id='4820585', start=10, values=[1, 0, 2])
    """
