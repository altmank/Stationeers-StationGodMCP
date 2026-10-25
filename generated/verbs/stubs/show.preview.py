def show(what: Literal['preview'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    placements: list = None,  # As build structure's placements; each may give its own tint, and its label shows as the ghost's tooltip.
    prefab: str | int = None,  # Prefab: a whole name, a hash, or parts with * ('*Battery*', '*Tank*Insulated*': each part, any order); or a list of them.
    at: list | dict | str = None,  # A point, "looking_at", or a relative or look-face form (help positions).
    rotation: list = None,  # [x, y, z] degrees, multiples of 90.
    facing: str = None,  # Where the front points (as build structure).
    up: str = None,  # Where the top points (as build structure).
    face: str = None,  # Face-placed pieces: the cell face it sits on.
    orient: dict = None,  # {mount, upright, controls_toward, ports: [{role, index, type, toward}], flow: {from, to}}: choose the turn by intent. mount: wall, floor, ceiling or an axis; a target (controls_toward, toward, from, to): an axis, room, player, a point or a thing's id.
    above_floor: float = None,  # Bottom this many metres above the floor below at.
    build_state: str | int = None,  # finished (default), first, or a state index.
    label: str = None,  # What the placement is, shown as a tooltip when the crosshair is on its ghost (as build structure's label).
    run: dict = None,  # A cable, pipe or chute run to plan and draw: build cable, pipe or chute's options with what ({what: "pipe", grade, waypoints, ...}); nothing is built.
    from_plan: str = None,  # A plan route or plan spot reply (p = sg.plan('route', ...)), or one spot (p['spots'][2]): preview its build_arguments.
    style: Literal['boxes', 'ghost', 'both'] = 'boxes',  # boxes: footprint, body and port wire boxes; ghost: the structures' own meshes at the build state, see-through and tinted; both.
    tint: str | list = None,  # The ghosts' colour, yours to choose (no meaning of its own): a palette name (show highlight's), [r, g, b] or [r, g, b, a] each 0 to 1; alpha 0.4 when left out.
    allow: list[Literal['door_keepout']] = None,  # Guards to lift, by name.
    cells: list = None,  # Yellow 0.5 m cubes at these points.
    boxes: list = None,  # {min, max, color} boxes; color: red, green, white, cyan, yellow, any tint name, or [r, g, b] / [r, g, b, a].
    seconds: float = 30,  # How long to show.
    keep: bool = False,  # Keep what earlier calls drew.
    clear: bool = None,  # Only remove what is drawn.
    xray: bool = False,  # Draw everything through walls, frames and terrain: boxes and cells whole, ghosts fainter where hidden. A run's ghosts always get the see-through pass (pieces sit inside floors and frames).
    verbose: bool = False,  # plan: build structure's whole report of each placement (orient alternatives, port checks, layout, cost) in place of the brief one.
) -> dict:
    """A planned structure layout or cable, pipe or chute run (build's dry run) as wire boxes or see-through ghosts, plus cells and boxes.
    Materials are not checked: ready means build would place it. A new call replaces what the last drew (keep: true adds); ghosts counts this call's ghosts, ghosts_total every preview ghost on screen.
    Reply:
      shown: int
      ghosts: int
      ghosts_total: int
      cleared: int
      seconds: float
      plan: {ready: bool, problems: list[{code: str, message: str, index: int | None, reference_id: str | None}], warnings: list[{code: str, message:
          str, index: int | None, reference_id: str | None}], placements: list[{index: int, prefab_name: str | None, position: Vec3 | None,
          orientation: {facing: str | None, up: str | None, euler: Vec3} | None, build_state: int | None, ports: list[{index: int, at: Vec3, toward:
          str, type: str, role: str, network_id: str | None}] | None, layout: {port_checks: list[{index: int, at: Vec3, toward: str, type: str, role:
          str, flow: str | None, occupant: Thing | None, joins: bool, would_join_network: str | None, blocked: str | None, in_door_keep_out: bool}]} |
          None}]} | None
      run: {what: str, ready: bool, pieces: int, problems: list[{code: str, message: str}], warnings: list[{code: str, message: str}]} | None
      notes: list[str]
      drawn_on: list[str] | None
      view: {source: str, age_s: float} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.show('preview', prefab='StructureWallLight', at=[716, 203, 668], facing='-x')
    """
