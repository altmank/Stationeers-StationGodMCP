"""The sg helpers that are not verbs: wait, watch, hold, ref and help (hub.md, *The hub protocol* and *Runs*)."""
import time

from . import _session
from ._catalogue import VERBS
from ._errors import Error, Stopped, _PassEnd
from ._replies import Reply, wrap
from ._verbs import read as _read

LIGHT_POLL_S = 1.0
HEAVY_POLL_S = 5.0


def wait(s):
    """Waits s seconds and returns None. Inside a run, time.sleep is this function. After a stop the hub answers at
    once (the run is unwinding), so a wait in a finally block never hangs."""
    _session.request("wait", {"s": float(s), "kind": "wait", "in_loop": _session.in_loop()})
    return None


def watch(reads, until, timeout):
    """Reads `read logic` with reads until until(rows) is true or timeout seconds pass; returns (rows, met).

    rows are the last read's rows, one per reads entry, in order. It never raises at timeout. It polls once a second,
    or every 5 s when an entry asks for atmosphere (a heavy read). In a report run the hub answers the watch's wait
    with {"instant": true} and the watch returns its current rows as met. After a stop it reads once and returns,
    because waits no longer wait and polling would spin."""
    interval = HEAVY_POLL_S if any(isinstance(entry, dict) and entry.get("atmosphere") for entry in reads) \
        else LIGHT_POLL_S
    started = time.monotonic()
    rows = _watch_rows(reads)
    while True:
        if until(rows):
            return rows, True
        run = _session.current_run()
        if run is not None and run.stopped:
            return rows, False
        left = timeout - (time.monotonic() - started)
        if left <= 0:
            return rows, False
        answer = _session.request("wait", {"s": min(interval, left), "kind": "watch",
                                           "in_loop": _session.in_loop()})
        if isinstance(answer, dict) and answer.get("instant"):
            return rows, True
        rows = _watch_rows(reads)


def _watch_rows(reads):
    reply = _read("logic", reads=reads)
    return list(reply.get("results") or [])


class hold:
    """with sg.hold(ids, logic, value=None, after=None): writes value now and restores after on exit (normal,
    exception or stop). after left out: the hub reads the current values first. The hub registers the restore with
    the mod before it writes, so it is also applied if the session ends abruptly.

    The context value is the hold itself: `.after` (the values to restore, from the hub) and, after the block,
    `.rows` (the restore's rows)."""

    def __init__(self, ids, logic, value=None, after=None):
        self.ids = [ids] if isinstance(ids, (str, int)) else list(ids)  # any other iterable (a set too) is a list
        self.logic = logic
        self.value = value
        self.after = after
        self.rows = None

    def _params(self, phase, after):
        params = {"phase": phase, "ids": self.ids, "logic": self.logic}
        if self.value is not None:
            params["value"] = self.value
        if after is not None:
            params["after"] = after
        return params

    def __enter__(self):
        result = _session.request("hold", self._params("enter", self.after)) or {}
        self.after = result.get("after", self.after)
        return self

    def __exit__(self, exc_type, exc, traceback):
        try:
            result = self._exit()
        except Error:
            if exc is not None:
                return False  # the block's own exception matters more; the hub reports the refused restore
            raise
        self.rows = result.get("rows")
        return False

    def _exit(self):
        try:
            return _session.request("hold", self._params("exit", self.after)) or {}
        except (Stopped, _PassEnd) as stop:
            # The stop arrived on this exit call, so the restore may not have acted. After a stop the hub lets hold
            # exits act (as CLEANUP), so send it once more, then let the stop go on up. If that is refused too (after a
            # world change no cleanup acts), the stop is still what ended the run.
            try:
                result = _session.request("hold", self._params("exit", self.after)) or {}
                self.rows = result.get("rows")
            except Error:
                pass
            raise stop


def ref(id):
    """A ref's content: for rN {ref, ids, reply}; for script-N {ref, returned, printed}."""
    return wrap(_session.request("ref", {"id": id}))


def help(verb=None, what=None):
    """StationGod's help for scripts, as text: sg.help() lists verbs and topics; sg.help('find') a verb's whats;
    sg.help('find', 'thing') one what's Python stub; sg.help('scripts') a topic; sg.help('types', 'BuildJob') a
    subtopic."""
    args = {"lang": "python"}
    if verb is not None and verb not in VERBS:
        args["topic"] = verb
        if what is not None:
            args["subtopic"] = what
    else:
        if verb is not None:
            args["verb"] = verb
        if what is not None:
            args["what"] = what
    result = _session.request("verb", {"verb": "help", "args": args},
                              call={"verb": "help", "what": None, "args": args})
    if isinstance(result, dict) and isinstance(result.get("text"), str):
        return result["text"]
    return Reply(result) if isinstance(result, dict) else result
