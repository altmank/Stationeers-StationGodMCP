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


def call_text(call):
    """A call as the Python that sent it, e.g. sg.write('logic', id='122643', logic='On', value=1)."""
    if not isinstance(call, dict):
        return repr(call)
    if "verb" not in call:
        return repr(call)
    parts = [repr(call["what"])] if call.get("what") is not None else []
    parts += [f"{name}={value!r}" for name, value in (call.get("args") or {}).items()]
    return f"sg.{call['verb']}({', '.join(parts)})"
