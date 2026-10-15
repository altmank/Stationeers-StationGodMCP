def find(what: Literal['script'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    text: str = None,  # Keep scripts whose name or description holds this text, any case.
) -> dict:  # main list: r['scripts']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """The saved scripts a run in this world can see: name, description, parameters and version.
    A run sees the shared scripts and the scripts of the world it runs in (inspect script shows one in full; write script saves them). The parameters are main(...)'s, one line each as its signature reads; run_script {name, args} checks its arguments against them.
    Reply:
      scripts: list[{name: str, scope: str, world: str | None, version: int, description: str, parameters: list[str]}] (one line per parameter, as
          main's signature reads)
      count: int
    Example: sg.find('script', text='greet')
    """
