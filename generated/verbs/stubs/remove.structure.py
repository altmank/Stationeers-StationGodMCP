def remove(what: Literal['structure'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The structures.
    allow: list[Literal['contents', 'breach', 'broken', 'burst', 'unsupported']] = None,  # contents: items drop, gas goes; breach: joins spaces 1 kPa or more apart; broken: nothing back; burst; unsupported.
    refund_to: str | int | list = None,  # Where removed materials go: a word (inventory, source, storage, ground, none), a container id, or a list tried in turn. (default [inventory, source, storage, ground]) (default [inventory, source, storage, ground])
    materials: str = None,  # The one holder for source and storage refunds: an id or "player". Default the player.
    gas_to: str = None,  # Keep a removed device's gas: connected, or a pipe network id.
    include: list[Literal['notes']] = None,  # Parts to add to the reply.
    verbose: bool = False,  # Dry run: each port a split cuts; real run: the whole job.
    acknowledge_gas_lost: str = None,  # The job id whose gas loss you accept (with the user's agreement); lifts the gas hold.
) -> dict:  # main list: r['placements']; iterating or indexing it walks every page, in order
    """Built structures by id (up to 256, one job), taken apart; every build state is refunded down to the kit.
    Reports in a report run; acts in a confirmed run.
    Reply, one of:
    (JobQueued)
      JobQueued
    (BuildJob)
      BuildJob
    (PlaceReport)
      tool: str
      status: str
      job_id: str | None
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
    (RemoveReport)
      tool: str
      status: str
      job_id: str | None
      ready: bool
      problems: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      warnings: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      removals: list[{index: int, reference_id: str, prefab_name: str | None, display_name: str | None, position: Vec3, build_state: int, kind: str,
          refund: list[{prefab_name: str | None, quantity: int}]}]
      refund: list[{prefab_name: str | None, quantity: int}]
      refund_to: Any
      refund_plan: RefundPlan | None
      from: Thing | None
      will_burst: list[{network_id: str, networks_left: int, volume_left_l: float, pressure_now_kpa: float, pressure_after_kpa: float, rating_kpa:
          float, pipes: list[{reference_id: str, prefab_name: str, rating_kpa: float, where: str, pressure_there_kpa: float}], where: list[str],
          holds_mol: float, released_mol: float, gases: list[{gas: str, amount_mol: float}]}]
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
    Example: sg.remove('structure', id=['1559707'])
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
