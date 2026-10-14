"""Where sg calls go: the current transport, the current run (if any), and the one function that sends a call.

Every sg call carries `params.line`, the line of the run's code that made it (hub.md, *Runs*) - except subscribe and
unsubscribe, which the mod answers as its own protocol methods and which take their own arguments alone. The worker
compiles the code under the file name "<script>", so the line is found by walking up the stack to the innermost frame
from that file. Outside a run (a program using sg.connect()) there is no such frame and the line is None.
"""
import sys

from ._errors import Error, Stopped, _PassEnd, from_error

SCRIPT_FILENAME = "<script>"


class Run:
    """What sg needs to know about the run in progress: its id, the lines inside a while body, per file - the run's
    own file and every saved script it loads, each under the name its code was compiled with (a wait inside any of them
    counts as a loop wait in a report run) - the script store it may load saved scripts from (scripts_dir and world,
    from the welcome's run key; None in a session whose hub gave it no store), and the stop (Stopped or _PassEnd) the
    hub delivered, if any. A delivered stop decides the run's end state even when the code swallowed it; an sg.Stopped
    the code raises itself is not one."""

    def __init__(self, run_id=None, while_lines=(), scripts_dir=None, world=None):
        self.id = run_id
        self.while_lines = {SCRIPT_FILENAME: frozenset(while_lines)}
        self.scripts_dir = scripts_dir
        self.world = world
        self.stop = None

    def add_while_lines(self, filename, lines):
        """Registers another file's while-body lines under the name its code is compiled with (a saved script the run
        loaded; its frames name that name alone). A name registered twice (a script saved again and loaded again)
        keeps both sets: a frame cannot say which of the two files it is."""
        self.while_lines[filename] = self.while_lines.get(filename, frozenset()) | frozenset(lines)

    @property
    def stopped(self):
        return self.stop is not None


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


def _script_frames():
    """The frames of the run's code on the stack, innermost first."""
    frame = sys._getframe(1)
    while frame is not None:
        if frame.f_code.co_filename == SCRIPT_FILENAME:
            yield frame
        frame = frame.f_back


def calling_line():
    """The 1-based line of the run's code that is executing now (the innermost frame), or None outside a run."""
    return next((frame.f_lineno for frame in _script_frames()), None)


def in_loop():
    """True when a frame of the run's code, or of a saved script it loaded, stands on a line inside a while body of
    its own file: a wait in a function that a while loop calls counts too. Loops made by recursion or by `for` over an
    endless iterator (itertools.count) are not seen."""
    if _run is None:
        return False
    frame = sys._getframe(1)
    while frame is not None:
        lines = _run.while_lines.get(frame.f_code.co_filename)
        if lines is not None and frame.f_lineno in lines:
            return True
        frame = frame.f_back
    return False


def _replaces(recorded, delivered):
    """Whether a stop the hub just delivered becomes the run's recorded stop: the first one, or a Stopped after a
    pass end (a stop outranks the end of a report pass)."""
    if not isinstance(delivered, (Stopped, _PassEnd)):
        return False
    return recorded is None or (isinstance(recorded, _PassEnd) and isinstance(delivered, Stopped))


def request(method, params, call=None, line=True):
    """Sends one hub call with params.line added and returns its result; a refusal raises Error, Stopped or
    _PassEnd. `call` is what Error.call reports (the verb call as sent). `line=False` sends it without a line, for the
    hub calls the mod answers as its own protocol methods (subscribe, unsubscribe): those take their own arguments
    alone and refuse anything else, while verb, ref, more, wait and hold read the line."""
    at = calling_line() if line else None
    params = dict(params)
    if line:
        params["line"] = at
    reply = current_transport().call(method, params)
    if reply.get("ok"):
        return reply.get("result")
    exception = from_error(reply.get("error"), call=call if call is not None else {"method": method, **params},
                           line=at)
    if _run is not None and _replaces(_run.stop, exception):
        _run.stop = exception
    raise exception
