def read(what: Literal['memory'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # One id, or "looking_at" (the thing the crosshair is on).
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    start: int,  # First address.
    count: int,  # Values to read.
) -> dict:
    """Up to 512 values of any device's memory, as a chip's get reads them (a chip's stack is also in inspect chip status).
    Reply:
      gateway_id: str
      reference_id: str
      start_address: int
      values: list[float]
      count: int
      stack_size: int | None
    Example: sg.read('memory', id='4820585', start=0, count=32)
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
