def plan(what: Literal['route'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    kind: Literal['cable', 'pipe', 'chute'],  # The run's kind.
    start: dict | list = None,  # (route) {at}, {id} of a piece, {id, port} of a device, or a list.
    end: dict = None,  # (route) As start, one, or {network}.
    reroute: dict = None,  # {id: [...]} or {between: [end, end]}: replace an old run; leave out start and end.
    trunk: dict = None,  # Bus mode instead of end: {waypoints} or {cells} every start branches from.
    grade: Literal['normal', 'heavy', 'super_heavy', 'gas', 'liquid', 'insulated_gas', 'insulated_liquid', 'chute'] = None,  # cable: normal, heavy (default), super_heavy; pipe (required): gas, liquid, insulated_gas, insulated_liquid; chute: chute.
    style: Literal['supported', 'frame_edges', 'walls', 'hidden', 'inside_frames', 'free'] = 'supported',  # What the route keeps to.
    allow: list[Literal['door_keepout', 'split']] = None,  # door_keepout: may enter a door keep-out; split: removals may split networks.
    split_long: bool = True,  # Split long straights the route meets.
    avoid: list[Literal['room_interior', 'walkways']] = None,  # Extra cost for room interiors or walkways.
    avoid_networks: bool | list = None,  # True, or networks: keep beside no other network.
    min_bends: bool = False,  # Turns cost much more.
    axis_order: Literal['any', 'vertical_first', 'horizontal_first'] = 'any',  # Which way the route turns first.
    max_length: int = 400,  # Most cells.
    margin: float = 6,  # Search box margin around the ends.
    join: Literal['ends', 'none', 'all'] = 'ends',  # What the run joins.
    allow_bridge: list = None,  # Networks and devices whose joining is meant.
    root: str = None,  # The device would_split measures cut-off against. (default every supplier) (default every supplier)
    join_to: str | dict = None,  # The network the route must end on. (default end's network) (default end's network)
    join_trunk: bool = False,  # Add a missing tap to join_to.
    reserve_cells: list = None,  # Cells kept free for another run.
    reserve_ports: list = None,  # {id, port} ports whose joining cell stays free.
    assume_removed: list[str] = None,  # Things planned as if gone; the kind's pieces are removed in the same job.
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    include: list[Literal['links', 'notes', 'network_devices']] = None,  # links: every added and lost link; notes: fixed text; network_devices: each network's devices.
    summarize: bool = False,  # The report in short instead of whole.
    rows: int = 5,  # Cells the report lists; 0 for none.
) -> dict:
    """A cable, pipe or chute route between ends, ports or networks, dry-run as the build would; answers build_arguments.
    Reply:
      tool: str
      found: bool
      route: {waypoints: list[Vec3], length: int, bends: int, cost: float, expanded: int, removes: list[str], air_cells: int, air: list[Vec3] | None,
          branches: list[{waypoints: list[Vec3], length: int, attach: Vec3}] | None, extra_ends: int | None, visibility: {inside: int, frame_surface:
          int, wall: int, air: int} | None, assumed_removed: {pieces: list[str], others: list[str], missing: list[str], in_the_way: list[str]} | None,
          removal_refund: list[{prefab_name: str | None, quantity: int}] | None} | None
      failure: str | None
      build_arguments: Any | None
      build_report: {grade: str | None, ready: bool, problems: list[{code: str, message: str, reference_id: str | None, at: Vec3 | None}], warnings:
          list[{code: str, message: str, reference_id: str | None, at: Vec3 | None}], cells_total: int, placed: int, changed: int, kept: int,
          removed_count: int, air_cells: int | None, cells: list[{at: Vec3, action: str, prefab_name: str | None, rotation_deg: Vec3 | None, shape:
          str, ends: list[str], cost: int, refund: int, existing: {reference_id: str, prefab_name: str, ends: list[str], network_id: str | None} |
          None, joins: list[{toward: str, kind: str, thing: Thing | None, port: int | None}], open_end: str | None, flow: {in: list[str], out:
          list[str]} | None}], removals: list[{reference_id: str, prefab_name: str | None, at: Vec3, network_id: str | None, refund:
          list[{prefab_name: str | None, quantity: int}], assumed: bool | None}], materials: {from: Thing | None, needed: list[{prefab_name: str |
          None, display_name: str | None, needed: int, available: int, stacks: list[{reference_id: str, quantity: int, held_in: str | None,
          slot_index: int | None}], paid_by: list[{reference_id: str, name: str | None, quantity: int, available: int}] | None}], refund_enabled:
          bool, refund: list[{prefab_name: str | None, quantity: int}], refund_plan: RefundPlan | None}, networks_before: list[Any], networks_after:
          list[{index: int, networks_before: list[str], new_pieces: int, device_count: int, port_count: int, devices: list[{device: Thing, port: int,
          bridging: bool, network_before: str | None}] | None, guard: Any | None}], would_bridge: list[{kind: str, networks: list[{network_id: str,
          devices: list[Thing]}], device: Thing | None, ports: list[int], allowed: bool}], would_split: list[{network_id: str | None, parts:
          list[int], cut_ports: list[{device: Thing, port: int, bridging: bool, network_before: str | None}], allowed: bool, components: list[{index:
          int, devices: list[{device: Thing, port: int, bridging: bool, network_before: str | None}], holds_root: bool}], root: list[Thing], cut_off:
          list[Thing] | None}], links: {count_before: int, count_after: int, added: list[{from_id: str, from_prefab_name: str | None, to_id: str,
          to_prefab_name: str | None}] | None, lost: list[{from_id: str, from_prefab_name: str | None, to_id: str, to_prefab_name: str | None}] |
          None, added_count: int, lost_count: int, model_matches_game: bool, model_differences: list[{from_id: str, from_prefab_name: str | None,
          to_id: str, to_prefab_name: str | None}]} | None, notes: list[str] | None} | None
      build_report_summary: {ready: bool, problems: list[{code: str, message: str, reference_id: str | None, at: Vec3 | None}], warnings: list[str],
          cells_total: int, placed: int, changed: int, kept: int, removed_count: int, air_cells: int | None, would_bridge: int, would_split: int,
          needed: list[{prefab_name: str | None, needed: int, available: int}]} | None
      notes: list[str]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.plan('route', kind='pipe', grade='gas', start={'id': '720259', 'port': 1}, end={'network': '5012345'})
    """
