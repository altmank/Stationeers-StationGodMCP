"""Today's cost of a fixed set of calls, measured over the version-1 pipe protocol. Read only.

For each call in the set it sends the same request 20 times on one kept connection and records the reply's size in
bytes, the round trip and the mod's own elapsed_ms; then it reads mod_info and keeps its runtime section (per method:
handler, serialisation and queue-wait times, reply bytes). The set:

    thing_health of 84 structure ids     thing_health scan, limit 500     list_devices of the world
    find_things kind structure           grid_survey of one room          read_devices of 5, 20 and 128 items
    game_clock

Everything it writes goes to the corpus folder, outside every repository, one folder per run:

    %LOCALAPPDATA%\\StationGodMCP\\corpus\\<source>\\baseline-<UTC time>\\report.json
                                                                 replies\\<call>.json   (the first reply of each call)

A first reply over 2 MB is not kept, and nothing is written that would take the whole corpus folder past 50 MB.
The corpus holds replies from a game, which are its owner's data: never copy it into a repository.

    py -3.12 tools/protocol_baseline.py --pipe StationGodMCP-Test --source test-server
    py -3.12 tools/protocol_baseline.py --pipe StationGodMCP --source owner-game     (only with the owner's OK)
    py -3.12 tools/protocol_baseline.py --self-test

Exit code 0 when the report has everything the baseline needs, 1 when it lacks something (listed), 2 when the pipe
could not be reached.
"""
import argparse
import datetime
import itertools
import json
import os
import statistics
import sys
import time

SAMPLES = 20
REFERENCE_REPLY_CAP = 2 * 1024 * 1024
CORPUS_CAP = 50 * 1024 * 1024
HEALTH_IDS = 84
READ_SIZES = (5, 20, 128)
LOGIC_PER_DEVICE = 4          # readable logic types asked per device in the read_devices calls
DESCRIBED_DEVICES = 32        # distinct devices the read_devices items cycle through
SOURCES = ("test-server", "owner-game")
OWNER_PIPE = "StationGodMCP"
RUNTIME_TALLIES = ("handler_ms", "serialize_ms", "queue_wait_ms", "reply_bytes")


def corpus_root():
    return os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "StationGodMCP", "corpus")


class Unreachable(Exception):
    pass


class Pipe:
    """One kept connection to the mod's pipe: a JSON request line out, a reply line back. Opened again once when a
    kept connection turns out broken (every call this tool makes is read only)."""

    def __init__(self, name):
        self.path = "\\\\.\\pipe\\" + name
        self.ids = itertools.count(1)
        self.handle = None

    def exchange(self, method, params):
        """(reply bytes as received, round trip in ms)."""
        line = (json.dumps({"id": str(next(self.ids)), "method": method, "params": params}) + "\n").encode()
        for attempt in (0, 1):
            if self.handle is None:
                self.handle = self._open()
            started = time.perf_counter()
            try:
                self.handle.write(line)
                reply = bytearray()
                while not reply.endswith(b"\n"):
                    chunk = self.handle.read(65536)
                    if not chunk:
                        raise OSError("the game closed the pipe without replying")
                    reply += chunk
                return bytes(reply), (time.perf_counter() - started) * 1000
            except OSError as error:
                self.close()
                if attempt == 1:
                    raise Unreachable(f"{method}: {error}") from error
        raise AssertionError("unreachable")

    def close(self):
        handle, self.handle = self.handle, None
        if handle is not None:
            try:
                handle.close()
            except OSError:
                pass

    def _open(self):
        deadline = time.monotonic() + 10
        while True:
            try:
                return open(self.path, "r+b", buffering=0)
            except OSError as error:
                if time.monotonic() > deadline:
                    raise Unreachable(f"no pipe {self.path}: {error}") from error
                time.sleep(0.02)


