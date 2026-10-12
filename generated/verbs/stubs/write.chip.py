def write(what: Literal['chip'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # One id, or "looking_at" (the thing the crosshair is on).
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    code: str = None,  # The complete program: IC10, or Lua for a Lua chip.
    code_file: str = None,  # A file path on the MCP server's machine whose text is sent as code.
    pins: dict = None,  # {d0..d5: device id or None}; pins left out keep their device.
    allow: list[Literal['off_network']] = None,  # off_network: a pin device not on the housing's data network.
    run: Literal['pause', 'step', 'resume', 'restart'] = None,  # pause, step, resume (IC10) or restart (Lua).
    include: list[Literal['code']] = None,  # code: echo the program as stored.
) -> dict:
    """A circuit holder's chip: write source (IC10 or Lua), set pins d0-d5, or pause, step, resume, restart.
    Given together, they apply in this order: code, pins, run. The reply is {code, pins, run} with the parts given; the first failure stops the rest, named in not_run.
    One id only: a list is refused (x-list one: this write cannot be undone).
    Reply, one of:
    (IcSourceSet)
      gateway_id: str
      reference_id: str
      source: str | None
      line_number: float
      compilation_error: bool
      error_line: str | None
      error_type: str | None
      error_code: str | None
      compile_error_line: int | None
      compile_error_type: str | None
      language: str
      holder: Thing
      chip: Thing
      source_length: int
      lua: {compiling: bool, has_runtime: bool, init_complete: bool, library: bool, source_version: int | None, last_error: {kind: str, line: int,
          message: str, traceback: str | None} | None, log: {lines: list[str], line_count: int} | None, unavailable: str | None} | None
      warnings: list[{code: str, message: str}]
    (SetIcPins)
      gateway_id: str
      reference_id: str
      has_data_network: bool
      changes: list[{index: int, previous_reference_id: str | None, reference_id: str | None, changed: bool}]
      pins: list[{index: int, reference_id: str | None, prefab_name: str | None, display_name: str | None, label: str | None, reachable: bool}]
    (IcControl)
      gateway_id: str
      reference_id: str
      action: str
      paused: bool
      previous_line: float
      line_number: float
      compilation_error: bool
      error_line: str | None
      error_type: str | None
      error_code: str | None
      compile_error_line: int | None
      compile_error_type: str | None
      language: str
      holder: Thing
      chip: Thing
      lua: {compiling: bool, has_runtime: bool, init_complete: bool, library: bool, source_version: int | None, last_error: {kind: str, line: int,
          message: str, traceback: str | None} | None, log: {lines: list[str], line_count: int} | None, unavailable: str | None} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('chip', id='4820585', code_file='C:/work/ctl.ic10')
    """
