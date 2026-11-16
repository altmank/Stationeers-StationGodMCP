def write(what: Literal['interact'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # One id, or "looking_at".
    interactable: str | int,  # Its name or index (inspect device include interactables).
    state: int = None,  # The state to leave it in; default what a click does.
    hand: Literal['empty', 'held'] = 'empty',  # held: press with what the player holds.
    force: bool = False,  # Press from out of reach. Cheat.
    as_player: str = None,  # The player the call is for, by name or id: near "player", looking_at, materials or to "player", show's screen. Needed with several players on a dedicated server; show without it draws for each.
) -> dict:
    """Press a thing's switch or button as the player does.
    The game is asked first whether the press works (not_accepted gives its own text); then it is made. Out of reach (3 m from the eye) is out_of_range: move player {to, stand} first.
    Cheat when force: true: ask the user first.
    Reply:
      thing: Thing
      interactable: {name: str, index: int}
      before: int
      after: int
      player: str
      message: str | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('interact', id='4860097', interactable='OnOff')
    """
