def server(what: Literal['command'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    command: str,  # The whole command line.
    lines: int = 100,  # Most lines returned (the oldest kept).
) -> dict:  # main list: r['output']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Run any console command and answer what it printed: spawn, delete, teleport, storms, settings.
    Read-only commands (help, rocket print) also only report in a report run; the game does not mark which commands change nothing.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      command: str
      command_key: str | None
      scope: str
      help_text: str | None
      arguments: list[str] | None
      run_simulation: bool
      console_available: bool
      completes_asynchronously: bool
      output_complete: bool
      output_line_count: int
      concurrent_log_lines: int
      output: list[{time: str | None, level: str | None, text: str | None}]
      note: str
    Example: sg.server('command', command='help')
    """
