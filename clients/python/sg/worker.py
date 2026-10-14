"""The script worker: runs one run_script run with sg connected to the hub over this process's stdin and stdout.

The hub starts it (ahead of time, so one is ready when a run comes) as

    python -s -P -c "<BOOT>" <the folder that holds sg>

with every PYTHON* variable removed from its environment and PYTHONHASHSEED=0 and PYTHONIOENCODING=utf-8 set. -s
leaves the user site out and -P keeps the working directory off sys.path (Python 3.11+); BOOT puts sg's folder on
sys.path itself. -I is not used: it implies -E, which would ignore PYTHONHASHSEED. With hash randomisation on, set and
str-keyed iteration would differ between a report run and its confirmed run, so the worker refuses to run then.

A worker serves exactly one run, so nothing a run does (patched modules, threads left running, changed builtins) can
reach the next one. It:
1. moves the protocol off fds 0 and 1 to private, non-inheritable descriptors numbered 100 and up, and points fd 0 at
   the null device and fd 1 at stderr. That keeps accidental writes to fd 1, child processes and input() away from
   the protocol. It does not stop code that sets out to find the descriptors; the hub keeps the run's end state
   itself and kills the worker after run_end (hub.md, *Runs*);
2. says hello and reads the hub's welcome, whose `run` key holds the code (hub.md, *Runs*; a run of a saved script
   carries `call` and `args` too, and the run is that function's call); end of stdin instead means the hub no longer
   needs this worker;
3. sends run_analysis before any user code runs;
4. runs the code as a function body with `sg`, `print` (tables for row lists) and `time.sleep` (sg.wait) in place,
   capturing what it prints;
5. sends run_end {state, returned, printed, error} whatever happened: done, stopped, pass_end or failed;
6. exits at once (os._exit, bound at import so the code cannot replace it), so threads the code left behind cannot
   hold the process or make more calls.

If the hub goes away mid-run, the worker exits 2 s after its stdin ends.
"""
import builtins
import io
import json
import os
import sys
import threading
import time

from . import _session
from ._analysis import RUN_FUNCTION, analyse
from ._errors import Stopped, _PassEnd
from ._failure import exception_name, failed_text, safe_repr, stopped_text
from ._replies import plain
from ._table import run_print
from ._transport import Transport

BOOT = "import sys; sys.path.insert(0, sys.argv[1]); import sg.worker; sg.worker.main()"
CLIENT_NAME = "sg-worker"
HUB_GONE_GRACE_S = 2.0
PROTOCOL_FD_FLOOR = 100

# Bound now, before any user code runs: a run that replaces os._exit or threading.Timer cannot keep the worker alive.
_hard_exit = os._exit
_Timer = threading.Timer


def main():
    """The worker process: one run, then exit."""
    reader, writer = take_protocol_streams()
    code = 1
    try:
        code = serve(reader, writer)
    finally:
        for stream in (sys.stdout, sys.stderr, sys.__stderr__):
            try:
                stream.flush()
            except Exception:  # noqa: BLE001 - exiting anyway
                pass
        _hard_exit(code)


def take_protocol_streams():
    """Moves the protocol to private, non-inheritable descriptors numbered PROTOCOL_FD_FLOOR and up (out of reach of
    a guessed small fd or os.closerange(3, 64)), then points fd 0 at the null device and fd 1 at stderr. Returns the
    protocol's (reader, writer)."""
    protocol_in = _move_high(0)
    protocol_out = _move_high(1)
    null = os.open(os.devnull, os.O_RDWR)
    os.dup2(null, 0)
    os.dup2(2, 1)
    os.close(null)
    sys.stdin = open(os.devnull, "r", encoding="utf-8")
    sys.stdout = sys.__stdout__ = sys.stderr  # outside a run, a stray print goes to stderr
    return open(protocol_in, "rb"), open(protocol_out, "wb")


def _move_high(fd):
    """A non-inheritable copy of fd at the first free descriptor from PROTOCOL_FD_FLOOR."""
    for target in range(PROTOCOL_FD_FLOOR, PROTOCOL_FD_FLOOR + 1000):
        try:
            os.fstat(target)
        except OSError:
            os.dup2(fd, target, inheritable=False)
            return target
    raise OSError("no free descriptor for the protocol")


def serve(reader, writer):
    """Serves one run; returns the exit code (0 once run_end is sent or when there was no run to serve)."""
    transport = Transport(reader, writer, client=CLIENT_NAME, on_end_of_stream=_exit_soon, closable=False)
    _session.use(transport)
    try:
        welcome = transport.hello()
    except Exception:  # noqa: BLE001 - the hub refused or broke the session: nothing to run
        return 1
    if welcome is None:
        return 0  # the hub closed before giving a run: it no longer needs this worker
    run = welcome.get("run")
    try:
        if sys.flags.hash_randomization:
            end = _run_end("failed", None, "", "the worker has hash randomisation on (PYTHONHASHSEED is not 0, or "
                           "-I/-E made Python ignore it), so set and dict order could differ between a report run "
                           "and its confirmed run; start workers as hub.md says")
        elif isinstance(run, dict):
            end = guarded_run(transport, run)
        else:
            end = _run_end("failed", None, "", "the welcome carried no run")
        send_run_end(transport, end)
    except Exception:  # noqa: BLE001 - the hub is gone: nobody is left to tell
        return 1
    return 0


