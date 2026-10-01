"""read_devices against the single tools, on a running game. Read only.

For a set of devices it asks read_devices for their logic, slots, atmospheres (own, internal, every pipe port) and
reagents plus the clock, then asks the single tools for the same things (read_logic_many, inspect_slots, connections +
atmosphere_contents, reagents, game_clock), and thing_health network_id against connections members + thing_health
reference_ids. Static fields (keys, names, sources, ids, error codes) must match exactly; live values within a
tolerance, or exactly with --exact (run with the game paused, e.g. a headless test server with its tick paused).

Every call it makes is read only: list_devices, describe_device, inspect_slots, connections, atmosphere_contents,
reagents, game_clock, read_logic_many, read_devices, thing_health.

    python tools/read_devices_equivalence.py --pipe StationGodMCP-Test
    python tools/read_devices_equivalence.py --ids 811234,811300 --exact

Exit code 0 when everything matched, 1 otherwise.
"""
import argparse
import itertools
import json
import math
import sys
import time

LIVE_ABS = 0.5           # default tolerance for live values: absolute ...
LIVE_REL = 0.02          # ... or relative, whichever is larger


class Pipe:
    """One JSON request line out, one reply line back, per connection (the mod's named pipe protocol)."""

    def __init__(self, name):
        self.path = "\\\\.\\pipe\\" + name
        self.ids = itertools.count(1)

    def call(self, method, **params):
        request = (json.dumps({"id": str(next(self.ids)), "method": method, "params": params}) + "\n").encode()
        deadline = time.monotonic() + 10
        while True:
            try:
                pipe = open(self.path, "r+b", buffering=0)
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.02)
        with pipe:
            pipe.write(request)
            reply = bytearray()
            while not reply.endswith(b"\n"):
                chunk = pipe.read(65536)
                if not chunk:
                    raise RuntimeError(f"{method}: the game closed the pipe without replying")
                reply += chunk
        return json.loads(reply)


class Report:
    def __init__(self, exact, tolerance_abs, tolerance_rel):
        self.exact = exact
        self.abs = tolerance_abs
        self.rel = tolerance_rel
        self.checked = 0
        self.problems = []

    def same(self, what, expected, actual):
        """Static: exactly equal."""
        self.checked += 1
        if expected != actual:
            self.problems.append(f"{what}: single tools {expected!r}, read_devices {actual!r}")

    def close(self, what, expected, actual):
        """A live value: equal within the tolerance (exact with --exact). NaN and the infinities come as strings."""
        self.checked += 1
        if isinstance(expected, str) or isinstance(actual, str) or expected is None or actual is None:
            if expected != actual:
                self.problems.append(f"{what}: single tools {expected!r}, read_devices {actual!r}")
            return
        if self.exact:
            ok = expected == actual
        else:
            ok = math.isclose(expected, actual, rel_tol=self.rel, abs_tol=self.abs)
        if not ok:
            self.problems.append(f"{what}: single tools {expected!r}, read_devices {actual!r}")


def result(reply):
    return reply["result"] if reply.get("ok") else None


def error_code(reply):
    return None if reply.get("ok") else (reply.get("error") or {}).get("code")


def pick_devices(pipe, ids, limit):
    if ids:
        return ids
    devices = result(pipe.call("list_devices"))["devices"]
    return [d["reference_id"] for d in devices[:limit]]


def plan(pipe, ids, max_logic):
    """The read_devices items for the devices, built from what the single tools say each one has."""
    items = []
    for ref in ids:
        described = result(pipe.call("describe_device", reference_id=ref))
        if described is None:
            items.append({"reference_id": ref, "logic": ["On"]})          # an error both sides must agree on
            continue
        names = [t["name"] for t in described["logic_types"] if t.get("readable") and t.get("name")][:max_logic]
        item = {"reference_id": ref, "reagents": True, "atmosphere": {}}
        if names:
            item["logic"] = names + ["NoSuchLogicType"]                       # one value that must fail alone
        slots = (result(pipe.call("inspect_slots", reference_id=ref)) or {}).get("slots") or []
        if slots:
            item["slots"] = [{"index": slots[0]["index"]}]
            if len(slots) > 1:
                item["slots"].append({"index": slots[1]["index"], "logic": ["Occupied", "Quantity"]})
            item["slots"].append({"index": 999})                              # slot_not_found both sides
        items.append(item)
        ends = (result(pipe.call("connections", reference_id=ref)) or {}).get("ends") or []
        for end in ends:
            if end.get("type") in ("Pipe", "PipeLiquid") and end.get("network"):
                items.append({"reference_id": ref, "atmosphere": {"port": end["index"]}})
        items.append({"reference_id": ref, "atmosphere": {"of": "internal"}})
    items.append({"reference_id": "1", "logic": ["On"]})                      # names nothing: fails its item
    return items[:128]