class Caller:
    """Calls through an exchange function: parses the reply envelope and keeps its raw size and timing."""

    def __init__(self, exchange):
        self.exchange = exchange

    def sample(self, method, params):
        """(the sample row, the parsed envelope, the raw reply line)."""
        raw, rtt = self.exchange(method, params)
        envelope = json.loads(raw)
        row = {"bytes": len(raw), "rtt_ms": round(rtt, 3), "elapsed_ms": envelope.get("elapsed_ms"),
               "ok": bool(envelope.get("ok"))}
        if not row["ok"]:
            row["error_code"] = (envelope.get("error") or {}).get("code")
        return row, envelope, raw

    def result(self, method, params):
        _, envelope, _ = self.sample(method, params)
        if not envelope.get("ok"):
            error = envelope.get("error") or {}
            raise RuntimeError(f"{method} {json.dumps(params)}: {error.get('code')}: {error.get('message')}")
        return envelope["result"]


def plan_calls(caller):
    """The fixed set: (name, method, params, note). The ids, room and devices come from the world being measured."""
    structures = caller.result("find_things", {"kind": "structure", "limit": HEALTH_IDS})["things"]
    ids = [thing["reference_id"] for thing in structures]
    devices = [device["reference_id"] for device in caller.result("list_devices", {})["devices"]]
    rooms = caller.result("rooms", {"include_devices": False})["rooms"]

    calls = [
        ("thing_health_84_ids", "thing_health", {"reference_ids": ids}, f"{len(ids)} structure ids"),
        ("thing_health_scan_500", "thing_health", {"limit": 500}, "scan"),
        ("list_devices_world", "list_devices", {}, f"{len(devices)} devices in the world"),
        ("find_things_structure", "find_things", {"kind": "structure"}, "default limit"),
    ]
    if rooms:
        room = rooms[0]
        calls.append(("grid_survey_room", "grid_survey", {"room_id": room["room_id"]},
                      f"room {room['room_id']}, {room.get('cell_count')} cells"))
    items = read_items(caller, devices)
    for size in READ_SIZES:
        chosen = [items[index % len(items)] for index in range(size)] if items else []
        calls.append((f"read_devices_{size}", "read_devices", {"items": chosen},
                      f"{size} items over {min(size, len(items))} distinct devices"))
    calls.append(("game_clock", "game_clock", {}, ""))
    return calls


def read_items(caller, devices):
    """read_devices items: up to DESCRIBED_DEVICES devices, each with its first readable logic types."""
    items = []
    for reference_id in devices[:DESCRIBED_DEVICES]:
        described = caller.result("describe_device", {"reference_id": reference_id})
        names = [t["name"] for t in described.get("logic_types") or () if t.get("readable") and t.get("name")]
        if names:
            items.append({"reference_id": reference_id, "logic": names[:LOGIC_PER_DEVICE]})
    return items


def tally(values):
    values = [v for v in values if isinstance(v, (int, float))]
    if not values:
        return None
    return {"min": round(min(values), 3), "mean": round(statistics.fmean(values), 3),
            "median": round(statistics.median(values), 3), "max": round(max(values), 3)}


def measure(caller, calls, samples, keep_reply):
    """Each call samples times; keep_reply(name, envelope bytes) stores the first reply and says where."""
    measured = []
    for name, method, params, note in calls:
        rows = []
        kept = None
        for index in range(samples):
            row, _, raw = caller.sample(method, params)
            rows.append(row)
            if index == 0:
                kept = keep_reply(name, raw)
        measured.append({
            "name": name, "method": method, "note": note, "params_keys": sorted(params),
            "items": len(params.get("items") or params.get("reference_ids") or ()) or None,
            "samples": rows, "sample_count": len(rows),
            "errors": sum(1 for row in rows if not row["ok"]),
            "bytes": tally([row["bytes"] for row in rows]),
            "rtt_ms": tally([row["rtt_ms"] for row in rows]),
            "elapsed_ms": tally([row["elapsed_ms"] for row in rows]),
            "reference_reply": kept,
        })
    return measured


def build_report(source, pipe_name, mod_info, clock, measured, started, finished):
    return {
        "tool": "protocol_baseline", "format": 1, "source": source, "pipe": pipe_name,
        "started_utc": started, "finished_utc": finished,
        "mod": {"mod_version": mod_info.get("mod_version"), "pipe_name": mod_info.get("pipe_name")},
        "clock": clock, "samples_per_call": SAMPLES, "calls": measured,
        "runtime": mod_info.get("runtime"),
    }


