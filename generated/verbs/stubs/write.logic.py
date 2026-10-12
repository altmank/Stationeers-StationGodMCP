def write(what: Literal['logic'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Device(s); every one gets logic = value.
    logic: str | int = None,  # Logic type name or number.
    value: float = None,  # A finite number.
    writes: list = None,  # Separate writes, each {id, logic, value}, applied in order.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    allow: list[Literal['lint']] = None,  # lint: a write that turns on or opens a vent, valve or pump runs the network lint rules (no oxidiser vented outdoors, fuel mix temperature) on the network it opens to, and is refused on a problem unless lint is allowed (passed lists it).
) -> dict:  # main list: r['results']; iterating or indexing it walks every page, in order
    """Logic values on devices, as a chip or console writes them; answers previous and read-back values.
    id or writes is required (missing_target names them); gateway only narrows where ids are looked up. To write every device of a kind, find them and pass their ids as a list (in a confirmed run, ids a find returned that you have not seen make the run a report before anything acts, and stop it after: report, then repeat).
    Reply, one of:
    (LogicWrite)
      gateway_id: str
      reference_id: str
      logic_type: LogicType
      requested_value: float
      previous_value: float | None
      current_value: float | None
    (LogicBatch)
      gateway_id: str
      results: list[{index: int, ok: bool, reference_id: str | None, logic_type: LogicType | None, error: Error} | {index: int, ok: bool,
          reference_id: str, logic_type: LogicType, value: float} | {index: int, ok: bool, reference_id: str, logic_type: LogicType, requested_value:
          float, previous_value: float | None, current_value: float | None} | {index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    Types: Error = {code: str, message: str, data: Any | None, see: {tool: str | None, topic: str, subtopic: str | None} | None}; LogicType = {id:
        int, name: str | None}
    Example: sg.write('logic', id='4820585', logic='On', value=1)
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
