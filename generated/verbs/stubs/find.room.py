def find(what: Literal['room'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A thing: only the room it is in.
    include: list[Literal['cells', 'devices']] = None,  # cells: every cell centre; devices: the devices in each room.
) -> dict:  # main list: r['rooms']; iterating or indexing it walks every page, in order
    """Closed rooms measured cell by cell: volume, pressure, temperature, gases; or the room one thing is in.
    Reply:
      rooms: list[{room_id: str, room_type: str, cell_count: int, volume_l: float, pressure_kpa: float, temperature_k: float, total_mol: float,
          heat_capacity_j_per_k: float, thermal_energy_j: float, gases: list[{gas: str, amount_mol: float, ratio: float}], bounds: {min: Vec3, max:
          Vec3}, contains_local_player: bool, devices: list[Thing] | None, cells: list[Vec3] | None}]
      local_player_room_id: str | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.find('room', id='4820585')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
