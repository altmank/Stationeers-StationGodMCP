def read(what: Literal['logic'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Device(s); with logic, every id is read for every logic type.
    logic: str | int | list = None,  # Logic type(s): name (Temperature) or number.
    reads: list = None,  # Separate reads, each {id, logic, slots: [{index, logic}], atmosphere: {of, port}, reagents: True}: up to 256 pairs, or 128 per-item reads.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    include: list[Literal['clock']] = None,  # clock: the game clock in the same frame.
) -> dict:  # main list: r['results']; iterating or indexing it walks every page, in order
    """Logic values of devices, by type name or number; also slot logic, atmosphere and reagents in one frame.
    One rule for the reply: one id and one logic type answer {reference_id, logic_type {id, name}, value}. id and logic lists, or reads whose every entry is a pair, answer one row per (id, logic) pair, index counting the pairs: results [{index, ok, reference_id, logic_type, value} or {index, ok, reference_id, error}], up to 256. Any entry with a logic list, slots, atmosphere or reagents, or include, answers one row per item in one frame: results [{index, ok, reference_id, logic {Type: value}, logic_errors, slots, atmosphere, reagents, errors} or {index, ok, reference_id, error}], up to 128 items and 1,024 values; more is refused, naming the bound.
    The two list forms: id: [a, b], logic: ["On", "Power"] answers 4 rows, one per (id, logic) pair: {index, ok, reference_id, logic_type {id, name}, value}. reads: [{id: a, logic: ["On", "Power"]}, {id: b, logic: ["On", "Power"]}] answers 2 rows, one per id: {index, ok, reference_id, logic {On: 1, Power: 40.2}}.
    Reply, one of:
    (LogicRead)
      gateway_id: str
      reference_id: str
      logic_type: LogicType
      value: float
    (LogicBatch)
      gateway_id: str
      results: list[{index: int, ok: bool, reference_id: str | None, logic_type: LogicType | None, error: Error} | {index: int, ok: bool,
          reference_id: str, logic_type: LogicType, value: float} | {index: int, ok: bool, reference_id: str, logic_type: LogicType, requested_value:
          float, previous_value: float | None, current_value: float | None} | {index: int, ok: bool, error: Error}]
      count: int
      success_count: int
      error_count: int
    (ReadDevices)
      gateway_id: str
      clock: {game_time_s: float, paused: bool, time_of_day_ratio: float, days_past: int} | None
      count: int
      success_count: int
      error_count: int
      results: list[{index: int, ok: bool, reference_id: str, error: Error} | {index: int, ok: bool, reference_id: str, logic: dict[str, float] |
          None, logic_errors: dict[str, Error] | None, slots: list[{index: int, logic: dict[str, float] | None, logic_errors: dict[str, Error] | None,
          error: Error | None}] | None, atmosphere: {source: str, atmosphere_id: str, network_id: str | None, volume_l: float, pressure_kpa: float,
          temperature_k: float, total_mol: float, liquid_volume_l: float, contents: list[{gas: str, state: str, amount_mol: float, liquid_l: float |
          None}]} | None, reagents: {total: float, reagents: list[{reagent: str, name: str | None, quantity: float, unit: str | None}]} | None,
          errors: dict[str, Error] | None} | {index: int, ok: bool, error: Error}]
    Types: Error = {code: str, message: str, data: Any | None, see: {tool: str | None, topic: str, subtopic: str | None} | None}; LogicType = {id:
        int, name: str | None}
    Example: sg.read('logic', id=['1199811', '1199812'], logic=['Pressure', 'Temperature'])
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
