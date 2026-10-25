def remove(what: Literal['structure'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # The structures; or give box.
    box: list = None,  # Every structure whose position lies in it (at most 32 m a side, 256 found), in one job; with id, both.
    prefab: str | int | list = None,  # With box: only these prefabs.
    allow: list[Literal['contents', 'breach', 'broken', 'burst', 'unsupported']] = None,  # contents: items drop, gas goes; breach: joins spaces 1 kPa or more apart; broken: nothing back; burst; unsupported.
    refund_to: str | int | list = None,  # Where removed materials go: a word (inventory, source, storage, ground, none), a container id, or a list tried in turn. (default [inventory, source, storage, ground]) (default [inventory, source, storage, ground])
    materials: str = None,  # The one holder for source and storage refunds: an id or "player". Default the player.
    gas_to: str = None,  # Keep a removed device's gas: connected, or a pipe network id.
    include: list[Literal['notes']] = None,  # Parts to add to the reply.
    verbose: bool = False,  # Dry run: each port a split cuts and each removal's own refund; real run: the whole job.
    acknowledge_gas_lost: str = None,  # The job id whose gas loss you accept (with the user's agreement); lifts the gas hold.
) -> dict:  # main list: r['removals']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Built structures by id or every one in a box (up to 256, one job), taken apart; every build state is refunded down to the kit.
    A box takes everything in it, frames and walls included: name prefab to keep to the pieces you mean, and read the report before confirm.
    Reports in a report run; acts in a confirmed run.
    Reply, one of:
    (JobQueued)
      JobQueued
    (BuildJob)
      BuildJob
    (RemoveReport)
      ready: bool
      problems: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      warnings: list[{code: str, message: str, index: int | None, reference_id: str | None}]
      removals: list[{index: int, reference_id: str, prefab_name: str | None, display_name: str | None, position: Vec3, build_state: int, kind: str,
          refund: list[{prefab_name: str | None, quantity: int}] | None}]
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
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
