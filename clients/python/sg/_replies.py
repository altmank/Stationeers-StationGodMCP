"""Reply dicts and their main list (SPEC note 23, hub.md *Faces*).

A verb reply is a `Reply` (a dict). Iterating one is almost always a mistake (`for t in sg.find('thing')` walks its
keys), so it raises TypeError naming the list to iterate instead.

The reply's main list (the key the hub names in `main`, else the what's `reply.list`) is a `MainList`: the page the
call answered. When the hub has more entries it adds `cursor`; the rest comes only when asked for: `walk()` yields
every entry page by page with the hub's `more` method, `all()` pages in the rest. Nothing else fetches a page.

Every dict in a reply, at any depth, is a `Row`: a missing key raises KeyError carrying the keys that dict has, so a
failed run can say which key was meant (note 22).

Any other list the game cut (the reply's `truncated` names it: a top-level key, or a path such as
`results[].slots` for a list in each entry) is a `CutList`: using it raises sg.Error("truncated", fix=<how to get
the rest>), so a script never acts on part of a list without knowing (review D, R-D-18).
"""
from . import _session
from ._errors import Error


class Row(dict):
    """A dict from a reply whose missing keys say which keys there are (for the failed-run text)."""

    def __missing__(self, key):
        error = KeyError(key)
        error.reply_keys = list(self.keys())
        raise error


class Reply(Row):
    """A verb reply. Index it by key; iterating it raises TypeError naming its main list."""

    _main = None

    def __iter__(self):
        main = self._main if isinstance(self.get(self._main), list) else _first_list_key(self)
        if main is not None:
            raise TypeError(f"a reply dict is not iterable: iterate r['{main}'] (the main list)")
        raise TypeError("a reply dict is not iterable; its keys: " + ", ".join(dict.keys(self)))


def _first_list_key(reply):
    for key in ("results",) + tuple(dict.keys(reply)):
        if isinstance(dict.get(reply, key), list):
            return key
    return None


class MainList(list):
    """A reply's main list: the page the call answered. When the hub has more entries behind `cursor`, the rest is
    fetched only when the caller asks for it (cost design A4): `walk()` goes through every entry page by page,
    `all()` pages in the rest and returns the whole list. Everything else (a `for`, len, an index, `in`, sorted,
    copy) sees only the entries fetched so far, so reading a reply never walks a list. `more` says whether entries
    remain behind the cursor."""

    def __init__(self, entries=(), cursor=None, rest=None):
        super().__init__(entries)
        self._cursor = cursor
        self._rest = rest if cursor is None else None

    def _unreachable(self):
        """Raises sg.Error("truncated") when the game held entries back that no page reaches (the hub's
        rest_unreachable, or the reply's truncated note on this list): the list is not whole, and walk() or all()
        never hand it over as if it were."""
        if self._rest is not None:
            note = self._rest
            raise Error("truncated",
                        f"{note.get('list')}: the game sent {note.get('returned')} of "
                        f"{'at least ' if note.get('at_least') else ''}{note.get('total')} entries and no page "
                        "reaches the rest", fix=note.get("more"), data=dict(note))

    @property
    def more(self):
        """True while entries remain that walk() or all() would fetch."""
        return self._cursor is not None

    def _next_page(self):
        """Fetches one more page with the hub's `more` method and appends it."""
        result = _session.request("more", {"cursor": self._cursor})
        self._cursor = result.get("cursor")
        list.extend(self, (_row(entry) for entry in result.get("entries") or []))

    def walk(self):
        """Every entry, the fetched ones first, then page by page with the hub's `more` until the list ends; the
        pages fetched are kept, so a later walk() or all() fetches nothing again."""
        index = 0
        while True:
            while index < list.__len__(self):
                yield list.__getitem__(self, index)
                index += 1
            if self._cursor is None:
                self._unreachable()
                return
            self._next_page()

    def all(self):
        """Pages in every remaining entry and returns this list, now whole; raises sg.Error("truncated") when the game
        held back entries no page reaches."""
        while self._cursor is not None:
            self._next_page()
        self._unreachable()
        return self


