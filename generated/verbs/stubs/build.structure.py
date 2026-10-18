def build(what: Literal['structure'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    placements: list = None,  # Up to 64 {prefab, at, rotation, facing, up, face, orient, above_floor, build_state, label, color, logic}, built in order in one job; or one at the top level.
    prefab: str | int = None,  # Prefab name or hash.
    at: list | dict | str = None,  # A point, "looking_at", or a relative or look-face form (help positions).
    rotation: list = None,  # [x, y, z] degrees, multiples of 90.
    spread: dict | list = None,  # {from, to, step} or a list: this prefab every step metres along each line; reports first.
    facing: Literal['+x', '-x', '+y', '-y', '+z', '-z', 'toward_player', 'away_from_player', 'out_of_face', 'into_room'] = None,  # Where the front points.
    up: Literal['+x', '-x', '+y', '-y', '+z', '-z'] = None,  # up (+x | -x | +y | -y | +z | -z, default +y (+z when facing is vertical)): Where the top points. (default +y (+z when facing is vertical))
    face: Literal['+x', '-x', '+y', '-y', '+z', '-z'] = None,  # Face-placed pieces (walls): the cell face it sits on.
    orient: dict = None,  # {mount, upright, controls_toward, ports: [{role, index, type, toward}], flow: {from, to}}: choose the turn by intent. mount: wall, floor, ceiling or an axis; a target (controls_toward, toward, from, to): an axis, room, player, a point or a thing's id.
    above_floor: float = None,  # Bottom this many metres above the floor below at.
    build_state: str | int = None,  # finished (default), first, or a state index.
    label: str = None,  # A name, as the Labeller writes it.
    color: str | int = None,  # A colour name or index.
    logic: dict = None,  # Logic values set once it is built, {On: 1, Setting: 50}, as write logic sets them; needs the finished build state.
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    free: bool = False,  # Creative worlds: no materials. Cheat.
    allow: list[Literal['door_keepout', 'lint', 'pad']] = None,  # door_keepout: a keep-out cell is a warning; lint: lint problems are warnings the report marks PASSED; pad: a placement in a landing pad's approach is a problem; allowed, a PASSED warning.
    include: list[Literal['notes', 'footprint_cells']] = None,  # footprint_cells: each footprint's cells.
    verbose: bool = False,  # Report: layouts a refused report leaves out; real run: the whole preflight.
    acknowledge_gas_lost: str = None,  # The job id whose gas loss you accept (with the user's agreement); lifts the gas hold.
    from_plan: str = None,  # A plan route or plan spot reply (p = sg.plan('route', ...)), or one spot (p['spots'][2]): build its build_arguments. With from_plan, what may be left out: sg.build(from_plan=p).
) -> dict:  # main list: r['placements']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Any structure a kit builds (light, device, frame, wall, tank, rocket part) by prefab, at a point with a turn; several placements are one job.
    A build that pays from the player's inventory (materials left out, or "player") carries the warning materials_from_player. With from_plan, what may be left out: it comes from the plan.
    The report gives each placement's position as the game's cursor snaps it (the row's cell and placements[].position). A placement checks against the run's earlier placements as standing: a clash with one (overlaps_placement), the frame it needs (supported_by_placement), and with above_floor, a frame or floor plate an earlier placement puts down as its floor.
    Reports in a report run; acts in a confirmed run.
    Cheat when free: true: ask the user first.
    placed: list[str] on a job reply, the new ids (a run's log.placed, a structure job's result.placed; a brief poll counts them in log.placed_count); int on a run report, the cells it would build.
    Reply, one of:
    (JobQueued)
      JobQueued
    (BuildJob)
      BuildJob
    (PlaceReport)
      ready: bool
      problems: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      warnings: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      placements: list[{index: int, prefab_name: str | None, prefab_hash: int | None, display_name: str | None, placement: str | None, position: Vec3
          | None, orientation: {facing: str | None, up: str | None, euler: Vec3} | None, face: str | None, ports: list[{index: int, at: Vec3, toward:
          str, type: str, role: str, network_id: str | None}] | None, build_state: int | None, build_states: int | None, label: str | None, color:
          {index: int | None, name: str | None} | None, cost: list[{prefab_name: str | None, quantity: int}], layout: {footprint: {small_cells:
          {count: int, cells: list[{x: float, y: float, z: float}] | None}, large_cells: list[Vec3] | None, body: {origin: {x: float, y: float, z:
          float}, render_box: {min: {x: float, y: float, z: float}, max: {x: float, y: float, z: float}, size: {x: float, y: float, z: float}},
          centre_offset: {x: float, y: float, z: float}, grid_box: {min: {x: float, y: float, z: float}, max: {x: float, y: float, z: float}, size:
          {x: float, y: float, z: float}} | None, grid_cells: int}, mount: {plane: str, outward: str, axes: list[str], min: list[float], max:
          list[float]} | None}, sections: {walls: list[{face: {x: float, y: float, z: float}, reference_id: str | None, prefab_name: str | None, kind:
          str}], count: int, crosses_seam: bool} | None, conflicts: list[{code: str, level: str, message: str, reference_id: str | None}],
          port_checks: list[{index: int, at: Vec3, toward: str, type: str, role: str, flow: str | None, occupant: Thing | None, joins: bool,
          would_join_network: str | None, blocked: str | None, in_door_keep_out: bool}] | None} | None, orient: {chosen: {facing: str | None, up: str
          | None, euler: Vec3, score: float | None, reasons: list[str], reversed_flow: bool} | None, alternatives: list[{facing: str | None, up: str |
          None, euler: Vec3, score: float | None, reasons: list[str], reversed_flow: bool}], tried: int, mode_flip: {logic_type: str, value: int,
          reason: str} | None} | None, resolved: {at: {x: float, y: float, z: float}, at_how: str, facing: str | None, facing_how: str | None, view:
          {source: str, age_s: float} | None} | None}]
      materials: list[{prefab_name: str | None, needed: int, available: int, paid_by: list[{reference_id: str, name: str | None, quantity: int,
          available: int}] | None}]
      from: Thing | None
      free: bool
      notes: list[str] | None
    (RunJob)
      RunJob
    (JobBusy)
      JobBusy
    (JobDropped)
      JobDropped
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Job views, one per help call (help {topic: "types", subtopic: "<name>"} or sg.help('types', '<name>')): BuildJob, JobBusy, JobDropped, JobQueued,
        RunJob
    Example: sg.build('structure', prefab='StructureWallLight', at=[716, 203, 668], facing='-x')
    """