def send_run_end(transport, end):
    """Sends run_end; when JSON cannot carry its returned value (it is checked, but a value can still surprise), sends
    it again with the returned value as text."""
    try:
        transport.send(end)
    except (TypeError, ValueError, RecursionError):
        cleaned = dict(end, returned=safe_repr(end.get("returned")))
        try:
            transport.send(cleaned)
        except (TypeError, ValueError, RecursionError):
            transport.send(_run_end(end.get("state", "failed"), None, "", "the run's end could not be sent as JSON"))


def _exit_soon():
    """The hub's stream ended: the run cannot make calls any more, so the worker ends shortly whatever it is doing."""
    timer = _Timer(HUB_GONE_GRACE_S, _hard_exit, (1,))
    timer.daemon = True
    timer.start()


def guarded_run(transport, run):
    """run_one, with a fallback run_end for anything that escapes it (a broken exception, a broken repr)."""
    try:
        return run_one(transport, run)
    except BaseException as error:  # noqa: BLE001 - every run must end with a run_end
        return _run_end("failed", None, "", f"the worker failed: {_type_name(error)}")


def run_one(transport, run):
    """Analyses and runs one run's code; returns its run_end message (run_analysis is sent from here). A run the hub
    started from a saved script carries `call` (the function to call, "main") and `args`: the code's body runs as
    usual, defining that function, and the run is its call."""
    source = run.get("code") or ""
    call = run.get("call")
    args = run.get("args") or {}
    analysis = analyse(source)
    transport.send(analysis.message())
    if analysis.code is None:
        return _run_end("failed", None, "", analysis.syntax_error)

    import sg  # the package itself, as the code sees it
    namespace = {"__name__": "__script__", "__builtins__": builtins, "sg": sg, "print": run_print}
    printed = io.StringIO()
    run_state = _session.Run(run.get("id"), analysis.while_lines)
    _session.begin_run(run_state)
    sys.stdout = sys.__stdout__ = printed
    time.sleep = sg.wait
    returned, exception = None, None
    try:
        exec(analysis.code, namespace)  # defines the run function
        if call:
            namespace[RUN_FUNCTION]()  # the script's own statements: defines main
            entry = namespace.get(call)
            if not callable(entry):
                raise LookupError(f"the script defines no {call}() to run")
            returned = entry(**args)
        else:
            returned = namespace[RUN_FUNCTION]()
    except BaseException as error:  # noqa: BLE001 - anything the code raised ends the run
        exception = error
    returned = returnable(returned)
    sys.stdout = sys.__stdout__ = sys.stderr
    state, error = end_state(run_state.stop, exception, source)
    return _run_end(state, returned, printed.getvalue(), error)


def end_state(stop, exception, source):
    """(state, error). Only a stop the hub delivered (stop, recorded by the session) makes the run stopped or
    pass_end, and it does so even when the code swallowed it or failed after it; an sg.Stopped the code raised itself
    is a failure like any other. A failure after a stop is kept after the stop's line."""
    failure = None if exception is None or exception is stop else _failure(exception, source)
    if isinstance(stop, Stopped):
        stopped = _text(stopped_text, stop)
        return "stopped", stopped if failure is None else f"{stopped}\n{failure}"
    if isinstance(stop, _PassEnd):
        return "pass_end", failure
    if exception is None:
        return "done", None
    return "failed", failure


def returnable(value):
    """The returned value as JSON can carry it: main lists paged in, else its repr when JSON cannot hold it (NaN,
    a set, an int too long to print). Paging may meet a stop; the run's state then says stopped."""
    try:
        value = plain(value)
    except BaseException:  # noqa: BLE001 - paging after a stop may be refused; keep what the code had
        return safe_repr(value)
    try:
        json.dumps(value, allow_nan=False)
        return value
    except (TypeError, ValueError, RecursionError):
        return safe_repr(value)


def _failure(exception, source):
    try:
        return failed_text(exception, source)
    except BaseException:  # noqa: BLE001 - a broken __str__ or similar: say at least what was raised
        return f"{_type_name(exception)} (its message could not be shown)"


def _text(build, value):
    try:
        return build(value)
    except BaseException:  # noqa: BLE001
        return _type_name(value)


def _type_name(exception):
    try:
        return exception_name(exception)
    except BaseException:  # noqa: BLE001
        return "an exception"


def _run_end(state, returned, printed, error):
    return {"type": "run_end", "state": state, "returned": returned, "printed": printed, "error": error}


if __name__ == "__main__":
    main()
