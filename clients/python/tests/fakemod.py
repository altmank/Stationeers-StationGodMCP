"""An in-process stand-in for the mod, speaking the wire protocol as protocol.md defines it, over loopback TCP or a
real overlapped named pipe: hello and welcome, calls in flight, events, subscriptions; a first message that is not
hello is a protocol_error that closes the connection.

Tests steer it: handlers answer methods, hold() keeps a method from answering until released, break_on() drops the
connection when a method's call arrives (after it was read, so the call counts as written), drop_all() breaks every
connection, set_world() changes the world id a new welcome reports, push() sends an event.
"""
import itertools
import json
import os
import socket
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from stationgod import _methods, pipe  # noqa: E402

DEFAULT_FEATURES = ["shape", "shape.paths", "subscriptions", "cancel"]


class FakeError(Exception):
    def __init__(self, code, message="refused", data=None):
        super().__init__(code)
        self.error = {"code": code, "message": message}
        if data is not None:
            self.error["data"] = data


class _Conn:
    def __init__(self, mod, stream_read, stream_write, stream_close, transport):
        self.mod = mod
        self.read = stream_read
        self._write = stream_write
        self._close = stream_close
        self.transport = transport
        self.lock = threading.Lock()
        self.welcomed = False
        self.signed_in = False
        self.client = None
        self.in_flight = {}
        self.subscriptions = {}
        self.closed = False
        self.hello = None

    def send(self, message):
        line = (json.dumps(message, separators=(",", ":")) + "\n").encode()
        with self.lock:
            if self.closed:
                return
            try:
                self._write(line)
            except OSError:
                self.close()

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self._close()
            except OSError:
                pass


