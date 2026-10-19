def inspect(what: Literal['connections'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
) -> dict:  # main list: r['ends']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """One pipe, cable, chute or device: its ends and what each joins.
    A list of ids answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply below; fields name its keys.
    Each end: position is the end's own point; at is the centre of the cell a piece joining it stands in (as build reports a port's at) and piece_end the end that piece needs, pointing back into this thing: build pipe {piece: {at, ends: [piece_end, ...]}} joins it.
    Reply:
      thing: Thing
      position: Vec3
      rotation: {facing: str | None, up: str | None, euler: Vec3} | None
      controls_facing: str | None
      own_network: {kind: str, id: str} | None
      color: Color | None
      ends: list[{index: int, type: str, type_name: str | None, role: str, role_name: str | None, position: Vec3 | None, at: Vec3 | None, piece_end:
          str | None, network: {kind: str, id: str} | None, connected: list[Thing]}]
      count: int
    Types: Color = {index: int | None, name: str | None, is_default: bool}; Thing = {reference_id: str, prefab_name: str | None, display_name: str |
        None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('connections', id=['2514519', '3575779'])
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
