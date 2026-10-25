def write(what: Literal['lattice'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    command: dict,  # One command, as help topic lattice writes it ({place: [...]}, {batch: [...]}).
) -> dict:
    """Any Lattice command: place, remove, change, move ghosts; plans; runs and routes; swaps, removals, finishes, splits, transforms, templates; repair, take down, reopen, revert, batch; executors.
    Reports Lattice's preview (what it would change, findings, bill, effects, a run's pieces) until confirm; confirm submits it and answers the change set. Ghosts are the agent's (StationGod agent) unless made_by says otherwise. Undo with {revert: change_set}.
    Reports in a report run; acts in a confirmed run.
    Reply:
      outcome: str
      change_set: Any | None
      preview: Any | None
      refusals: list[str]
      notes: list[str]
    Example: sg.write('lattice', command={'plan_route': {'kit': 'ItemPipe', 'from': [10.25, 1.25, 4.25], 'to': [14.25, 1.25, 4.25], 'to_port': {'ghost': 812, 'port': 0}}})
    """
