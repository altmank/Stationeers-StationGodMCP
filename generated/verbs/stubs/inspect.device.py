def inspect(what: Literal['device'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
    include: list[Literal['logic_types']] = None,  # logic_types: every logic type with id and access.
) -> dict:  # main list: r['logic_types']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A device: logic types it reads and writes, writable ones by name, rocket, umbilical and uplink details.
    A list of ids answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply below; fields name its keys.
    Reply:
      device: {reference_id: str, prefab_name: str | None, prefab_hash: int, display_name: str | None, runtime_type: str | None, slot_count: int,
          is_gateway: bool, worn_by: str | None, worn_slot: str | None}
      build_state: {current: int, last: int, complete: bool, next: list[{prefab_name: str, display_name: str, quantity: int | None, tool: bool}] |
          None} | None
      rocket: {network_id: str, name: str, state: str, dry_mass_kg: float, hull_pieces: int, internals: int} | None
      umbilical: {role: str, open: bool, partner: Thing | None, partner_distance: int, search: {found: str | None, partner_distance: int, how: str,
          stops: list[{column: int, step: int, reason: str}] | None}} | None
      uplink: {downlink: {reference_id: str, prefab_name: str | None, display_name: str | None, rocket: {network_id: str, name: str, state: str,
          dry_mass_kg: float, hull_pieces: int, internals: int} | None} | None, connected: bool, choices: list[{reference_id: str, prefab_name: str |
          None, display_name: str | None, rocket: {network_id: str, name: str, state: str, dry_mass_kg: float, hull_pieces: int, internals: int} |
          None}]} | None
      logic_types: list[{id: int, name: str | None, readable: bool, writable: bool}] | None
      logic_type_count: int
      writable: list[str]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.inspect('device', id='4820585')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
