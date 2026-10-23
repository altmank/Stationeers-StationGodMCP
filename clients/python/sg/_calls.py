"""The verb call every generated function in _verbs.py makes, and how a call is written back as Python text."""
from . import _session
from ._catalogue import MAIN_LISTS
from ._replies import wrap


def verb_call(verb, what, options, extra):
    """Sends `verb {verb, what, args}`; options are the function's keyword options (None meaning left out) and extra
    the **opts it did not name. Returns the reply as a Reply whose main list pages on demand."""
    args = {name: value for name, value in options.items() if value is not None}
    args.update(extra)
    params = {"verb": verb, "args": args}
    if what is not None:
        params["what"] = what
    call = {"verb": verb, "what": what, "args": args}
    result = _session.request("verb", params, call=call)
    return wrap(result, MAIN_LISTS.get((verb, what if what is not None else "-")))


# How much of a call an error echoes back: each value's text, and the whole call's. A failed call that carried a
# large list (168 placements) would otherwise put tens of KB of its own arguments into the run's reply.
VALUE_LIMIT = 120
CALL_LIMIT = 400


def call_text(call):
    """A call as the Python that sent it, e.g. sg.write('logic', id='122643', logic='On', value=1); a long value
    shows its start and size, and the whole text is cut at CALL_LIMIT characters."""
    if not isinstance(call, dict):
        return _cut(repr(call), CALL_LIMIT)
    if "verb" not in call:
        return _cut(repr(call), CALL_LIMIT)
    parts = [repr(call["what"])] if call.get("what") is not None else []
    parts += [f"{name}={_value_text(value)}" for name, value in (call.get("args") or {}).items()]
    return _cut(f"sg.{call['verb']}({', '.join(parts)})", CALL_LIMIT)


def _value_text(value):
    text = repr(value)
    if len(text) <= VALUE_LIMIT:
        return text
    size = f"{len(value)} items, " if isinstance(value, (list, tuple, dict)) else ""
    return f"{text[:VALUE_LIMIT]}... ({size}{len(text)} chars)"


def _cut(text, limit):
    return text if len(text) <= limit else f"{text[:limit]}... ({len(text)} chars)"