def compare_logic(pipe, report, item, got):
    reads = [{"reference_id": item["reference_id"], "logic_type": name} for name in item["logic"]]
    single = result(pipe.call("read_logic_many", reads=reads))["results"]
    for name, entry in zip(item["logic"], single):
        what = f"{item['reference_id']} logic {name}"
        if entry["ok"]:
            report.close(what, entry["value"], (got.get("logic") or {}).get(name))
        else:
            failed = (got.get("logic_errors") or {}).get(name) or (got.get("errors") or {}).get("logic") or {}
            report.same(what + " error", entry["error"]["code"], failed.get("code"))


def compare_slots(pipe, report, item, got):
    by_index = {s["index"]: s for s in got.get("slots") or ()}
    for slot in item["slots"]:
        what = f"{item['reference_id']} slot {slot['index']}"
        reply = pipe.call("inspect_slots", reference_id=item["reference_id"], slot_index=slot["index"])
        mine = by_index.get(slot["index"]) or {}
        if not reply.get("ok"):
            code = (mine.get("error") or (got.get("errors") or {}).get("slots") or {}).get("code")
            report.same(what + " error", error_code(reply), code)
            continue
        values = {v["logic_slot_type"]["name"]: v["value"] for v in reply["result"]["slots"][0]["logic_values"]}
        if "logic" in slot:
            for name in slot["logic"]:
                if name in values:
                    report.close(f"{what} {name}", values[name], (mine.get("logic") or {}).get(name))
                else:
                    report.same(f"{what} {name} error", "logic_not_readable",
                                ((mine.get("logic_errors") or {}).get(name) or {}).get("code"))
        else:
            report.same(what + " names", sorted(values), sorted(mine.get("logic") or {}))
            for name, value in values.items():
                report.close(f"{what} {name}", value, (mine.get("logic") or {}).get(name))


def single_atmosphere(pipe, ref, target):
    """What the single tools give for the item's atmosphere: (entry or None, error code or None). {port: n}: the
    network connections names at end n, then atmosphere_contents on it; {of: internal}: the internal entry; {}: the
    first entry when it is the id's own (internal, pipe_network, landing_pad_network), not a device's connected one."""
    if "port" in target:
        reply = pipe.call("connections", reference_id=ref)
        if not reply.get("ok"):
            return None, error_code(reply)
        end = next((e for e in reply["result"]["ends"] if e["index"] == target["port"]), None)
        if end is None or not end.get("network"):
            return None, "port_not_joined"
        ref = end["network"]["id"]
    reply = pipe.call("atmosphere_contents", reference_id=ref)
    if not reply.get("ok"):
        return None, error_code(reply)
    entries = reply["result"]["atmospheres"]
    if target.get("of") == "internal":
        entry = next((e for e in entries if e["source"] == "internal"), None)
    elif entries and ("port" in target or entries[0]["source"] in ("internal", "pipe_network", "landing_pad_network")):
        entry = entries[0]
    else:
        entry = None
    return (entry, None) if entry and entry.get("atmosphere") else (None, "no_atmosphere")


def compare_atmosphere(pipe, report, item, got):
    what = f"{item['reference_id']} atmosphere {json.dumps(item['atmosphere'])}"
    entry, code = single_atmosphere(pipe, item["reference_id"], item["atmosphere"])
    mine = got.get("atmosphere")
    if entry is None:
        report.same(what + " error", code, ((got.get("errors") or {}).get("atmosphere") or {}).get("code"))
        return
    if mine is None:
        report.same(what, "an atmosphere", (got.get("errors") or {}).get("atmosphere"))
        return
    a = entry["atmosphere"]
    report.same(what + " source", entry["source"], mine["source"])
    report.same(what + " atmosphere_id", a["reference_id"], mine["atmosphere_id"])
    for key in ("volume_l", "pressure_kpa", "temperature_k", "total_mol", "liquid_volume_l"):
        report.close(f"{what} {key}", a[key], mine[key])
    theirs = {g["gas"]: g for g in a["contents"]}
    ours = {g["gas"]: g for g in mine["contents"]}
    if report.exact:
        report.same(what + " gases", sorted(theirs), sorted(ours))
    for gas in set(theirs) & set(ours):
        report.same(f"{what} {gas} state", theirs[gas]["state"], ours[gas]["state"])
        report.close(f"{what} {gas} amount_mol", theirs[gas]["amount_mol"], ours[gas]["amount_mol"])


