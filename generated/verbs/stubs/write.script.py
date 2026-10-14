def write(what: Literal['script'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    name: str,  # The script's name: letters, digits and underscores (it names a file and an sg module).
    code: str,  # The Python source; it must define main(...), with type hints on the parameters run_script checks.
    description: str,  # One line saying what the script does (find script shows it).
    scope: Literal['shared', 'world'],  # shared: runs in any world, and may hard-code no ids; world: bound to the world it is saved in.
) -> dict:
    """Save a Python script under a name: the next version, every older one kept.
    The hub saves it, not the game: under the server's --scripts-dir, in shared/ or world/<world id>/. Saving a name again writes the next version as <name>/v<N>.py and makes <name>.py it; every older version stays. The code is parsed once here: a script that does not parse, has no def main(...), or has positional-only parameters is refused. A shared script may not hard-code ids: a constant written as an id, an integer of 100000 and up, or digits that big written as a string are all refused. Not caught: an id built at run time, one below 100000 reached through a variable, and composite ids like '5-1234'. A world script belongs to the world it was saved in. A name is a Python module name, and may not collide with a script of the other scope a run can see, or with either in the same scope however it is cased.
    Reply:
      name: str
      scope: str
      world: str | None (world scope)
      version: int
      parameters: list[str] (each line as the signature reads: 'who: str', '*extra')
      file: str
    Example: sg.write('script', name='greet', code='def main(who: str) -> str: return \'hi \' + who', description='greet a player by name', scope='shared')
    """
