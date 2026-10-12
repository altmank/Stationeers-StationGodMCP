"""Runs the failure-semantics suite (PLAN 3.3) against a test instance and writes results.json and RESULTS.md.

    py -3 tests/gate/run_gate.py --pipe StationGodMCP-SGT1 [--only <substring>] [--out <dir>]

The instance must be a copy of the test save on the spike-branch mod build, free to change. Never LU's live server.
"""
import argparse
import json
import os
import sys
import time
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cases  # noqa: E402


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pipe", required=True)
    parser.add_argument("--exe")
    parser.add_argument("--only", action="append", default=[])
    parser.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    args = parser.parse_args()
    if "StationeersDedicated" in args.pipe or args.pipe == "StationGodMCP":
        sys.exit("refusing: the suite runs on test instances only")
    os.makedirs(args.out, exist_ok=True)
    results = []
    for group, name, fn in cases.CASES:
        if args.only and not any(o in name or o in group for o in args.only):
            continue
        ctx = cases.Ctx(args.pipe, args.exe)
        start = time.time()
        try:
            outcome = fn(ctx)
        except Exception as error:  # noqa: BLE001 - a case that crashes fails, with its traceback kept
            outcome = {"ok": False, "evidence": {"crash": traceback.format_exc()[-1500:]}}
        finally:
            stderr = [line for h in ctx.hubs for line in h.stderr_lines[-20:]]
            ctx.close()
        outcome.update(group=group, name=name, seconds=round(time.time() - start, 1), hub_stderr=stderr)
        results.append(outcome)
        print("%-5s %-12s %s (%.0f s)" % ("PASS" if outcome["ok"] else "FAIL", group, name, outcome["seconds"]),
              flush=True)
    with open(os.path.join(args.out, "results.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    with open(os.path.join(args.out, "RESULTS.md"), "w", encoding="utf-8") as f:
        f.write("| group | case | result | s |\n|---|---|---|---|\n")
        for r in results:
            f.write("| %s | %s | %s | %s |\n" % (r["group"], r["name"], "PASS" if r["ok"] else "FAIL", r["seconds"]))
    print("%d of %d passed" % (sum(r["ok"] for r in results), len(results)))


if __name__ == "__main__":
    main()
