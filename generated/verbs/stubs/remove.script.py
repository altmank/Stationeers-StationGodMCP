def remove(what: Literal['script'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    name: str,  # The saved script's name.
    scope: Literal['shared', 'world'] = None,  # Which copy: shared, or this world's; left out, the one a run would open. A name saved in both needs it.
) -> dict:
    """Delete a saved script and every version kept beside it.
    The hub removes it, not the game: <name>.py and the <name>/ folder of versions under the script store's scope. A world script is removed only in the world it was saved in; an unknown name is refused with the nearest names.
    Reply:
      name: str
      scope: str
      world: str | None (world scope)
      version: int
      removed: bool
    Example: sg.remove('script', name='greet')
    """
