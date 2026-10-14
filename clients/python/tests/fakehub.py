"""A small fake hub: it answers the hub protocol (docs/architecture/hub.md) over a pair of byte streams.

It answers `hello` with a welcome (carrying the next queued run, if any), passes each `call` to a handler on its own
thread (so replies can come back out of order), and records everything else it receives (run_analysis, run_end).
Tests talk to it in process over os.pipe, or run the real worker as a child process against it.

It is the hub as a run's worker sees it (the `run` face, the default) unless `face="program"` says an agent's own
connection. It refuses what the session refuses before the run or the game sees the call: a method outside the run's
seven, and stream_end, release, cursor_close and session_end; a second hello and an unknown message type end the
session; a `more` whose cursor none of this hub's replies carried is cursor_expired, on either face, because every
session backs a walk with the hub's own cursors.
"""
import json
import os
import subprocess
import sys
import tempfile
import threading

# The methods a run's session answers (hub.md, The hub protocol): every other method goes through verb.
RUN_METHODS = ("verb", "ref", "more", "wait", "hold", "subscribe", "unsubscribe")

# What a run's session refuses: a stream and the job pins belong to the hub and to the run, not to a worker.
RUN_REFUSED_MESSAGES = ("stream_end", "release", "cursor_close", "session_end")

RUN_CALLS_ONLY = ("a run's session calls verb, ref, more, wait, hold, subscribe and unsubscribe; other mod methods "
                  "go through verb")

# Every message a session takes besides hello (protocol.md, Framing); run_analysis and run_end belong to a run.
KNOWN_MESSAGES = ("call", "cancel", "stream_end", "release", "cursor_close", "session_end", "bye",
                  "run_analysis", "run_end")

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(HERE)
REPOSITORY = os.path.abspath(os.path.join(PACKAGE_ROOT, "..", ".."))
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)


def worker_environment(**changes):
    """The environment the hub gives a worker: this one without any PYTHON* variable, plus the seed and encoding."""
    environment = {name: value for name, value in os.environ.items() if not name.upper().startswith("PYTHON")}
    environment.update(PYTHONHASHSEED="0", PYTHONIOENCODING="utf-8")
    environment.update(changes)
    return environment


def ok(result):
    return {"ok": True, "result": result}


def refused(code, message="refused", **extra):
    """An error reply; extra keys (fix, maybe_ran, stopped, pass_end, data) go into the error object."""
    return {"ok": False, "error": {"code": code, "message": message, **extra}}


def welcome(run=None, features=None):
    message = {"type": "welcome", "protocol": 2, "client_id": "h1", "client": "anonymous",
               "server": {"world": {"id": "w1"}, "game_state": "Running"},
               "features": ["pipeline"] if features is None else list(features)}
    if run is not None:
        message["run"] = run
    return message


