"""Prove the mod's pipe reads and writes at once, and serves up to its connection limit (stage 3).

    py -3.12 tools/duplex_probe.py --pipe StationGodMCP-Test
        One connection: a read is left pending on one thread, then a game_clock request is written from another;
        the reply must arrive (round trip printed, at most --max-ms, default 400).
    py -3.12 tools/duplex_probe.py --pipe StationGodMCP-Test --connections 33
        Opens and holds connections up to the given number, each proving itself with one game_clock; the last must
        find the pipe busy, then be served once one held connection closes. Use the mod's limit plus one.

Read only: it calls game_clock and nothing else. Uses the overlapped pipe module of the Python library in
clients/python (no install needed). Refuses the default pipe name unless --allow-default-pipe is given.
"""
import argparse
import json
import os
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "clients", "python"))
from stationgod import pipe  # noqa: E402


def read_line(handle, buffer):
    while b"\n" not in buffer:
        chunk = handle.read()
        if not chunk:
            raise SystemExit("the pipe closed without a reply")
        buffer += chunk
    line, _, rest = buffer.partition(b"\n")
    buffer[:] = rest
    return json.loads(line)


def call(handle, request_id):
    handle.write((json.dumps({"id": request_id, "method": "game_clock", "params": {}}) + "\n").encode())
    reply = read_line(handle, bytearray())
    if not reply.get("ok") or reply.get("id") != request_id:
        raise SystemExit(f"unexpected reply to {request_id}: {reply}")
    return reply


def duplex(name, max_ms):
    handle = pipe.connect(name, 5.0)
    try:
        received = {}

        def reader():
            received["reply"] = read_line(handle, bytearray())
            received["at"] = time.perf_counter()

        thread = threading.Thread(target=reader, daemon=True)
        thread.start()
        time.sleep(0.3)
        if "reply" in received:
            raise SystemExit("the read returned before anything was sent")
        sent = time.perf_counter()
        handle.write((json.dumps({"id": "duplex", "method": "game_clock", "params": {}}) + "\n").encode())
        thread.join(5.0)
        if "reply" not in received:
            raise SystemExit("no reply within 5 s while a read was pending: the pipe is not full duplex")
        rtt_ms = (received["at"] - sent) * 1000
        print(json.dumps({"check": "duplex", "round_trip_ms": round(rtt_ms, 1), "reply": received["reply"]}, indent=2))
        return rtt_ms <= max_ms
    finally:
        handle.close()


def connections(name, count):
    held = []
    try:
        for index in range(count - 1):
            handle = pipe.connect(name, 5.0)
            call(handle, f"held-{index}")
            held.append(handle)
        try:
            extra = pipe.connect(name, 1.0)
            extra.close()
            print(f"connection {count} was not refused: the limit is above {count - 1}")
            return False
        except pipe.PipeBusy:
            pass
        held.pop(0).close()
        started = time.perf_counter()
        last = pipe.connect(name, 10.0)
        try:
            call(last, "last")
        finally:
            last.close()
        print(json.dumps({"check": "connections", "held": len(held) + 1, "refused_while_full": True,
                          "served_after_close_ms": round((time.perf_counter() - started) * 1000, 1)}, indent=2))
        return True
    finally:
        for handle in held:
            handle.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pipe", required=True)
    parser.add_argument("--connections", type=int)
    parser.add_argument("--max-ms", type=float, default=400.0)
    parser.add_argument("--allow-default-pipe", action="store_true")
    args = parser.parse_args()
    if args.pipe == "StationGodMCP" and not args.allow_default_pipe:
        parser.error("refusing the default pipe (the owner's game); pass --allow-default-pipe to mean it")
    ok = connections(args.pipe, args.connections) if args.connections else duplex(args.pipe, args.max_ms)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