def check_report(report, samples=SAMPLES):
    """What the baseline needs and the report lacks; empty when complete."""
    problems = []
    names = {call["name"] for call in report.get("calls") or ()}
    wanted = ["thing_health_84_ids", "thing_health_scan_500", "list_devices_world", "find_things_structure",
              "grid_survey_room", "game_clock"] + [f"read_devices_{size}" for size in READ_SIZES]
    for name in wanted:
        if name not in names:
            problems.append(f"call {name} is missing")
    for call in report.get("calls") or ():
        if call.get("sample_count") != samples or len(call.get("samples") or ()) != samples:
            problems.append(f"{call.get('name')}: {call.get('sample_count')} samples, not {samples}")
        for index, row in enumerate(call.get("samples") or ()):
            for key in ("bytes", "rtt_ms", "elapsed_ms", "ok"):
                if row.get(key) is None:
                    problems.append(f"{call.get('name')} sample {index}: no {key}")
        if call.get("errors"):
            problems.append(f"{call.get('name')}: {call['errors']} of its samples were errors")
    runtime = report.get("runtime")
    if not isinstance(runtime, dict):
        problems.append("no mod_info.runtime")
        return problems
    methods = {entry.get("method"): entry for entry in runtime.get("methods") or ()}
    for method in sorted({call.get("method") for call in report.get("calls") or ()}):
        entry = methods.get(method)
        if entry is None:
            problems.append(f"runtime.methods has no {method}")
            continue
        for key in RUNTIME_TALLIES:
            if not isinstance(entry.get(key), dict) or entry[key].get("mean") is None:
                problems.append(f"runtime.methods {method}: no {key}")
    return problems


def corpus_size(root):
    total = 0
    for folder, _, files in os.walk(root):
        for file in files:
            try:
                total += os.path.getsize(os.path.join(folder, file))
            except OSError:
                pass
    return total


class Corpus:
    """One run's folder in the corpus; refuses any write that would take the corpus past its cap."""

    def __init__(self, root, source, stamp):
        self.root = root
        self.folder = os.path.join(root, source, f"baseline-{stamp}")
        self.skipped = []

    def keep_reply(self, name, raw):
        if len(raw) > REFERENCE_REPLY_CAP:
            self.skipped.append(f"{name}: {len(raw)} bytes, over the {REFERENCE_REPLY_CAP} byte cap")
            return None
        if corpus_size(self.root) + len(raw) > CORPUS_CAP:
            self.skipped.append(f"{name}: the corpus would pass {CORPUS_CAP} bytes")
            return None
        path = os.path.join(self.folder, "replies", f"{name}.json")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as file:
            file.write(raw)
        return os.path.relpath(path, self.folder)

    def write_report(self, report):
        os.makedirs(self.folder, exist_ok=True)
        path = os.path.join(self.folder, "report.json")
        with open(path, "w", encoding="utf-8") as file:
            json.dump(report, file, indent=1)
        return path


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def run(pipe_name, source, root, samples):
    pipe = Pipe(pipe_name)
    caller = Caller(pipe.exchange)
    started = utc_now()
    stamp = started.replace(":", "").replace("-", "")
    corpus = Corpus(root, source, stamp)
    try:
        mod_info = caller.result("mod_info", {})
        calls = plan_calls(caller)
        measured = measure(caller, calls, samples, corpus.keep_reply)
        clock = caller.result("game_clock", {})
        mod_info = caller.result("mod_info", {})
    finally:
        pipe.close()
    report = build_report(source, pipe_name, mod_info, clock, measured, started, utc_now())
    report["reference_replies_skipped"] = corpus.skipped
    path = corpus.write_report(report)
    return report, path


def print_summary(report, path):
    print(f"{report['source']} on pipe {report['pipe']}, mod {report['mod']['mod_version']}: {path}")
    print(f"{'call':26} {'bytes':>10} {'rtt ms':>9} {'elapsed ms':>11}")
    for call in report["calls"]:
        print(f"{call['name']:26} {call['bytes']['mean']:>10.0f} {call['rtt_ms']['mean']:>9.2f} "
              f"{(call['elapsed_ms'] or {}).get('mean', float('nan')):>11.2f}")
    for line in report.get("reference_replies_skipped") or ():
        print(f"reference reply not kept: {line}")


# ---- self-test: the report builder and its check on canned replies, no game ----

