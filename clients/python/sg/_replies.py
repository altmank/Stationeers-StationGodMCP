"""Reply dicts and their main list (SPEC note 23, hub.md *Faces*).

A verb reply is a `Reply` (a dict). Iterating one is almost always a mistake (`for t in sg.find('thing')` walks its
keys), so it raises TypeError naming the list to iterate instead.

The reply's main list (the key the hub names in `main`, else the what's `reply.list`) is a `MainList`. When the hub
has more entries than the first page it adds `cursor`; then a single `for` walks page by page with the hub's `more`
method, and anything that needs the whole list first (len, an index, `in`, sorted, reversed, a second pass) pages in
the rest. The pages fetched are kept, so after the first full walk it is a plain list.

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
    """A reply's main list that may have more pages behind `cursor`. A single `for` walks it page by page; every other
    use pages in the rest first."""

    def __init__(self, entries=(), cursor=None):
        super().__init__(entries)
        self._cursor = cursor

    # ----- paging -----

    def _next_page(self):
        """Fetches one more page with the hub's `more` method and appends it."""
        result = _session.request("more", {"cursor": self._cursor})
        self._cursor = result.get("cursor")
        list.extend(self, (_row(entry) for entry in result.get("entries") or []))

    def _page_in_all(self):
        while self._cursor is not None:
            self._next_page()

    def __iter__(self):
        index = 0
        while True:
            while index < list.__len__(self):
                yield list.__getitem__(self, index)
                index += 1
            if self._cursor is None:
                return
            self._next_page()

    # ----- uses that need the whole list -----

    def __len__(self):
        self._page_in_all()
        return list.__len__(self)

    def __bool__(self):
        return list.__len__(self) > 0 or self._cursor is not None

    def __getitem__(self, index):
        self._page_in_all()
        return list.__getitem__(self, index)

    def __contains__(self, value):
        self._page_in_all()
        return list.__contains__(self, value)

    def __reversed__(self):
        self._page_in_all()
        return list.__reversed__(self)

    def __eq__(self, other):
        self._page_in_all()
        return list.__eq__(self, other)

    def __ne__(self, other):
        return not self.__eq__(other)

    __hash__ = None

    def __repr__(self):
        self._page_in_all()
        return list.__repr__(self)

    def __add__(self, other):
        self._page_in_all()
        return list(self) + list(other)

    def __radd__(self, other):
        self._page_in_all()
        return list(other) + list(self)

    def __copy__(self):
        self._page_in_all()
        return list(self)

    def __reduce_ex__(self, protocol):
        """copy.deepcopy and pickle see the whole list, as a plain list."""
        self._page_in_all()
        return (list, (list(list.__iter__(self)),))

    def __mul__(self, count):
        self._page_in_all()
        return list(self) * count

    def __setitem__(self, index, value):
        self._page_in_all()
        list.__setitem__(self, index, value)

    def __delitem__(self, index):
        self._page_in_all()
        list.__delitem__(self, index)

    def __iadd__(self, other):
        self._page_in_all()
        list.extend(self, other)
        return self

    def append(self, value):
        self._page_in_all()
        list.append(self, value)

    def extend(self, values):
        self._page_in_all()
        list.extend(self, values)

    def insert(self, index, value):
        self._page_in_all()
        list.insert(self, index, value)

    def pop(self, index=-1):
        self._page_in_all()
        return list.pop(self, index)

    def remove(self, value):
        self._page_in_all()
        list.remove(self, value)

    def index(self, value, *args):
        self._page_in_all()
        return list.index(self, value, *args)

    def count(self, value):
        self._page_in_all()
        return list.count(self, value)

    def sort(self, *, key=None, reverse=False):
        self._page_in_all()
        list.sort(self, key=key, reverse=reverse)

    def reverse(self):
        self._page_in_all()
        list.reverse(self)

    def copy(self):
        self._page_in_all()
        return list(self)


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
    """value with every MainList paged in and turned into a list, and every Reply or Row into a dict (for JSON)."""
    if isinstance(value, dict):
        return {key: plain(item) for key, item in dict.items(value)}
    if isinstance(value, MainList):
        value._page_in_all()
        return [plain(item) for item in list.__iter__(value)]
    if isinstance(value, (list, tuple)):
        return [plain(item) for item in value]
    return value
