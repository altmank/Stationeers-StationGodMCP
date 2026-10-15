def find(what: Literal['room'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A thing: only the room it is in.
    include: list[Literal['cells', 'devices']] = None,  # cells: every cell centre; devices: the devices in each room.
) -> dict:  # main list: r['rooms']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Closed rooms measured cell by cell: volume, pressure, temperature, gases; or the room one thing is in.
    One id answers the wrapper below; a list of ids answers one row per id, {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count, local_player_room_id}, each row's payload the room itself (the rooms item's keys) - not the wrapper - or the error for a thing in no room.
    Reply:
      rooms: list[{room_id: str, room_type: str, cell_count: int, volume_l: float, pressure_kpa: float, temperature_k: float, total_mol: float,
          heat_capacity_j_per_k: float, thermal_energy_j: float, gases: list[{gas: str, amount_mol: float, ratio: float}], bounds: {min: Vec3, max:
          Vec3}, contains_local_player: bool, devices: list[Thing] | None, cells: list[Vec3] | None}]
      count: int
      total: int
      offset: int
      has_more: bool
      local_player_room_id: str | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.find('room', id='4820585')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
