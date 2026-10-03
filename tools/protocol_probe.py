"""Probe protocol version 2 on a live mod, message by message (stages 4 to 6 of docs/architecture/stages.md).

Raw protocol: it writes the lines itself, so it can do what a well-behaved library would not (17 calls at once, a cancel
straight after a call). Uses the overlapped pipe module of the Python library in clients/python;
TCP with --host and --port signs in with the shared secret from --secret-env (default STATIONGODMCP_SECRET) first. Every check prints what it saw as JSON and exits 0 when it passed.

    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test hello
    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test burst --method game_clock --count 16
    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test order --device <id with a writable Setting>
    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test cancel --survey '{"min": [x,y,z], "max": [x,y,z]}'
    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test strict --method connections --params '{...}'
    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test call read_logic '{...}'
    py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test hold --seconds 90
    py -3.12 tools/protocol_probe.py --host 127.0.0.1 --port 18765 hello

The default pipe name (the owner's game) is refused unless --allow-default-pipe is given; nothing here writes to the
world except order, which restores the Setting it changed.
"""
import argparse
import json
import os
import socket
import sys
import threading
import time
import queue

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "clients", "python"))
from stationgod import pipe  # noqa: E402


class Probe:
    """One connection: lines out, and a reader thread putting every server line on a queue."""

    def __init__(self, args):
        self.args = args
        self.lines = queue.Queue()
        if args.host:
            self.sock = socket.create_connection((args.host, args.port), timeout=10)
            self.sock.settimeout(None)
            self.transport = "tcp"
            self._write = self.sock.sendall
            self._read = lambda: self.sock.recv(65536)
        else:
            self.handle = pipe.connect(args.pipe, 5.0)
            self.transport = "pipe"
            self._write = self.handle.write
            self._read = self.handle.read
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        buffer = b""
        while True:
            try:
                chunk = self._read()
            except OSError:
                chunk = b""
            if not chunk:
                self.lines.put(None)
                return
            buffer += chunk
            while b"\n" in buffer:
                line, buffer = buffer.split(b"\n", 1)
                if line.strip():
                    self.lines.put(json.loads(line))

    def send(self, message):
        self._write((json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8"))

    def send_many(self, messages):
        self._write(b"".join((json.dumps(m, separators=(",", ":")) + "\n").encode("utf-8") for m in messages))

    def next(self, timeout=10.0):
        try:
            message = self.lines.get(timeout=timeout)
        except queue.Empty:
            raise SystemExit(f"nothing from the server within {timeout} s")
        if message is None:
            raise SystemExit("the server closed the connection")
        return message

    def next_reply(self, call_id, timeout=30.0):
        deadline = time.monotonic() + timeout
        while True:
            message = self.next(max(0.1, deadline - time.monotonic()))
            if message.get("type") == "reply" and message.get("id") == call_id:
                return message
            if message.get("type") == "event":
                print(json.dumps({"event": message}))

    def hello(self):
        message = {"type": "hello", "protocol": [2], "client": {"name": self.args.client or "protocol-probe", "version": "1"}}
        if self.transport == "tcp":
            signed_in = self.sign_in()
            if signed_in.get("ok") is not True:
                return signed_in
        self.send(message)
        return self.next()

    def sign_in(self):
        """TCP: the shared secret is the first line; the server answers {"ok": true} or unauthorized and closes."""
        secret_env = getattr(self.args, "secret_env", None) or "STATIONGODMCP_SECRET"
        self.send({"type": "auth", "secret": os.environ.get(secret_env, "")})
        return self.next()

    def call(self, call_id, method, params=None, **extra):
        message = {"type": "call", "id": call_id, "method": method, "params": params or {}}
        message.update(extra)
        self.send(message)
        return self.next_reply(call_id)


def check_hello(probe, args):
    welcome = probe.hello()
    print(json.dumps(welcome, indent=2))
    ok = (welcome.get("type") == "welcome" and welcome.get("protocol") == 2
          and welcome["server"]["pipe_name"] == args.pipe and welcome["server"]["world"]["id"]
          and welcome["limits"]["max_in_flight"] == 16
          and {"shape", "cancel"} <= set(welcome.get("features", [])))
    return ok


def check_burst(probe, args):
    welcome = probe.hello()
    count = args.count
    probe.send_many([{"type": "call", "id": f"b{index}", "method": args.method, "params": {}} for index in range(count + 1)])
    seen = {}
    refused = []
    while len(seen) + len(refused) < count + 1:
        message = probe.next(30)
        if message.get("type") != "reply":
            continue
        if not message.get("ok") and message["error"]["code"] == "too_many_in_flight":
            refused.append(message["id"])
        else:
            seen[message["id"]] = seen.get(message["id"], 0) + 1
    report = {"client_id": welcome.get("client_id"), "answered": len(seen), "each_once": all(v == 1 for v in seen.values()),
              "refused_too_many_in_flight": refused}
    print(json.dumps(report, indent=2))
    return report["each_once"] and len(seen) + len(refused) == count + 1 and (
        refused == [f"b{count}"] or (not refused and len(seen) == count + 1))


def check_order(probe, args):
    probe.hello()
    target = {"reference_id": args.device, "logic_type": "Setting"}
    before = probe.call("o0", "read_logic", target)
    if not before.get("ok"):
        raise SystemExit(f"read_logic failed: {before}")
    old = before["result"]["value"]
    new = 7 if old != 7 else 8
    probe.send_many([
        {"type": "call", "id": "w", "method": "write_logic", "params": dict(target, value=new)},
        {"type": "call", "id": "r", "method": "read_logic", "params": target},
    ])
    replies = {}
    while len(replies) < 2:
        message = probe.next(30)
        if message.get("type") == "reply":
            replies[message["id"]] = message
    restored = probe.call("o9", "write_logic", dict(target, value=old))
    report = {"old": old, "written": new, "read_after_write": replies["r"].get("result", {}).get("value"),
              "write_frame": replies["w"].get("frame"), "read_frame": replies["r"].get("frame"),
              "restored": restored.get("ok")}
    print(json.dumps(report, indent=2))
    return report["read_after_write"] == new and restored.get("ok")


def check_cancel(probe, args):
    probe.hello()
    survey = json.loads(args.survey) if args.survey else {}
    probe.send_many([
        {"type": "call", "id": "survey", "method": "grid_survey", "params": survey, "shape": {"fields": ["reference_id"]}},
        {"type": "call", "id": "clock", "method": "game_clock", "params": {}},
        {"type": "cancel", "id": "clock"},
    ])
    replies = {}
    while len(replies) < 2:
        message = probe.next(60)
        if message.get("type") == "reply":
            replies[message["id"]] = message
    clock = replies["clock"]
    outcome = "result" if clock.get("ok") else clock["error"]["code"]
    report = {"clock": outcome, "survey_ok": replies["survey"].get("ok")}
    print(json.dumps(report, indent=2))
    return outcome in ("cancelled", "result")


def check_strict(probe, args):
    probe.hello()
    reply = probe.call("s", args.method, json.loads(args.params))
    print(json.dumps(reply, indent=2))
    return not reply.get("ok") and reply["error"]["code"] == "invalid_argument"


def check_call(probe, args):
    welcome = probe.hello()
    print(json.dumps({"welcome": {k: welcome.get(k) for k in ("type", "client_id", "client", "level", "cheat", "error")}}))
    if welcome.get("type") != "welcome":
        print(json.dumps(welcome, indent=2))
        return False
    reply = probe.call("c", args.method, json.loads(args.params))
    print(json.dumps(reply, indent=2))
    return True


def check_hold(probe, args):
    """Connects (with its key), prints welcome, then stays connected printing every event for --seconds; with --method
    it retries that call every 5 s, so a test can watch a cheat call go from cheat_not_armed to answered."""
    welcome = probe.hello()
    print(json.dumps(welcome, indent=2), flush=True)
    until = time.monotonic() + args.seconds
    tick = 0
    while time.monotonic() < until:
        if args.method:
            tick += 1
            probe.send({"type": "call", "id": f"h{tick}", "method": args.method, "params": json.loads(args.params)})
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                message = probe.lines.get(timeout=max(0.1, deadline - time.monotonic()))
            except queue.Empty:
                break
            if message is None:
                print("closed", flush=True)
                return True
            print(json.dumps(message), flush=True)
    return True


CHECKS = {"hello": check_hello, "burst": check_burst, "order": check_order, "cancel": check_cancel,
          "strict": check_strict, "call": check_call, "hold": check_hold}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pipe", default="StationGodMCP-Test")
    parser.add_argument("--host")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--client")
    parser.add_argument("--secret-env", default="STATIONGODMCP_SECRET")
    parser.add_argument("--allow-default-pipe", action="store_true")
    parser.add_argument("check", choices=sorted(CHECKS))
    parser.add_argument("method_arg", nargs="?")
    parser.add_argument("params_arg", nargs="?")
    parser.add_argument("--method", default="game_clock")
    parser.add_argument("--params", default="{}")
    parser.add_argument("--count", type=int, default=16)
    parser.add_argument("--device")
    parser.add_argument("--survey")
    parser.add_argument("--seconds", type=float, default=60)
    args = parser.parse_args()
    if args.method_arg:
        args.method = args.method_arg
    if args.params_arg:
        args.params = args.params_arg
    if args.pipe == "StationGodMCP" and not args.host and not args.allow_default_pipe:
        parser.error("refusing the default pipe (the owner's game); pass --allow-default-pipe to mean it")
    probe = Probe(args)
    return 0 if CHECKS[args.check](probe, args) else 1


if __name__ == "__main__":
    sys.exit(main())
