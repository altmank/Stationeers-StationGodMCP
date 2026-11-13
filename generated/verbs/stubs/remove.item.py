def remove(what: Literal['item'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The items: ids, a list, or a ref.
    allow: list[Literal['contents', 'carried']] = None,  # contents: an item holding items is deleted with them; carried: an item a player carries (hands, inventory, suit) may go.
    include: list[Literal['notes']] = None,  # Parts to add to the reply.
) -> dict:  # main list: r['results']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Delete loose items by id (up to 256): on the ground, in a slot, or carried, nothing refunded.
    A cheat (cheats_off outside creative unless the user turned cheats on): no player deletes an item outright, and nothing is refunded. Reports until confirm: each item that would go [id, name, where: ground, in <holder> slot N, carried by <player>], and each refused id as a problem. A structure, a player or an entity is refused (remove structure takes structures down); a carried item needs allow carried, an item holding items needs allow contents (those go too and are listed). A refused id never stops the others. Cannot be undone: build item makes new ones.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply, one of:
    ()
      results: list[{index: int, ok: bool, reference_id: str, name: str, where: str, contents: list[{reference_id: str, name: str, where: str}] |
          None} | {index: int, ok: bool, reference_id: str | None, error: Error}]
      count: int
      success_count: int
      error_count: int
    (ItemReport)
      items: int
      ids: int
      notes: list[str] | None
      problems: list[{code: str, message: str, index: int | None, reference_id: str | None}]
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}
    Example: sg.remove('item', id=['5200100', '5200101'])
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
