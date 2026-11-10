"""Runs the failure-semantics suite (PLAN 3.3) against a test instance and writes results.json and RESULTS.md.

    py -3 tests/gate/run_gate.py --pipe <test pipe> [--fixtures <file>] [--reset <command>]
                                 [--only <substring>] [--out <dir>]

The instance must have loaded a copy of the sg-gate test world (tools/test-worlds), free to change, with its
required mods. --fixtures defaults to SG_GATE_FIXTURES, else sg-gate/fixtures.json in STATIONGOD_TEST_WORLDS (the
world library). --reset (else SG_GATE_RESET) is the command the world-load case runs to reload the sg-gate world
on the instance; see cases.reset_command. Never LU's live server.
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
    parser.add_argument("--fixtures")
    parser.add_argument("--reset", help="command that reloads the sg-gate world (default: SG_GATE_RESET)")
    parser.add_argument("--only", action="append", default=[])
    parser.add_argument("--skip", action="append", default=[])
    parser.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    args = parser.parse_args()
    if "StationeersDedicated" in args.pipe or args.pipe == "StationGodMCP":
        sys.exit("refusing: the suite runs on test instances only")
    cases.use_fix(args.fixtures)
    cases.RESET = args.reset
    os.makedirs(args.out, exist_ok=True)
    results = []
    for group, name, fn in cases.CASES:
        if (args.only and not any(o in name or o == group for o in args.only)) or any(s in name for s in args.skip):
            continue
        start = time.time()
        ctx = None
        try:
            ctx = cases.Ctx(args.pipe, args.exe)
            outcome = fn(ctx)
        except Exception as error:  # noqa: BLE001 - a case that crashes fails, with its traceback kept
            outcome = {"ok": False, "evidence": {"crash": traceback.format_exc()[-1500:]}}
        finally:
            stderr = [line for h in ctx.hubs for line in h.stderr_lines[-20:]] if ctx else []
            if ctx:
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
