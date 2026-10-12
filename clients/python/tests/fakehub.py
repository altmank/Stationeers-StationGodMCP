"""A small fake hub: it answers the hub protocol (docs/architecture/hub.md) over a pair of byte streams.

It answers `hello` with a welcome (carrying the next queued run, if any), passes each `call` to a handler on its own
thread (so replies can come back out of order), and records everything else it receives (run_analysis, run_end).
Tests talk to it in process over os.pipe, or run the real worker as a child process against it.
"""
import json
import os
import subprocess
import sys
import tempfile
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(HERE)
REPOSITORY = os.path.abspath(os.path.join(PACKAGE_ROOT, "..", ".."))
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)


def ok(result):
    return {"ok": True, "result": result}


def refused(code, message="refused", **extra):
    """An error reply; extra keys (fix, maybe_ran, stopped, pass_end, data) go into the error object."""
    return {"ok": False, "error": {"code": code, "message": message, **extra}}


def welcome(run=None):
    message = {"type": "welcome", "protocol": 2, "client_id": "h1", "client": "anonymous",
               "server": {"world": {"id": "w1"}, "game_state": "Running"}, "features": ["pipeline"]}
    if run is not None:
        message["run"] = run
    return message


class FakeHub:
    """handler(method, params) -> reply dict (ok(...) or refused(...)). runs: the run objects to hand out, one per
    hello; once they are used up the hub closes its side when the next hello comes (a warm worker then exits)."""

    def __init__(self, reader, writer, handler=None, runs=None):
        self.reader = reader
        self.writer = writer
        self.handler = handler or (lambda method, params: ok({}))
        self.runs = list(runs) if runs is not None else None
        self.calls = []          # (method, params) in arrival order
        self.messages = []       # every message that is not a call
        self.lock = threading.RLock()  # re-entrant: wait_for predicates call of_type
        self.write_lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    # ----- in process, over pipes -----

    @classmethod
    def over_pipes(cls, handler=None, runs=None):
        """(hub, client_reader, client_writer): the client's ends are binary file objects."""
        to_hub_read, to_hub_write = os.pipe()
        to_client_read, to_client_write = os.pipe()
        hub = cls(open(to_hub_read, "rb"), open(to_client_write, "wb"), handler, runs)
        return hub, open(to_client_read, "rb"), open(to_hub_write, "wb")

    # ----- the real worker, as a child process -----

    @classmethod
    def with_worker(cls, run, handler=None):
        """Starts the real worker exactly as the hub does (python -I -c BOOT <sg folder>, PYTHONHASHSEED=0), in a
        temporary working directory, against a fake hub that hands it run; returns (hub, process)."""
        from sg.worker import BOOT
        environment = dict(os.environ, PYTHONHASHSEED="0", PYTHONIOENCODING="utf-8")
        environment.pop("PYTHONPATH", None)
        process = subprocess.Popen([sys.executable, "-I", "-c", BOOT, PACKAGE_ROOT], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=tempfile.gettempdir(),
                                   env=environment)
        hub = cls(process.stdout, process.stdin, handler, [run])
        return hub, process

    # ----- what the tests read -----

    def wait_for(self, predicate, timeout=10.0):
        """Waits until predicate(self) is true (checked whenever a message arrives)."""
        with self.changed:
            if not self.changed.wait_for(lambda: predicate(self), timeout):
                raise AssertionError(f"fake hub: timed out; messages {self.messages}, calls {self.calls}")

    def of_type(self, kind):
        with self.lock:
            return [message for message in self.messages if message.get("type") == kind]

    def methods(self):
        with self.lock:
            return [method for method, _ in self.calls]

    def send(self, message):
        data = (json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8")
        with self.write_lock:
            try:
                self.writer.write(data)
                self.writer.flush()
            except (OSError, ValueError):
                pass

    def close(self):
        with self.write_lock:
            try:
                self.writer.close()
            except OSError:
                pass

    # ----- serving -----

    def _serve(self):
        try:
            for line in iter(self.reader.readline, b""):
                if line.strip():
                    self._receive(json.loads(line.decode("utf-8")))
        except (OSError, ValueError):
            pass
        finally:
            self.close()
            try:
                self.reader.close()
            except OSError:
                pass
            with self.changed:
                self.messages.append({"type": "_eof"})
                self.changed.notify_all()

    def _receive(self, message):
        if message.get("type") == "call":
            with self.changed:
                self.calls.append((message["method"], message.get("params") or {}))
                self.changed.notify_all()
            threading.Thread(target=self._answer, args=(message,), daemon=True).start()
            return
        with self.changed:
            self.messages.append(message)
            self.changed.notify_all()
        if message.get("type") == "hello":
            if self.runs is None:
                self.send(welcome())
            elif self.runs:
                self.send(welcome(self.runs.pop(0)))
            else:
                self.close()

    def _answer(self, message):
        try:
            reply = self.handler(message["method"], message.get("params") or {})
        except Exception as error:  # noqa: BLE001 - a broken handler answers an error, not a hang
            reply = refused("fake_hub_handler", repr(error))
        self.send({"type": "reply", "id": message["id"], **reply})