class FakeHub:
    """handler(method, params) -> reply dict (ok(...) or refused(...)). runs: the run objects to hand out, one per
    hello; once they are used up the hub closes its side when the next hello comes (a warm worker then exits).
    features: what the welcome lists (["pipeline"] by default; tests add `subscriptions` for the devices topic).
    face: "run" (a worker's session, the default) or "program" (an agent's own connection)."""

    def __init__(self, reader, writer, handler=None, runs=None, features=None, face="run"):
        self.reader = reader
        self.writer = writer
        self.handler = handler or (lambda method, params: ok({}))
        self.runs = list(runs) if runs is not None else None
        self.features = features
        self.face = face
        self.calls = []          # (method, params) in arrival order
        self.messages = []       # every message that is not a call
        self.cursors = set()     # the hub cursors this hub's replies carried
        self.cancelled = set()   # the call ids a cancel came for
        self.helloed = False
        self.stopped = False     # a bye or a protocol error ended the session: nothing more is answered
        self.lock = threading.RLock()  # re-entrant: wait_for predicates call of_type
        self.write_lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    # ----- in process, over pipes -----

    @classmethod
    def over_pipes(cls, handler=None, runs=None, features=None, face="run"):
        """(hub, client_reader, client_writer): the client's ends are binary file objects."""
        to_hub_read, to_hub_write = os.pipe()
        to_client_read, to_client_write = os.pipe()
        hub = cls(open(to_hub_read, "rb"), open(to_client_write, "wb"), handler, runs, features, face)
        return hub, open(to_client_read, "rb"), open(to_hub_write, "wb")

    # ----- the real worker, as a child process -----

    @classmethod
    def with_worker(cls, run, handler=None, environment=None):
        """Starts the real worker exactly as the hub does (python -s -P -c BOOT <sg folder>, every PYTHON* variable
        removed, PYTHONHASHSEED=0 and PYTHONIOENCODING=utf-8 set), in a temporary working directory, against a fake
        hub that hands it run; returns (hub, process)."""
        from sg.worker import BOOT
        process = subprocess.Popen([sys.executable, "-s", "-P", "-c", BOOT, PACKAGE_ROOT], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=tempfile.gettempdir(),
                                   env=worker_environment() if environment is None else environment)
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

    def write(self, message):
        """Pushes one message to the client right now: the way a test sends what a live hub would (a subscription's
        update, its end, a world change). The hub's own answers go through send."""
        self.send(message)

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
        if self.stopped:
            return
        kind = message.get("type")
        if kind == "call":
            if not self.helloed:
                self._stop("protocol_error", "The first message must be hello.")
                return
            with self.changed:
                self.calls.append((message["method"], message.get("params") or {}))
                self.changed.notify_all()
            threading.Thread(target=self._answer, args=(message,), daemon=True).start()
            return
        with self.changed:
            self.messages.append(message)
            self.changed.notify_all()
        if kind == "hello":
            self._hello()
        elif kind == "cancel":
            with self.lock:
                self.cancelled.add(message.get("id"))
        elif kind == "bye":
            self._stop()
        elif kind in RUN_REFUSED_MESSAGES and self.face == "run":
            self.send({"type": "reply", "id": None, **refused("not_allowed", RUN_CALLS_ONLY)})
        elif kind not in KNOWN_MESSAGES:
            self._stop("protocol_error", f"Unknown message type '{kind}'.")

    def _hello(self):
        """The first hello gets a welcome (or a close when no run is left); a second hello is a protocol error and
        the session ends, as a session that answered hello once does."""
        if self.helloed:
            self._stop("protocol_error", "hello was answered already; a session says hello once.")
            return
        self.helloed = True
        if self.runs is None:
            self.send(welcome(features=self.features))
        elif self.runs:
            self.send(welcome(self.runs.pop(0), self.features))
        else:
            self.close()

    def _stop(self, code=None, message=None):
        """The session ends: the hub's error line goes out (a null id, as a hello's or a broken line's refusal has),
        nothing more is answered, and the client sees its stream end."""
        self.stopped = True
        if code is not None:
            self.send({"type": "reply", "id": None, **refused(code, message)})
        self.close()

    def _answer(self, message):
        method = message["method"]
        params = message.get("params") or {}
        try:
            reply = self._refuse(method, params) or self.handler(method, params)
        except Exception as error:  # noqa: BLE001 - a broken handler answers an error, not a hang
            reply = refused("fake_hub_handler", repr(error))
        with self.lock:
            if message.get("id") in self.cancelled:
                reply = refused("cancelled", f"{method} was cancelled; if it had reached the game it may have run: "
                                "read the state back before repeating a change.", maybe_ran=True)
        self._record(message, reply)
        self.send({"type": "reply", "id": message["id"], **reply})

    def _refuse(self, method, params):
        """What the session refuses before the run (or the handler standing in for the game) sees the call: None when
        the handler answers it."""
        if self.face == "run" and method not in RUN_METHODS:
            return refused("not_allowed", RUN_CALLS_ONLY)
        if method != "more":
            return None
        cursor = params.get("cursor")
        if not isinstance(cursor, str):
            return refused("invalid_argument", "more needs cursor: the hub cursor a reply carried.")
        if cursor not in self.cursors:
            return refused("cursor_expired", f"No cursor '{cursor}' on this stream: it ended, the world changed, or "
                           "the list was done. Start the walk again.", data={"cursor": cursor})
        return None

    def _record(self, message, reply):
        """The cursors this hub carried: a reply's own cursor is one a walk may go on with, and the page that ends
        the walk (no cursor) frees the one it was walked with, as the hub frees its cursor then."""
        result = reply.get("result")
        carried = result.get("cursor") if isinstance(result, dict) else None
        if isinstance(carried, str):
            self.cursors.add(carried)
        asked = (message.get("params") or {}).get("cursor")
        if message["method"] == "more" and asked and not carried:
            self.cursors.discard(asked)