class CutList(list):
    """A list the game cut (the reply's `truncated` names it). Iterating it, its len, an index, `in` or its truth
    raise sg.Error("truncated") whose fix says how to get the whole list; `entries` gives the part that came, for a
    script that wants it anyway."""

    def __init__(self, entries, note):
        super().__init__(entries)
        self._note = note

    @property
    def entries(self):
        """The entries that came, as a plain list."""
        return list(list.__iter__(self))

    def _refuse(self):
        note = self._note
        total = note.get("total")
        returned = note.get("returned")
        raise Error("truncated",
                    f"{note.get('list')}: the game sent {returned} of {'at least ' if note.get('at_least') else ''}"
                    f"{total} entries; .entries is the part that came",
                    fix=note.get("more"), data=dict(note))

    def __iter__(self):
        self._refuse()

    def __len__(self):
        self._refuse()

    def __getitem__(self, index):
        self._refuse()

    def __contains__(self, value):
        self._refuse()

    def __bool__(self):
        self._refuse()


def _cut(value, steps, note):
    """Makes the list at steps (a truncated path's parts; "x[]" walks each entry of list x) a CutList, in place."""
    if not steps or not isinstance(value, dict):
        return
    step, rest = steps[0], steps[1:]
    each = step.endswith("[]")
    key = step[:-2] if each else step
    found = dict.get(value, key)
    if isinstance(found, dict) and not each and rest:
        _cut(found, rest, note)
        return
    if not isinstance(found, list) or isinstance(found, CutList):
        return
    if each:
        for entry in list.__iter__(found):
            _cut(entry, rest, note)
    elif not rest:
        if not isinstance(found, MainList):
            dict.__setitem__(value, key, CutList(list.__iter__(found), note))
    else:
        for entry in list.__iter__(found):
            _cut(entry, rest, note)


def _row(value):
    """value with every dict in it, at any depth, a Row (so a missing key names the keys there are)."""
    if isinstance(value, dict):
        return Row((key, _row(item)) for key, item in dict.items(value))
    if isinstance(value, list) and not isinstance(value, MainList):
        return [_row(item) for item in value]
    return value


def wrap(result, main=None):
    """A verb result as sg gives it: a Reply whose main list (the hub's `main`, else `main` from the catalogue) is a
    MainList of Rows. Anything that is not a dict is returned as it is."""
    if not isinstance(result, dict):
        return result
    reply = Reply((key, _row(item)) for key, item in result.items())
    key = result.get("main") if isinstance(result.get("main"), str) else main
    if key is None or not isinstance(result.get(key), list):
        key = "results" if isinstance(result.get("results"), list) else key
    if key is not None and isinstance(result.get(key), list):
        dict.__setitem__(reply, key, MainList(dict.__getitem__(reply, key), result.get("cursor"),
                                              _rest_of(result, key)))
        reply._main = key
    for note in result.get("truncated") or []:
        if isinstance(note, dict) and isinstance(note.get("list"), str) and note["list"] != key:
            _cut(reply, note["list"].split("."), note)
    return reply


def _rest_of(result, key):
    """The note of the main list's entries no page reaches: the reply's truncated note on it, else the hub's
    rest_unreachable; None when the list is whole or pages on."""
    for note in result.get("truncated") or []:
        if isinstance(note, dict) and note.get("list") == key:
            return note
    if isinstance(result.get("rest_unreachable"), str):
        return {"list": key, "returned": len(result.get(key)), "total": None, "more": result["rest_unreachable"]}
    return None


def plain(value):
    """value with every MainList as the list of its fetched entries, and every Reply or Row as a dict (for JSON). It
    fetches no page: a list with more behind its cursor stays as fetched."""
    if isinstance(value, dict):
        return {key: plain(item) for key, item in dict.items(value)}
    if isinstance(value, (list, tuple)):
        return [plain(item) for item in list.__iter__(value)] if isinstance(value, (MainList, CutList)) \
            else [plain(item) for item in value]
    return value