class FakeMod:
    def __init__(self, transport="tcp", features=None, world_id="w1",
                 catalogue_hash=None, max_in_flight=16, legacy_secret="s3cret", catalogue=None,
                 subscription_limit=None):
        self.transport = transport
        self.features = list(DEFAULT_FEATURES if features is None else features)
        self.world_id = world_id
        self.catalogue_hash = catalogue_hash or _methods.CATALOGUE_HASH
        self.catalogue = catalogue
        self.max_in_flight = max_in_flight
        self.legacy_secret = legacy_secret
        self.subscription_limit = subscription_limit
        self.handlers = {"game_clock": lambda params: {"game_time_s": 84211.5, "paused": False}}
        self.received = []                   # every message read, in order
        self.connections = []
        self.held = {}
        self.breaks = set()
        self.instance = 0
        self._sub_ids = itertools.count(1)
        self._client_ids = itertools.count(1)
        self._stop = False
        self._listener = None
        self.pipe_name = None
        self.port = None

    # ---- steering ----

    def calls(self, method=None):
        return [m for m in self.received if m.get("method") is not None and (method is None or m["method"] == method)
                and m.get("type", "call") == "call"]

    def hold(self, method):
        event = threading.Event()
        self.held[method] = event
        return event

    def break_on(self, method):
        self.breaks.add(method)

    def drop_all(self):
        for conn in list(self.connections):
            conn.close()

    def set_world(self, world_id):
        self.world_id = world_id

    def push(self, event):
        for conn in list(self.connections):
            if conn.welcomed:
                conn.send(dict({"type": "event"}, **event))

    def push_update(self, subscription, result, seq=1, frame=100):
        self.push({"event": "update", "subscription": subscription, "seq": seq, "frame": frame,
                   "game_time_s": 1.0, "late_ms": 0, "result": result})

    def subscriptions(self):
        found = {}
        for conn in self.connections:
            found.update(conn.subscriptions)
        return found

    # ---- running ----

    def start(self):
        if self.transport == "tcp":
            server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            server.bind(("127.0.0.1", 0))
            server.listen(16)
            self.port = server.getsockname()[1]
            self._listener = server
        else:
            self.pipe_name = f"StationGodFake-{os.getpid()}-{time.time_ns()}"
            self._listener = pipe.PipeListener(self.pipe_name)
        threading.Thread(target=self._accept_loop, daemon=True).start()
        if self.transport == "pipe":
            time.sleep(0.02)
        return self

    def stop(self):
        self._stop = True
        try:
            self._listener.close()
        except OSError:
            pass
        self.drop_all()
        for event in self.held.values():
            event.set()

    def client_options(self):
        return {"host": "127.0.0.1", "port": self.port} if self.transport == "tcp" else {"pipe": self.pipe_name}

    def _accept_loop(self):
        while not self._stop:
            try:
                if self.transport == "tcp":
                    sock, _ = self._listener.accept()
                    conn = _Conn(self, lambda s=sock: _recv(s), sock.sendall, lambda s=sock: _shut(s), "tcp")
                else:
                    handle = self._listener.accept()
                    conn = _Conn(self, lambda h=handle: h.read(), handle.write, handle.close, "pipe")
            except OSError:
                if self._stop:
                    return
                continue
            self.connections.append(conn)
            threading.Thread(target=self._serve, args=(conn,), daemon=True).start()

    def _serve(self, conn):
        buffer = b""
        try:
            while not conn.closed:
                chunk = conn.read()
                if not chunk:
                    break
                buffer += chunk
                while b"\n" in buffer:
                    line, buffer = buffer.split(b"\n", 1)
                    if line.strip():
                        self._line(conn, json.loads(line))
        except (OSError, ValueError):
            pass
        finally:
            conn.close()
            if conn in self.connections:
                self.connections.remove(conn)

    def _line(self, conn, message):
        self.received.append(message)
        if conn.transport == "tcp" and not conn.signed_in:
            # TCP: the first line must be the shared secret; after it the connection talks as the pipe does.
            if not (message.get("type") == "auth" and self.legacy_secret and
                    message.get("secret") == self.legacy_secret):
                conn.send({"ok": False, "error": {"code": "unauthorized", "message": "Authentication failed."}})
                conn.close()
                return
            conn.signed_in = True
            conn.send({"ok": True})
            return
        if not conn.welcomed:
            if message.get("type") == "hello":
                return self._hello(conn, message)
            conn.send({"type": "reply", "id": None, "ok": False,
                       "error": {"code": "protocol_error", "message": "The first message must be hello."}})
            conn.send({"type": "goodbye", "reason": "protocol_error"})
            conn.close()
            return
        return self._v2(conn, message)

    def _hello(self, conn, message):
        conn.hello = message
        conn.welcomed = True
        conn.client = (message.get("client") or {}).get("name")
        self.instance += 1
        conn.send({"type": "welcome", "protocol": 2, "client_id": f"c{next(self._client_ids)}", "client": "anonymous",
                   "server": {"mod_version": "9.9.9", "instance_id": "fake", "pipe_name": self.pipe_name,
                              "transport": conn.transport, "role": "host", "dedicated": False,
                              "world": {"id": self.world_id, "save": "fixround", "epoch": 1},
                              "game_state": "Running"},
                   "catalogue": {"hash": self.catalogue_hash, "methods": 91, "protocol_methods": 3},
                   "limits": {"max_in_flight": self.max_in_flight, "max_request_bytes": 4194304,
                              "max_reply_bytes": 16777216, "max_subscriptions": 64,
                              "max_subscription_values": 8192, "max_values_per_subscription": 1024,
                              "min_subscription_interval_s": 0.5},
                   "features": self.features})

    def _v2(self, conn, message):
        kind = message.get("type")
        if kind == "cancel":
            return
        if kind == "bye":
            return
        if kind != "call":
            conn.send({"type": "reply", "id": None, "ok": False,
                       "error": {"code": "protocol_error", "message": "bad message"}})
            conn.close()
            return
        call_id = message.get("id")
        if call_id in conn.in_flight:
            return conn.send({"type": "reply", "id": call_id, "ok": False,
                              "error": {"code": "duplicate_id", "message": "in flight"}})
        if len(conn.in_flight) >= self.max_in_flight:
            return conn.send({"type": "reply", "id": call_id, "ok": False,
                              "error": {"code": "too_many_in_flight", "message": "too many",
                                        "data": {"limit": self.max_in_flight}}})
        if message.get("method") in self.breaks:
            conn.close()
            return
        conn.in_flight[call_id] = message
        threading.Thread(target=self._answer, args=(conn, message), daemon=True).start()

    def _answer(self, conn, message):
        method = message["method"]
        params = message.get("params") or {}
        held = self.held.get(method)
        if held is not None:
            held.wait(30)
        reply = {"type": "reply", "id": message["id"]}
        try:
            if method == "subscribe":
                result = self._subscribe(conn, params)
            elif method == "unsubscribe":
                if conn.subscriptions.pop(params.get("subscription"), None) is None:
                    raise FakeError("unknown_subscription", "no such subscription")
                result = {"subscription": params.get("subscription"), "ended": True}
            elif method == "catalogue" and self.catalogue is not None:
                result = self.catalogue
            else:
                result = self._run(method, params)
            shaped = False
            shape = message.get("shape")
            if shape and "shape" in self.features and isinstance(result, dict):
                result = _fields(result, shape.get("fields"))
                shaped = True
            reply.update(ok=True, shaped=shaped, result=result, elapsed_ms=0.2, queue_ms=1.5, frame=1000)
        except FakeError as error:
            reply.update(ok=False, error=error.error)
        conn.in_flight.pop(message["id"], None)
        conn.send(reply)

    def _subscribe(self, conn, params):
        if "subscriptions" not in self.features:
            raise FakeError("method_not_found", "no subscriptions")
        if self.subscription_limit is not None and len(conn.subscriptions) >= self.subscription_limit:
            raise FakeError("subscription_limit", "too many", {"limit": self.subscription_limit, "requested": 1,
                                                                 "projected_ms_per_s": 0.0})
        sub_id = f"s{next(self._sub_ids)}"
        conn.subscriptions[sub_id] = params
        if params.get("topic") == "world":
            return {"subscription": sub_id, "interval_s": None, "values": 0, "frame": 1000, "result": None}
        first = self._run("read_devices", params) if "read_devices" in self.handlers else {"results": []}
        return {"subscription": sub_id, "interval_s": params.get("interval_s", 1.0), "values": 1, "frame": 1000,
                "result": first}

    def _run(self, method, params):
        handler = self.handlers.get(method)
        if handler is None:
            raise FakeError("method_not_found", f"Unknown StationGodMCP method '{method}'.")
        return handler(params)


def _fields(result, fields):
    """The fake mod's shaping: single names only, enough to see that shape reached it."""
    if not fields or not isinstance(result, dict):
        return result
    shaped = {}
    for key, value in result.items():
        if isinstance(value, list):
            value = [{k: v for k, v in entry.items() if k in fields} if isinstance(entry, dict) else entry
                     for entry in value]
        shaped[key] = value
    return shaped


def _recv(sock):
    try:
        return sock.recv(65536)
    except OSError:
        return b""


def _shut(sock):
    try:
        sock.shutdown(socket.SHUT_RDWR)
    except OSError:
        pass
    sock.close()
