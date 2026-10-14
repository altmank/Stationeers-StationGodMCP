def write(what: Literal['paint'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Things to paint the one color.
    network: str | dict = None,  # A pipe, cable or chute network (its id, a piece on it, or a device with kind): paints its pieces.
    kind: Literal['pipe', 'cable', 'chute'] = None,  # With network given as a device: which of its networks.
    include: list[Literal['devices']] = None,  # devices: also paint the network's devices.
    color: str = None,  # A colour name, an index, or 'default'.
    items: list = None,  # {id, color} pairs, a colour each.
) -> dict:  # main list: r['colors']; iterating or indexing it walks every page, in order
    """Paint things as a spray can does; network: a pipe's id, or a device's with kind, paints that network's pieces; with no target, list the colours.
    Reply, one of:
    (PaintColors)
      colors: list[{index: int, name: str | None, paint_only: bool}]
      count: int
    (BatchResult)
      results: list[{index: int, ok: bool, reference_id: str, previous_color: Color, color: Color} | {index: int, ok: bool, reference_id: str, error:
          Error}]
      count: int
      success_count: int
      error_count: int
    Types: Color = {index: int | None, name: str | None, is_default: bool}; Error = {code: str, message: str, data: Any | None, see: {verb: str |
        None, what: str | None, topic: str | None, subtopic: str | None} | None}
    Example: sg.write('paint', network='4639870', kind='pipe', color='Red')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
