def inspect(what: Literal['controllers'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The device(s).
) -> dict:
    """What can write or override a device: IC chips (pins, batch writers on its data network), logic writers, Lua screen scripts, other runs.
    Reply:
      reference_id: str
      display_name: str | None
      controllers: list[{reference_id: str, display_name: str | None, kind: str, how: str, logic_types: list[str], line: int | None, on: bool | None}]
      count: int
    Example: sg.inspect('controllers', id='4936423')
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
