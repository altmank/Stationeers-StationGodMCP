def inspect(what: Literal['looking_at'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    distance: float = 10,  # How far the look ray is cast.
    include: list[Literal['player', 'interactable', 'view', 'hit', 'body', 'all']] = None,  # Parts to add.
) -> dict:
    """What the player's crosshair is on, and where a longer look ray lands.
    Reply:
      player: {reference_id: str, display_name: str | None, position: Vec3} | None
      target: {reference_id: str, prefab_name: str | None, display_name: str | None, custom_name: str | None, kind: str, runtime_type: str, position:
          Vec3, distance_m: float | None, is_device: bool, has_atmosphere: bool, parent: {reference_id: str, prefab_name: str | None, display_name:
          str | None, slot_index: int, slot_name: str | None} | None, rotation: {facing: str | None, up: str | None, euler: Vec3} | None, body:
          {origin: {x: float, y: float, z: float}, render_box: {min: {x: float, y: float, z: float}, max: {x: float, y: float, z: float}, size: {x:
          float, y: float, z: float}}, centre_offset: {x: float, y: float, z: float}, grid_box: {min: {x: float, y: float, z: float}, max: {x: float,
          y: float, z: float}, size: {x: float, y: float, z: float}} | None, grid_cells: int} | None, facing_me: bool | None} | None
      interactable: {action: str, display_name: str | None, contextual_name: str | None, state: int, slot: {slot_index: int, slot_name: str | None,
          occupant: Thing | None} | None} | None
      view: {eye: {x: float, y: float, z: float}, forward: {x: float, y: float, z: float}, right: {x: float, y: float, z: float}, up: {x: float, y:
          float, z: float}, yaw_deg: float, pitch_deg: float, axes: {forward: str, right: str, up: str, back: str, left: str, down: str, look: str},
          ambiguous: bool, third_person: bool, seated: bool, source: str, age_s: float} | None
      hit: {point: {x: float, y: float, z: float}, distance_m: float, face: str | None, thing: Thing | None, normal: {x: float, y: float, z: float},
          face_plane: str | None, cell_2m: Vec3, small_cell: {x: float, y: float, z: float}, support: str, local_on_target: {right_m: float, up_m:
          float, forward_m: float} | None} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('looking_at')
    """
