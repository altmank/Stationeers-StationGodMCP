def write(what: Literal['label'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One id; a list names each '<label> 1', '<label> 2' in id order.
    label: str = None,  # The new name; empty gives back the game's name.
    labels: list = None,  # Several renames, each {id, label}, in order.
) -> dict:  # main list: r['results']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Rename things as the hand Labeller does; empty name gives back the game's name.
    Pipe, cable and chute pieces cannot be renamed (not_labelable, per entry).
    Reply, one of:
    (Labelled)
      index: int
      ok: bool
      reference_id: str
      prefab_name: str | None
      written: str
      sent_to_host: bool
      previous: {display_name: str | None, custom_name: str | None}
      current: {display_name: str | None, custom_name: str | None}
    (BatchResult)
      results: list[{index: int, ok: bool, reference_id: str, prefab_name: str | None, written: str, sent_to_host: bool, previous: {display_name: str
          | None, custom_name: str | None}, current: {display_name: str | None, custom_name: str | None}} | {index: int, ok: bool, reference_id: str,
          error: Error}]
      count: int
      success_count: int
      error_count: int
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}
    Example: sg.write('label', id='1199811', label='O2 Tank 1')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
