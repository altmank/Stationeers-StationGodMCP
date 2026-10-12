def swap(what: Literal['frame'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # Target: an id, a list of ids, or "looking_at" (the thing the crosshair is on); "player" where the what says.
    room: str = None,  # Every frame around this room.
    into: str = None,  # The new frame prefab; default each frame's own (finishes it).
    prefab: str | int | list = None,  # Only pieces of these prefabs.
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    skip_unmatched: bool = False,  # Leave pieces that cannot change as they are instead of refusing.
    refund_to: str | int | list = None,  # Where removed materials go: a word (inventory, source, storage, ground, none), a container id, or a list tried in turn. (default [inventory, source, storage, ground]) (default [inventory, source, storage, ground])
    include: list[Literal['notes']] = None,  # Parts to add to the reply.
    rows: int = 5,  # Pieces listed per list.
    verbose: bool = False,  # The long form of the reply.
) -> dict:  # main list: r['pieces']; iterating or indexing it walks every page, in order
    """Replace frames with another frame prefab, or finish unfinished frames, without opening a cell.
    Reports in a report run; acts in a confirmed run.
    Reply, one of:
    (JobQueued)
      JobQueued
    (StructureSwapReport)
      tool: str
      target: str
      status: str
      job_id: str | None
      ready: bool
      problems: list[{code: str, message: str, reference_id: str | None}]
      pieces_total: int
      to_swap: int
      kept: int
      unmatched: int
      pieces: list[{reference_id: str, prefab_name: str | None, position: Vec3, rotation_deg: Vec3, build_state: int, target_prefab_name: str | None,
          target_build_state: int, blocks_air_before: bool, blocks_air_after: bool, blocks_gravity_before: bool, blocks_gravity_after: bool,
          air_change: str, seals: bool, stressed: bool, faces: list[{position: Vec3, cells: list[Vec3], pressure_kpa_a: float, pressure_kpa_b: float,
          difference_kpa: float, max_pressure_delta_kpa_before: float, max_pressure_delta_kpa_after: float, verdict: str}] | None, room_ids:
          list[str], materials: list[{prefab_name: str | None, cost: int, refund: int, net: int}]}]
      pieces_listed: int
      by_prefab: list[{prefab_name: str | None, target_prefab_name: str | None, count: int}]
      kept_pieces: list[{reference_id: str, prefab_name: str | None, reason: str, message: str}]
      unmatched_pieces: list[{reference_id: str, prefab_name: str | None, reason: str, message: str}]
      from: Thing | None
      materials: list[{prefab_name: str | None, display_name: str | None, cost: int, refund: int, charge: int, give_back: int, available: int,
          paid_by: list[{reference_id: str, name: str | None, quantity: int, available: int}] | None, stacks: list[{reference_id: str, quantity: int,
          held_in: str | None, slot_index: int | None}]}]
      refund_enabled: bool
      refund_plan: RefundPlan | None
      rooms: list[{room_id: str, cell_count: int, total_mol: float, energy_j: float}]
      notes: list[str] | None
    (JobBusy)
      JobBusy
    (SwapJob)
      SwapJob
    (JobDropped)
      JobDropped
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Job views, one per help call (help {topic: "types", subtopic: "<name>"} or sg.help('types', '<name>')): JobBusy, JobDropped, JobQueued, SwapJob
    Example: sg.swap('frame', room='17')
    In a script this always answers {results: [{index, ok, reference_id, ...the reply above} or {index, ok, reference_id, error}], count, success_count, error_count}, given one id or a list.
    """
