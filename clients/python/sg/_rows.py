"""sg.ok and sg.failed: the per-row results of a reply.

A list of ids answers {results: [{index, ok, reference_id, ...}]} on every face (catalogue/verbs/write.json:69); one id
answers the what's one-id shape on the script face and {results: [...]} on the program face (hub.md, *Faces*). Both
helpers take either: a reply with no results list is one row that succeeded (a refused one-id call raises sg.Error at
the call itself).
"""
from . import _trace
from ._errors import Error
from ._replies import MainList


def _results(reply):
    """reply's result rows, every page of them, or None when reply has no results list."""
    try:
        results = reply["results"]
    except (KeyError, TypeError, IndexError):
        return None
    if isinstance(results, MainList):
        return list(results.all())
    return list(results) if isinstance(results, list) else None


def failed(reply):
    """The rows of reply's results whose `ok` is false; [] when none or when reply has no results list."""
    results = _results(reply)
    return [] if results is None else [row for row in results if not row.get("ok", True)]


def ok(reply):
    """reply's result rows when every one is ok ([reply] when it has no results list); else one sg.Error
    "rows_failed" listing each failed row's index, reference_id, error code and message, and its fix when it has one.
    The failed rows are the error's data."""
    results = _results(reply)
    if results is None:
        return [reply]
    bad = [row for row in results if not row.get("ok", True)]
    if not bad:
        return results
    error = Error("rows_failed", f"{len(bad)} of {len(results)} rows failed: " + "; ".join(map(_line, bad)),
                  "send the failed rows again after their fixes", data=bad)
    _trace.annotate(error)
    raise error


def _line(row):
    # A row error is the shared Error type {code, message, data, see} (catalogue/verbs/_shared.json:178); a fix is
    # shown only when the row carries one.
    error = row.get("error") or {}
    text = f"row {row.get('index')} ({row.get('reference_id')}): {error.get('code')}"
    if error.get("message"):
        text += f": {error.get('message')}"
    if error.get("fix"):
        text += f" (fix: {error.get('fix')})"
    return text
