def inspect(what: Literal['script'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    name: str,  # The saved script's name.
) -> dict:
    """One saved script in full: its source, parameters, version and where it was last run.
    source is the file as saved (the header, then the code); parameters is main(...)'s signature, one line per parameter; last_used names the run that last ran it and when (null when none did). A world script is shown only in the world it was saved in.
    Reply:
      name: str
      scope: str
      world: str | None (world scope)
      version: int
      description: str
      parameters: list[str] (each line as the signature reads)
      file: str
      source: str
      last_used: {run: str, at: str} | None
    Example: sg.inspect('script', name='greet')
    """
