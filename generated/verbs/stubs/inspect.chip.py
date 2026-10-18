def inspect(what: Literal['chip'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The circuit holder, worn holder, Lua board or the chip.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    parts: list[Literal['status', 'source', 'selectors']] = None,  # Parts: status, source, selectors. (default [status]) (default [status])
    start: int = 0,  # status: first stack address.
    count: int = 64,  # status: stack values; 0 for none.
    log: int = 5,  # status: last Lua log lines; 0 for none.
    targets: list[str] = None,  # selectors: devices to evaluate; default those on the holder's data network.
) -> dict:  # main list: r['pins']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A circuit holder's chip: status (line, registers, stack, pins, errors, Lua log), source, selectors. Any device's memory: read memory.
    One part answers that part's reply as it is; several answer {status, source, selectors}.
    A source is often longer than the reply cap: add whole: True to read it all.
    Reply, one of:
    (IcNoChip)
      gateway_id: str
      reference_id: str
      pins: list[{index: int, reference_id: str | None, prefab_name: str | None, display_name: str | None, label: str | None, reachable: bool}]
      holder: Thing
    (IcStatus)
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
      housing: {kind: str, on: bool, powered: bool, operable: bool}
      pins: list[{index: int, reference_id: str | None, prefab_name: str | None, display_name: str | None, label: str | None, reachable: bool}]
      runtime: {execution: {next_address: int, current_line: str | None, paused: bool}, registers: list[{index: int, aliases: list[str], value:
          float}], register_count: int, stack_pointer_register: int, stack_pointer: float | None, return_address_register: int, return_address: float
          | None, stack: {size: int, start_address: int, values: list[float], count: int}, aliases: list[{name: str | None, target: str | None, index:
          int | None}], defines: list[{name: str | None, value: float}], jump_tags: list[{name: str | None, value: int}]} | None
      language: str
      holder: Thing
      chip: Thing
      source_length: int
      lua: {compiling: bool, has_runtime: bool, init_complete: bool, library: bool, source_version: int | None, last_error: {kind: str, line: int,
          message: str, traceback: str | None} | None, log: {lines: list[str], line_count: int} | None, unavailable: str | None} | None
    (IcSource)
      gateway_id: str
      reference_id: str
      source: str | None
      line_number: float
      language: str
      holder: Thing
      chip: Thing
      source_length: int
      lua: {compiling: bool, has_runtime: bool, init_complete: bool, library: bool, source_version: int | None, last_error: {kind: str, line: int,
          message: str, traceback: str | None} | None, log: {lines: list[str], line_count: int} | None, unavailable: str | None} | None
    (IcSelectors)
      gateway_id: str
      reference_id: str
      db: {reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str | None, slot_count: int,
          is_gateway: bool, worn_by: str | None, worn_slot: str | None}
      pins: list[{selector: str, connected: bool, target: {reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None,
          runtime_type: str | None, slot_count: int, is_gateway: bool, worn_by: str | None, worn_slot: str | None} | None}]
      aliases: list[{name: str | None, target: str | None, index: int | None}]
      stable_selectors: list[{reference_id: str, display_name: str | None, prefab_name: str | None, prefab_hash: int, name_hash: int | None,
          reachable: bool, collision_count: int, ic10_example: str | None}]
      stable_selector_count: int
      batch_device_count: int | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.inspect('chip', id='4820585', parts=['status', 'source'])
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
