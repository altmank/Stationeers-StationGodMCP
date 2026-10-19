def build(what: Literal['chute'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    grade: Literal['chute'] = 'chute',  # The chute kit (one grade).
    waypoints: list = None,  # Points joined by straight axis lines.
    cells: list = None,  # Every cell, each next to the one before.
    piece: dict = None,  # One piece {at, ends, label, color}; label and color optional.
    pieces: list = None,  # Separate pieces, each {at, ends, label, color}: label and color (a colour index or name) are optional.
    branches: list = None,  # Side runs, each {waypoints or cells, attach}.
    extra_ends: list = None,  # Extra ends on run cells, each {at, toward}.
    join: Literal['ends', 'none', 'all'] = 'ends',  # What the run joins.
    allow: list[Literal['door_keepout', 'lint', 'split']] = None,  # Guards to lift, by name.
    split_long: bool = True,  # Split a long straight the run joins or crosses; False refuses.
    remove_ids: list[str] = None,  # Pieces of the run's kind removed in the same job first.
    assume_removed: list[str] = None,  # Things checked as if gone; a real run needs them gone.
    allow_bridge: list = None,  # Networks and devices whose joining is meant.
    root: str = None,  # The device would_split measures cut-off against. (default every supplier) (default every supplier)
    join_to: str | dict = None,  # The network the run must end on.
    join_trunk: bool = False,  # With join_to: add the missing tap.
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    free: bool = False,  # No materials, and what it replaces or removes gives nothing back unless refund_to names where. Cheat.
    refund_to: str | int | list = None,  # Where removed materials go: a word (inventory, source, storage, ground, none), a container id, or a list tried in turn. (default [inventory, source, storage, ground]) (default [inventory, source, storage, ground])
    include: list[Literal['links', 'notes', 'network_devices']] = None,  # Parts to add to the reply.
    rows: int = 5,  # Cells listed; 0 for none.
    verbose: bool = False,  # The long form of the reply.
    from_plan: str = None,  # A plan route or plan spot reply (p = sg.plan('route', ...)), or one spot (p['spots'][2]): build its build_arguments. With from_plan, what may be left out: sg.build(from_plan=p).
) -> dict:  # main list: r['cells']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """A chute run or pieces from Kit (Chute), source to sink, with flow guards.
    A build that pays from the player's inventory (materials left out, or "player") carries the warning materials_from_player. With from_plan, what may be left out: it comes from the plan.
    Ending on an existing piece takes no hand swap: a run cell on a piece of the kind, a run end meeting one (join ends), or a piece's end pointing into one makes that piece the junction it needs in the same job (listed changed). To end on a network rather than a cell, give join_to with join_trunk: true.
    Reports in a report run; acts in a confirmed run.
    Cheat when free: true: ask the user first.
    placed: list[str] on a job reply, the new ids (a run's log.placed, a structure job's result.placed; a brief poll counts them in log.placed_count); int on a run report, the cells it would build.
    Reply, one of:
    (JobQueued)
      JobQueued
    (RunJob)
      RunJob
    (RunReport)
      grade: str | None
      ready: bool
      problems: list[{code: str, message: str, reference_id: str | None, at: Vec3 | None}]
      warnings: list[{code: str, message: str, reference_id: str | None, at: Vec3 | None}]
      cells_total: int
      placed: int
      changed: int
      kept: int
      removed_count: int
      air_cells: int | None
      cells: list[{at: Vec3, action: str, prefab_name: str | None, rotation_deg: Vec3 | None, shape: str, ends: list[str], cost: int, refund: int,
          existing: {reference_id: str, prefab_name: str, ends: list[str], network_id: str | None} | None, joins: list[{toward: str, kind: str, thing:
          Thing | None, port: int | None}], open_end: str | None, flow: {in: list[str], out: list[str]} | None}]
      removals: list[{reference_id: str, prefab_name: str | None, at: Vec3, network_id: str | None, refund: list[{prefab_name: str | None, quantity:
          int}], assumed: bool | None}]
      materials: {from: Thing | None, needed: list[{prefab_name: str | None, display_name: str | None, needed: int, available: int, stacks:
          list[{reference_id: str, quantity: int, held_in: str | None, slot_index: int | None}], paid_by: list[{reference_id: str, name: str | None,
          quantity: int, available: int}] | None}], refund_enabled: bool, refund: list[{prefab_name: str | None, quantity: int}], refund_plan:
          RefundPlan | None}
      networks_before: list[Any]
      networks_after: list[{index: int, networks_before: list[str], new_pieces: int, device_count: int, port_count: int, devices: list[{device: Thing,
          port: int, bridging: bool, network_before: str | None}] | None, guard: Any | None}]
      would_bridge: list[{kind: str, networks: list[{network_id: str, devices: list[Thing]}], device: Thing | None, ports: list[int], allowed: bool}]
      would_split: list[{network_id: str | None, parts: list[int], cut_ports: list[{device: Thing, port: int, bridging: bool, network_before: str |
          None}], allowed: bool, components: list[{index: int, devices: list[{device: Thing, port: int, bridging: bool, network_before: str | None}],
          holds_root: bool}], root: list[Thing], cut_off: list[Thing] | None}]
      links: {count_before: int, count_after: int, added: list[{from_id: str, from_prefab_name: str | None, to_id: str, to_prefab_name: str | None}] |
          None, lost: list[{from_id: str, from_prefab_name: str | None, to_id: str, to_prefab_name: str | None}] | None, added_count: int, lost_count:
          int, model_matches_game: bool, model_differences: list[{from_id: str, from_prefab_name: str | None, to_id: str, to_prefab_name: str |
          None}]} | None
      notes: list[str] | None
    (BuildJob)
      BuildJob
    (JobBusy)
      JobBusy
    (JobDropped)
      JobDropped
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Job views, one per help call (help {topic: "types", subtopic: "<name>"} or sg.help('types', '<name>')): BuildJob, JobBusy, JobDropped, JobQueued,
        RunJob
    Example: sg.build('chute', waypoints=[[10, 2, 4], [10, 2, 9]])
    """
