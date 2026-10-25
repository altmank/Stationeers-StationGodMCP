def find(what: Literal['lattice'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    ask: Literal['ghosts', 'plans', 'plan', 'load', 'template', 'validation', 'steps', 'bill', 'swaps', 'analysis', 'preview', 'executors', 'story', 'export', 'catalogue', 'ports', 'facing', 'families', 'groups', 'modules', 'world', 'explain', 'matches', 'pick', 'clip', 'events'],  # What to ask; help topic lattice gives each ask's args.
    args: dict = None,  # The ask's arguments.
) -> dict:
    """Anything Lattice answers: every ghost (any intent), plans and their facts, validation, steps, bills, swaps, analysis, previews, executors, stories, the catalogue, ports, the world, styles, picks and events.
    Answers are Lattice's own values as JSON (help topic lattice): cells in metres, turns as rotation degrees, pieces by prefab name. An answer Lattice is still working out is pending: true; ask again. Needs Lattice.
    Reply:
      ask: str
      answer: Any
      pending: bool
      notes: list[str]
    Example: sg.find('lattice', ask='plan', args={'plan': 3})
    """
