def show(what: Literal['lattice'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    layer: dict = None,  # {add: layer} | {enable|disable|remove: name} | {library: name, enabled}; help topic lattice.
    set: dict = None,  # {id, put|replace|add|remove: ghost ids} or {id, clear: true}; structures: true for a structure set.
    drafts: dict = None,  # {id, show: [drafts]} | {id, preview: command} | {id, clear: true}.
    clip: dict = None,  # {plane: {point, normal}} | {box: [corner, opposite]} | {move: shape} | {clear: true}.
    highlight: dict = None,  # {targets, style, seconds}: targets {ghosts|picked|structures}.
) -> dict:
    """Lattice's view on this seat (never synced): style layers, ghost and structure sets, drafts, the clip, highlights.
    One of layer, set, drafts, clip or highlight per call. Everything here is this seat's drawing only: it never syncs, saves or changes what others see (ghost data does: write lattice). Layers are StationGod's own (agent-<name>); library layers: blueprint, step_numbers, bill_counts, reasons, plan_names. Forms: help topic lattice.
    Reply:
      done: str
      layers: list[str]
      notes: list[str]
    Example: sg.show('lattice', highlight={'targets': {'picked': {'plan': 3}}, 'style': {'glow': {'lit': {'paint': 'select', 'strength': 0.9}}}, 'seconds': 10})
    """