def compare_reagents(pipe, report, item, got):
    what = f"{item['reference_id']} reagents"
    reply = pipe.call("reagents", reference_id=item["reference_id"])
    if not reply.get("ok"):
        report.same(what + " error", error_code(reply), ((got.get("errors") or {}).get("reagents") or {}).get("code"))
        return
    mine = got.get("reagents") or {}
    report.close(what + " total", reply["result"]["total"], mine.get("total"))
    report.same(what + " names", [r["reagent"] for r in reply["result"]["reagents"]],
                [r["reagent"] for r in mine.get("reagents") or ()])


def compare_health(pipe, report, network_id):
    what = f"thing_health network {network_id}"
    members = result(pipe.call("connections", network_id=network_id, kind="pipe", limit=1000)) or {}
    ids = [m["reference_id"] for m in members.get("members") or () if m.get("member") == "pipe"]
    damaged = set()
    for i in range(0, len(ids), 256):
        for r in result(pipe.call("thing_health", reference_ids=ids[i:i + 256]))["results"]:
            if r.get("ok") and r.get("condition") in ("broken", "damaged"):
                damaged.add(r["reference_id"])
    mine = result(pipe.call("thing_health", network_id=network_id, damaged_only=True, limit=500))
    if mine is None:
        report.same(what, "a reply", "an error")
        return
    report.same(what + " pieces", len(ids), mine["pieces"])
    report.same(what + " damaged", sorted(damaged), sorted(t["reference_id"] for t in mine["things"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pipe", default="StationGodMCP", help="pipe name (default StationGodMCP)")
    parser.add_argument("--ids", default="", help="comma-separated device reference ids (default: list_devices)")
    parser.add_argument("--limit", type=int, default=20, help="devices from list_devices when --ids is not given")
    parser.add_argument("--max-logic", type=int, default=12, help="logic types per device")
    parser.add_argument("--exact", action="store_true", help="live values must match exactly (game paused)")
    parser.add_argument("--abs", type=float, default=LIVE_ABS)
    parser.add_argument("--rel", type=float, default=LIVE_REL)
    args = parser.parse_args()

    pipe = Pipe(args.pipe)
    report = Report(args.exact, args.abs, args.rel)
    ids = pick_devices(pipe, [i.strip() for i in args.ids.split(",") if i.strip()], args.limit)
    items = plan(pipe, ids, args.max_logic)
    reply = pipe.call("read_devices", items=items, include=["clock"])
    if not reply.get("ok"):
        print(f"read_devices refused: {reply.get('error')}")
        return 1
    grouped = reply["result"]
    clock = result(pipe.call("game_clock"))
    report.same("clock paused", clock["paused"], grouped["clock"]["paused"])
    report.same("clock days_past", clock["days_past"], grouped["clock"]["days_past"])
    networks = set()
    for item, got in zip(items, grouped["results"]):
        if not got["ok"]:
            reply = pipe.call("atmosphere_contents", reference_id=item["reference_id"])
            report.same(f"{item['reference_id']} item error", error_code(reply), got["error"]["code"])
            continue
        if "logic" in item:
            compare_logic(pipe, report, item, got)
        if "slots" in item:
            compare_slots(pipe, report, item, got)
        if "atmosphere" in item:
            compare_atmosphere(pipe, report, item, got)
            network = (got.get("atmosphere") or {}).get("network_id")
            if network and "port" in item["atmosphere"]:
                networks.add(network)
        if item.get("reagents"):
            compare_reagents(pipe, report, item, got)
    for network in sorted(networks)[:5]:
        compare_health(pipe, report, network)

    print(f"{len(items)} items, {report.checked} checks, {len(report.problems)} differences"
          f" ({'exact' if args.exact else f'live tolerance abs {args.abs} / rel {args.rel}'}).")
    for problem in report.problems:
        print("  " + problem)
    return 0 if not report.problems else 1


if __name__ == "__main__":
    sys.exit(main())
