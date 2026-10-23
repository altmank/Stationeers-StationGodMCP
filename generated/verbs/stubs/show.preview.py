def show(what: Literal['preview'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    placements: list = None,  # As build structure's placements; each may give its own tint.
    prefab: str | int = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    at: list | dict | str = None,  # A point, "looking_at", or a relative or look-face form (help positions).
    rotation: list = None,  # [x, y, z] degrees, multiples of 90.
    facing: str = None,  # Where the front points (as build structure).
    up: str = None,  # Where the top points (as build structure).
    face: str = None,  # Face-placed pieces: the cell face it sits on.
    orient: dict = None,  # {mount, upright, controls_toward, ports: [{role, index, type, toward}], flow: {from, to}}: choose the turn by intent. mount: wall, floor, ceiling or an axis; a target (controls_toward, toward, from, to): an axis, room, player, a point or a thing's id.
    above_floor: float = None,  # Bottom this many metres above the floor below at.
    build_state: str | int = None,  # finished (default), first, or a state index.
    run: dict = None,  # A cable, pipe or chute run to plan and draw: build cable, pipe or chute's options with what ({what: "pipe", grade, waypoints, ...}); nothing is built.
    from_plan: str = None,  # A plan route or plan spot reply (p = sg.plan('route', ...)), or one spot (p['spots'][2]): preview its build_arguments.
    style: Literal['boxes', 'ghost', 'both'] = 'boxes',  # boxes: footprint, body and port wire boxes; ghost: the structures' own meshes at the build state, see-through and tinted; both.
    tint: str | list = None,  # The ghosts' colour, yours to choose (no meaning of its own): a palette name (show highlight's), [r, g, b] or [r, g, b, a] each 0 to 1; alpha 0.4 when left out.
    allow: list[Literal['door_keepout']] = None,  # Guards to lift, by name.
    cells: list = None,  # Yellow 0.5 m cubes at these points.
    boxes: list = None,  # {min, max, color} boxes.
    seconds: float = 30,  # How long to show.
    keep: bool = False,  # Keep what earlier calls drew.
    clear: bool = None,  # Only remove what is drawn.
    xray: bool = False,  # Draw everything through walls, frames and terrain: boxes and cells whole, ghosts fainter where hidden.
) -> dict:
    """A planned structure layout or cable, pipe or chute run (build's dry run) as wire boxes or see-through ghosts, plus cells and boxes.
    Reply:
      shown: int
      ghosts: int
      cleared: int
      seconds: float
      plan: {ready: bool, problems: list[{code: str, message: str, index: int | None, reference_id: str | None}], warnings: list[{code: str, message:
          str, index: int | None, reference_id: str | None}], placements: list[{index: int, prefab_name: str | None, prefab_hash: int | None,
          display_name: str | None, placement: str | None, position: Vec3 | None, orientation: {facing: str | None, up: str | None, euler: Vec3} |
          None, face: str | None, ports: list[{index: int, at: Vec3, toward: str, type: str, role: str, network_id: str | None}] | None, build_state:
          int | None, build_states: int | None, label: str | None, color: {index: int | None, name: str | None} | None, cost: list[{prefab_name: str |
          None, quantity: int}], layout: {footprint: {small_cells: {count: int, cells: list[{x: float, y: float, z: float}] | None}, large_cells:
          list[Vec3] | None, body: {origin: {x: float, y: float, z: float}, render_box: {min: PointView, max: PointView, size: PointView},
          centre_offset: {x: float, y: float, z: float}, grid_box: {min: PointView, max: PointView, size: PointView} | None, grid_cells: int}, mount:
          {plane: str, outward: str, axes: list[str], min: list[float], max: list[float]} | None}, sections: {walls: list[{face: {x: float, y: float,
          z: float}, reference_id: str | None, prefab_name: str | None, kind: str}], count: int, crosses_seam: bool} | None, conflicts: list[{code:
          str, level: str, message: str, reference_id: str | None}], port_checks: list[{index: int, at: Vec3, toward: str, type: str, role: str, flow:
          str | None, occupant: Thing | None, joins: bool, would_join_network: str | None, blocked: str | None, in_door_keep_out: bool}] | None} |
          None, orient: {chosen: {facing: str | None, up: str | None, euler: Vec3, score: float | None, reasons: list[str], reversed_flow: bool} |
          None, alternatives: list[{facing: str | None, up: str | None, euler: Vec3, score: float | None, reasons: list[str], reversed_flow: bool}],
          tried: int, mode_flip: {logic_type: str, value: int, reason: str} | None} | None, resolved: {at: {x: float, y: float, z: float}, at_how:
          str, facing: str | None, facing_how: str | None, view: {source: str, age_s: float} | None} | None}], materials: list[{prefab_name: str |
          None, needed: int, available: int, paid_by: list[{reference_id: str, name: str | None, quantity: int, available: int}] | None}], from: Thing
          | None, free: bool, notes: list[str] | None} | None
      run: {what: str, ready: bool, pieces: int, problems: list[{code: str, message: str}], warnings: list[{code: str, message: str}]} | None
      notes: list[str]
      drawn_on: list[str] | None
      view: {source: str, age_s: float} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.show('preview', prefab='StructureWallLight', at=[716, 203, 668], facing='-x')
    """
