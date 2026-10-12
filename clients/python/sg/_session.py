"""Where sg calls go: the current transport, the current run (if any), and the one function that sends a call.

Every sg call carries `params.line`, the line of the run's code that made it (hub.md, *Runs*). The worker compiles the
code under the file name "<script>", so the line is found by walking up the stack to the innermost frame from that
file. Outside a run (a program using sg.connect()) there is no such frame and the line is None.
"""
import sys

from ._errors import Error, Stopped, _PassEnd, from_error

SCRIPT_FILENAME = "<script>"


class Run:
    """What sg needs to know about the run in progress: its id, the lines inside a while body (a wait there counts
    as a loop wait in a report run), and whether a stop has already been delivered."""

    def __init__(self, run_id=None, while_lines=()):
        self.id = run_id
        self.while_lines = frozenset(while_lines)
        self.stopped = False


_transport = None
_run = None


def use(transport):
    """Makes transport the one sg calls go to."""
    global _transport
    _transport = transport


def current_transport():
    if _transport is None:
        raise Error("not_connected", "sg is not connected to a hub", "call sg.connect() first")
    return _transport


def begin_run(run):
    global _run
    _run = run


def end_run():
    global _run
    _run = None


def current_run():
    return _run


def calling_line():
    """The 1-based line of the run's code that is executing now, or None outside a run."""
    frame = sys._getframe(1)
    while frame is not None:
        if frame.f_code.co_filename == SCRIPT_FILENAME:
            return frame.f_lineno
        frame = frame.f_back
    return None


def in_loop(line):
    """True when line is inside a while body of the run's code."""
    return _run is not None and line is not None and line in _run.while_lines


def request(method, params, call=None):
    """Sends one hub call with params.line added and returns its result; a refusal raises Error, Stopped or
    _PassEnd. `call` is what Error.call reports (the verb call as sent)."""
    line = calling_line()
    params = dict(params)
    params["line"] = line
    reply = current_transport().call(method, params)
    if reply.get("ok"):
        return reply.get("result")
    exception = from_error(reply.get("error"), call=call if call is not None else {"method": method, **params},
                           line=line)
    if isinstance(exception, (Stopped, _PassEnd)) and _run is not None:
        _run.stopped = True
    raise exception
