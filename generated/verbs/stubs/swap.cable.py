def swap(what: Literal['cable'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    network: str | dict = None,  # A network: its id, a piece or device on it, or {id, port}.
    id: str | list[str] = None,  # Pieces (up to 4096).
    into: Literal['heavy', 'super_heavy'] = None,  # Upgrade to this grade; heavy when tidy is left out.
    tidy: list[Literal['remove_dead_ends', 'remove_loops', 'remove_redundant', 'split_long_straights', 'merge_straights', 'simplify_junctions']] | bool = None,  # Tidy operations, alone or together; True is [simplify_junctions].
    spare: list[str] = None,  # tidy: pieces never removed, loops spared.
    only: list[str] = None,  # remove_redundant: the only pieces that may go.
    older_than: str = None,  # remove_redundant: only pieces with a lower id may go.
    root: str = None,  # remove_redundant: the device the network is fed from. (default every supplier) (default every supplier)
    materials: str | list[str] = None,  # Holder(s) that pay materials and take source refunds, tried in order; ids or "player". Default the player.
    skip_unmatched: bool = False,  # Leave pieces that cannot change as they are instead of refusing.
    refund_to: str | int | list = None,  # Where removed materials go: a word (inventory, source, storage, ground, none), a container id, or a list tried in turn. (default [inventory, source, storage, ground]) (default [inventory, source, storage, ground])
    include: list[Literal['notes', 'network_devices']] = None,  # notes: fixed text; network_devices: each network's devices in the job's verification.
    rows: int = 5,  # Pieces listed per list.
    verbose: bool = False,  # The long form of the reply.
) -> dict:  # main list: r['pieces']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Upgrade cable in place (into heavy or super_heavy) or tidy a network (tidy: remove dead ends, loops...).
    Give into (upgrade) or tidy, not both.
    Reports in a report run; acts in a confirmed run.
    Reply, one of:
    (JobQueued)
      JobQueued
    (UpgradeReport)
      target: str
      ready: bool
      problems: list[{code: str, message: str, reference_id: str | None}]
      warnings: list[{code: str, message: str, reference_id: str | None}] | None
      pieces_total: int
      to_swap: int
      kept: int
      unmatched: int
      pieces: list[{reference_id: str, prefab_name: str | None, position: Vec3, rotation_deg: Vec3, target_prefab_name: str | None,
          target_rotation_deg: Vec3, rotation_kept: bool, cost: int, network_id: str | None, operation: str | None, ends: list[str] | None,
          connected_ends: list[str] | None, replacement_count: int | None, round: int | None, why: str | None, merged_reference_ids: list[str] | None,
          refund_count: int | None}]
      pieces_listed: int
      by_prefab: list[Any]
      kept_pieces: list[{reference_id: str, prefab_name: str | None, reason: str, message: str}]
      unmatched_pieces: list[{reference_id: str, prefab_name: str | None, reason: str, message: str}]
      dead_ends: int | None
      dead_end_pieces: list[{reference_id: str, prefab_name: str | None, position: Vec3, reason: str, ends: list[str], connected_ends: list[str],
          stopped_by: str | None}] | None
      loops: list[{index: int, pieces: list[{reference_id: str, prefab_name: str | None, position: Vec3, cut: bool}], cut: list[str], spared: bool,
          unbroken: str | None}] | None
      redundant: {candidates: int, removed: list[str], root: list[Thing], kept_count: int, kept_by_reason: dict[str, int], kept: list[{reference_id:
          str, prefab_name: str | None, position: Vec3, reason: str, devices: list[Thing], pieces_cut_off: int}]} | None
      riding_count: int | None
      riding: list[{reference_id: str, carries: Thing, held: str}] | None
      from: Thing | None
      coils: list[{prefab_name: str | None, display_name: str | None, needed: int, available: int, stacks: list[{reference_id: str, quantity: int,
          held_in: str | None, slot_index: int | None}], paid_by: list[{reference_id: str, name: str | None, quantity: int, available: int}] | None}]
      refund_enabled: bool
      refund: list[{prefab_name: str | None, quantity: int}]
      refund_plan: RefundPlan | None
      networks: list[Any]
      devices: list[{reference_id: str, prefab_name: str | None, display_name: str | None, links: int, mounted: bool, network_ids: list[str]}]
      connectivity: {links_before: int, links_after_predicted: int, model_matches_game: bool, added: list[{from_id: str, from_prefab_name: str | None,
          to_id: str, to_prefab_name: str | None}], lost: list[{from_id: str, from_prefab_name: str | None, to_id: str, to_prefab_name: str | None}],
          model_differences: list[{from_id: str, from_prefab_name: str | None, to_id: str, to_prefab_name: str | None}], mounted: list[{device_id:
          str, piece_id: str, attached_before: bool, attached_after: bool | None}]} | None
      notes: list[str] | None
    (JobBusy)
      JobBusy
    (UpgradeJob)
      UpgradeJob
    (JobDropped)
      JobDropped
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Job views, one per help call (help {topic: "types", subtopic: "<name>"} or sg.help('types', '<name>')): JobBusy, JobDropped, JobQueued, UpgradeJob
    Example: sg.swap('cable', network='3768865', into='super_heavy')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