def canned_exchange():
    """A fake mod: canned replies for every method the tool calls, and mod_info.runtime for what was called."""
    called = {}

    def reply(result, elapsed=0.5):
        return (json.dumps({"id": "1", "ok": True, "result": result, "elapsed_ms": elapsed}) + "\n").encode()

    def runtime():
        tally_of = {"total": 1.0, "mean": 0.5, "max": 0.9}
        return {"uptime_s": 10.0, "methods": [
            {"method": method, "calls": count, "errors": 0, "main_thread_ms": 1.0, "handler_ms": tally_of,
             "serialize_ms": tally_of, "queue_wait_ms": tally_of, "reply_bytes": {"total": 100, "mean": 50, "max": 90}}
            for method, count in sorted(called.items())]}

    def exchange(method, params):
        called[method] = called.get(method, 0) + 1
        if method == "mod_info":
            return reply({"mod_version": "1.10.0", "pipe_name": "fake", "runtime": runtime()}), 1.0
        if method == "find_things":
            return reply({"things": [{"reference_id": str(100 + i)} for i in range(params.get("limit") or 3)]}), 2.0
        if method == "list_devices":
            return reply({"devices": [{"reference_id": str(200 + i)} for i in range(6)]}), 2.0
        if method == "rooms":
            return reply({"rooms": [{"room_id": "7", "cell_count": 12}]}), 1.0
        if method == "describe_device":
            return reply({"logic_types": [{"name": "On", "readable": True}, {"name": "Power", "readable": True}]}), 1.0
        if method == "game_clock":
            return reply({"game_time_s": 1.0, "paused": False}), 0.4
        return reply({"results": [], "count": 0}), 3.0

    return exchange


def self_test():
    import tempfile
    caller = Caller(canned_exchange())
    calls = plan_calls(caller)
    with tempfile.TemporaryDirectory() as root:
        corpus = Corpus(root, "test-server", "self-test")
        measured = measure(caller, calls, SAMPLES, corpus.keep_reply)
        mod_info = caller.result("mod_info", {})
        report = build_report("test-server", "fake", mod_info, {"game_time_s": 1.0}, measured, utc_now(), utc_now())
        problems = check_report(report)
        kept = [call["reference_reply"] for call in report["calls"]]
        path = corpus.write_report(report)
        written = json.load(open(path, encoding="utf-8"))
        # The check must also catch a short report.
        broken = json.loads(json.dumps(report))
        broken["calls"] = broken["calls"][1:]
        broken["calls"][0]["samples"].pop()
        for entry in broken["runtime"]["methods"]:
            if entry["method"] == "game_clock":
                del entry["serialize_ms"]
        caught = check_report(broken)
    failures = list(problems)
    if any(k is None for k in kept):
        failures.append("a first reply was not kept")
    if written != report:
        failures.append("report.json differs from the report")
    for expected in ("call thing_health_84_ids is missing", "samples, not", "no serialize_ms"):
        if not any(expected in line for line in caught):
            failures.append(f"the check missed: {expected}")
    for failure in failures:
        print(f"FAIL {failure}")
    print("self-test " + ("failed" if failures else f"passed: {len(report['calls'])} calls, every field present"))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pipe", help="pipe name, e.g. StationGodMCP-Test")
    parser.add_argument("--source", choices=SOURCES, help="the corpus folder the run goes to")
    parser.add_argument("--corpus", default=corpus_root(), help="corpus folder (default %(default)s)")
    parser.add_argument("--samples", type=int, default=SAMPLES, help=argparse.SUPPRESS)
    parser.add_argument("--self-test", action="store_true", help="check the report builder on canned replies")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if not args.pipe or not args.source:
        parser.error("--pipe and --source are required")
    if (args.pipe == OWNER_PIPE) != (args.source == "owner-game"):
        parser.error(f"--source owner-game goes with the owner's pipe {OWNER_PIPE}, and only with it")
    try:
        report, path = run(args.pipe, args.source, args.corpus, args.samples)
    except Unreachable as error:
        print(f"unreachable: {error}", file=sys.stderr)
        return 2
    print_summary(report, path)
    problems = check_report(report, args.samples)
    for problem in problems:
        print(f"MISSING {problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
