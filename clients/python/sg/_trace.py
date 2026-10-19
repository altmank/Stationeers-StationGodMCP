"""The call trace: the last 20 hub calls sg made, and the last 5 appended to an sg.Error's message.

Each entry is {verb, what, args, ms, ok, code}: verb and what from a `verb` call's params (any other hub method is
recorded under its method name, what None), args as short text (at most 120 chars), ms the call's wall time, ok the
reply's ok, code the error object's code when refused (hub.md:82 - a hub-protocol error is the mod's error object;
sg/_errors.py from_error reads its `code`). A call whose transport raised is recorded too: ok false, code the raised
sg.Error's code (the exception's class name for anything else).

An annotated sg.Error keeps the lines it was given in `last_calls`, so a run's failed text (_failure.failed_text) can
put them after the call and the values instead of in the middle of its message line.
"""
import collections
import json

SIZE = 20
SHOWN = 5
ARGS_MAX = 120

_ring = collections.deque(maxlen=SIZE)


def _short(value):
    try:
        text = json.dumps(value, separators=(",", ":"), default=repr, ensure_ascii=False)
    except (TypeError, ValueError):
        text = repr(value)
    return text if len(text) <= ARGS_MAX else text[:ARGS_MAX - 3] + "..."


def _names(method, params):
    if method == "verb":
        return params.get("verb"), params.get("what"), params.get("args")
    return method, None, {key: value for key, value in params.items() if key != "line"}


def record(method, params, ms, reply):
    """Adds one call to the ring; reply is the hub's reply envelope ({ok, result} or {ok: false, error})."""
    verb, what, args = _names(method, params)
    is_ok = bool(reply.get("ok")) if isinstance(reply, dict) else False
    error = reply.get("error") if isinstance(reply, dict) else None
    code = None if is_ok else (error.get("code") if isinstance(error, dict) else None)
    _ring.append({"verb": verb, "what": what, "args": _short(args), "ms": round(ms, 1), "ok": is_ok, "code": code})


def record_raised(method, params, ms, exception):
    """Adds one call whose transport raised instead of answering: ok false, code from the exception."""
    verb, what, args = _names(method, params)
    code = getattr(exception, "code", None) or type(exception).__name__
    _ring.append({"verb": verb, "what": what, "args": _short(args), "ms": round(ms, 1), "ok": False, "code": code})


def _line(entry):
    name = entry["verb"] if entry["what"] is None else f"{entry['verb']}.{entry['what']}"
    state = "ok" if entry["ok"] else f"error {entry['code']}"
    return f"  {name} {entry['args']} {entry['ms']}ms {state}"


def annotate(exception):
    """Appends the last 5 calls (one line each) to an sg.Error's message (its str()) and keeps them in
    exception.last_calls; an error already annotated is left as it is."""
    if getattr(exception, "last_calls", None) is not None:
        return
    lines = [_line(entry) for entry in list(_ring)[-SHOWN:]]
    exception.last_calls = lines
    if lines and exception.args:
        exception.args = (f"{exception.args[0]}\nlast calls:\n" + "\n".join(lines),) + exception.args[1:]


def trace():
    """The last 20 calls, newest last."""
    return [dict(entry) for entry in _ring]


def clear():
    _ring.clear()
