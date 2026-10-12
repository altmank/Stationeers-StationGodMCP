"""The script worker: runs run_script code with sg connected to the hub over this process's stdin and stdout.

    python -I -m sg.worker          (started by the hub with PYTHONHASHSEED=0)

One worker serves runs one after another (the hub keeps a warm pool). For each run it:
1. says hello and reads the hub's welcome, whose `run` key holds the code (hub.md, *Runs*); end of stdin instead
   means the hub no longer needs this worker, and it exits;
2. sends run_analysis before any user code runs (literals, while_lines, writes, holds, syntax_error);
3. runs the code as a function body with `sg`, `print` (tables for row lists) and `time.sleep` (sg.wait) in place,
   capturing what it prints;
4. sends run_end {state, returned, printed, error}: done, stopped, pass_end or failed (note-22 text).

stdout carries the protocol, so nothing else may write to it: outside a run sys.stdout points at stderr, and inside
one at the run's capture buffer.
"""
import builtins
import io
import json
import sys
import time

from . import _session
from ._analysis import RUN_FUNCTION, analyse
from ._errors import Stopped, _PassEnd
from ._failure import safe_repr, failed_text, stopped_text
from ._replies import plain
from ._table import run_print
from ._transport import Transport

CLIENT_NAME = "sg-worker"


def main():
    protocol_in = sys.stdin.buffer
    protocol_out = sys.stdout.buffer
    sys.stdout = sys.stderr  # a stray print outside a run must not corrupt the protocol
    serve(protocol_in, protocol_out)


def serve(reader, writer):
    """Serves runs until the hub closes the stream."""
    transport = Transport(reader, writer, client=CLIENT_NAME)
    _session.use(transport)
    try:
        while True:
            welcome = transport.hello()
            if welcome is None:
                return
            run = welcome.get("run")
            if not isinstance(run, dict):
                transport.send(_run_end("failed", None, "", "the welcome carried no run"))
                continue
            transport.send(run_one(transport, run))
    except OSError:
        return  # the hub closed our stdout: nobody is left to tell


def run_one(transport, run):
    """Analyses and runs one run's code; returns its run_end message (run_analysis is sent from here)."""
    source = run.get("code") or ""
    analysis = analyse(source)
    transport.send(analysis.message())
    if analysis.code is None:
        return _run_end("failed", None, "", analysis.syntax_error)

    import sg  # the package itself, as the code sees it
    namespace = {"__name__": "__script__", "__builtins__": builtins, "sg": sg, "print": run_print}
    exec(analysis.code, namespace)
    function = namespace[RUN_FUNCTION]

    printed = io.StringIO()
    real_stdout, real_sleep = sys.stdout, time.sleep
    _session.begin_run(_session.Run(run.get("id"), analysis.while_lines))
    sys.stdout, time.sleep = printed, sg.wait
    state, returned, error = "done", None, None
    try:
        returned = function()
    except Stopped as stop:
        state, error = "stopped", stopped_text(stop)
    except _PassEnd:
        state = "pass_end"
    except BaseException as exception:  # noqa: BLE001 - anything the code raised ends the run as failed
        state, error = "failed", failed_text(exception, source)
    finally:
        sys.stdout, time.sleep = real_stdout, real_sleep
    try:
        returned = returnable(returned)
    finally:
        _session.end_run()
    return _run_end(state, returned, printed.getvalue(), error)


def returnable(value):
    """The returned value as JSON can carry it: main lists paged in, else its repr when JSON cannot hold it."""
    try:
        value = plain(value)
    except BaseException:  # noqa: BLE001 - paging after a stop may be refused; keep what the code had
        return safe_repr(value)
    try:
        json.dumps(value)
        return value
    except (TypeError, ValueError):
        return repr(value)


def _run_end(state, returned, printed, error):
    return {"type": "run_end", "state": state, "returned": returned, "printed": printed, "error": error}


if __name__ == "__main__":
    main()
