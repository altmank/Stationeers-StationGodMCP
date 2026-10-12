"""The script worker: runs one run_script run with sg connected to the hub over this process's stdin and stdout.

The hub starts it (ahead of time, so one is ready when a run comes) as

    python -I -c "<BOOT>" <the folder that holds sg>          with PYTHONHASHSEED=0

-I keeps the user's environment, user site and working directory out; BOOT puts sg's folder on sys.path itself.

A worker serves exactly one run, so nothing a run does (patched modules, threads left running, changed builtins) can
reach the next one. It:
1. moves the protocol off fds 0 and 1 to private descriptors, so the code (os.write(1, ...), a subprocess, input())
   cannot read or forge protocol lines;
2. says hello and reads the hub's welcome, whose `run` key holds the code (hub.md, *Runs*); end of stdin instead
   means the hub no longer needs this worker;
3. sends run_analysis before any user code runs;
4. runs the code as a function body with `sg`, `print` (tables for row lists) and `time.sleep` (sg.wait) in place,
   capturing what it prints;
5. sends run_end {state, returned, printed, error} whatever happened: done, stopped, pass_end or failed;
6. exits at once (os._exit), so threads the code left behind cannot hold the process or make more calls.

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
        os._exit(code)


def take_protocol_streams():
    """Moves the protocol to private, non-inheritable copies of fds 0 and 1, then points fd 0 at the null device and
    fd 1 at stderr. Code that writes to fd 1, starts a subprocess or calls input() then reaches stderr or nothing,
    never the hub. Returns the protocol's (reader, writer)."""
    protocol_in = os.dup(0)
    protocol_out = os.dup(1)
    os.set_inheritable(protocol_in, False)
    os.set_inheritable(protocol_out, False)
    null = os.open(os.devnull, os.O_RDWR)
    os.dup2(null, 0)
    os.dup2(2, 1)
    os.close(null)
    sys.stdin = open(os.devnull, "r", encoding="utf-8")
    sys.stdout = sys.__stdout__ = sys.stderr  # outside a run, a stray print goes to stderr
    return open(protocol_in, "rb"), open(protocol_out, "wb")


def serve(reader, writer):
    """Serves one run; returns the exit code (0 once run_end is sent or when there was no run to serve)."""
    transport = Transport(reader, writer, client=CLIENT_NAME, on_end_of_stream=_exit_soon)
    _session.use(transport)
    try:
        welcome = transport.hello()
    except Exception:  # noqa: BLE001 - the hub refused or broke the session: nothing to run
        return 1
    if welcome is None:
        return 0  # the hub closed before giving a run: it no longer needs this worker
    run = welcome.get("run")
    try:
        if isinstance(run, dict):
            end = guarded_run(transport, run)
        else:
            end = _run_end("failed", None, "", "the welcome carried no run")
        transport.send(end)
    except Exception:  # noqa: BLE001 - the hub is gone: nobody is left to tell
        return 1
    return 0


def _exit_soon():
    """The hub's stream ended: the run cannot make calls any more, so the worker ends shortly whatever it is doing."""
    timer = threading.Timer(HUB_GONE_GRACE_S, os._exit, (1,))
    timer.daemon = True
    timer.start()


def guarded_run(transport, run):
    """run_one, with a fallback run_end for anything that escapes it (a broken exception, a broken repr)."""
    try:
        return run_one(transport, run)
    except BaseException as error:  # noqa: BLE001 - every run must end with a run_end
        return _run_end("failed", None, "", f"the worker failed: {_type_name(error)}")


def run_one(transport, run):
    """Analyses and runs one run's code; returns its run_end message (run_analysis is sent from here)."""
    source = run.get("code") or ""
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
    state, returned, error = "done", None, None
    try:
        exec(analysis.code, namespace)  # defines the run function (and runs a hoisted `from x import *`)
        returned = namespace[RUN_FUNCTION]()
    except Stopped as stop:
        state, error = "stopped", _text(stopped_text, stop)
    except _PassEnd:
        state = "pass_end"
    except BaseException as exception:  # noqa: BLE001 - anything the code raised ends the run as failed
        state, error = "failed", _failure(exception, source)
    returned = returnable(returned)
    sys.stdout = sys.__stdout__ = sys.stderr
    state, error = end_state(run_state, state, error)
    return _run_end(state, returned, printed.getvalue(), error)


def end_state(run_state, state, error):
    """(state, error): a stop the hub delivered decides the state, even when the code swallowed it or failed after
    it; the failed text, if any, is kept after the stop's line."""
    stop = run_state.stop
    if stop is None or state in ("stopped", "pass_end"):
        return state, error
    if isinstance(stop, _PassEnd):
        return "pass_end", error
    stopped = _text(stopped_text, stop)
    return "stopped", stopped if error is None else f"{stopped}\n{error}"


def returnable(value):
    """The returned value as JSON can carry it: main lists paged in, else its repr when JSON cannot hold it. Paging
    may meet a stop; the run's state then says stopped (the session recorded it)."""
    try:
        value = plain(value)
    except BaseException:  # noqa: BLE001 - paging after a stop may be refused; keep what the code had
        return safe_repr(value)
    try:
        json.dumps(value)
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
