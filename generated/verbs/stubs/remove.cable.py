def remove(what: Literal['cable'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Pieces.
    waypoints: list = None,  # The piece in every cell along these lines.
    cells: list = None,  # The piece in each cell.
    allow: list[Literal['split']] = None,  # split: may split networks or leave ports joined to nothing.
    root: str = None,  # The device would_split measures cut-off against. (default every supplier) (default every supplier)
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    refund_to: str | int | list = None,  # Where removed materials go: a word (inventory, source, storage, ground, none), a container id, or a list tried in turn. (default [inventory, source, storage, ground]) (default [inventory, source, storage, ground])
    include: list[Literal['links', 'notes', 'network_devices']] = None,  # Parts to add to the reply.
    rows: int = 5,  # Pieces listed; 0 for none.
    verbose: bool = False,  # The long form of the reply.
) -> dict:  # main list: r['removals']; iterating or indexing it walks every page, in order
    """Cable pieces as wire cutters remove them: by id, or the piece in each cell of waypoints or cells.
    Reports in a report run; acts in a confirmed run.
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
    Example: sg.remove('cable', id=['3763565'])
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
