"""Reply dicts and their main list (SPEC note 23, hub.md *Faces*).

A verb reply is a `Reply` (a dict). Iterating one is almost always a mistake (`for t in sg.find('thing')` walks its
keys), so it raises TypeError naming the list to iterate instead.

The reply's main list (the key the hub names in `main`, else the what's `reply.list`) is a `MainList`: the page the
call answered. When the hub has more entries it adds `cursor`; the rest comes only when asked for: `walk()` yields
every entry page by page with the hub's `more` method, `all()` pages in the rest. Nothing else fetches a page.

Every dict in a reply, at any depth, is a `Row`: a missing key raises KeyError carrying the keys that dict has, so a
failed run can say which key was meant (note 22).
"""
from . import _session


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

    def __init__(self, entries=(), cursor=None):
        super().__init__(entries)
        self._cursor = cursor

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
                return
            self._next_page()

    def all(self):
        """Pages in every remaining entry and returns this list, now whole."""
        while self._cursor is not None:
            self._next_page()
        return self


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
        dict.__setitem__(reply, key, MainList(dict.__getitem__(reply, key), result.get("cursor")))
        reply._main = key
    return reply


def plain(value):
    """value with every MainList as the list of its fetched entries, and every Reply or Row as a dict (for JSON). It
    fetches no page: a list with more behind its cursor stays as fetched."""
    if isinstance(value, dict):
        return {key: plain(item) for key, item in dict.items(value)}
    if isinstance(value, (list, tuple)):
        return [plain(item) for item in list.__iter__(value)] if isinstance(value, MainList) \
            else [plain(item) for item in value]
    return value
