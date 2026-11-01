"""One StationGod call against a TEST rig's pipe, through this worktree's sg client and a hub you name.

    py -3.12 sgcall.py --pipe StationGodMCP-SGT5 --hub <dir or StationGodMCP.Server.dll> <verb> [what] ['<json options>']

Refuses the live pipe name (StationGodMCP). Prints the reply, or the refusal (code, message, fix, data), as JSON.
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "clients", "python"))
import sg  # noqa: E402

LIVE_PIPES = {"StationGodMCP"}


def hub_dll(path):
    return os.path.join(path, "StationGodMCP.Server.dll") if os.path.isdir(path) else path


def connect(pipe, hub):
    if pipe in LIVE_PIPES:
        raise SystemExit(f"refusing the live pipe {pipe!r}")
    t = sg.connect(hub_dll(hub), "--pipe", pipe, client="sg-silo-rig")
    got = ((t.welcome or {}).get("server") or {}).get("pipe_name")
    if got is not None and got != pipe:
        raise SystemExit(f"pipe answered as {got!r}, not {pipe!r}; refusing")
    return t


def call(verb, what=None, **options):
    """The verb's reply as plain JSON data, or {"error": {...}} for a refusal."""
    fn = getattr(sg, verb)
    try:
        reply = fn(what, **options) if what is not None else fn(**options)
        return json.loads(json.dumps(reply, default=lambda o: list(o) if hasattr(o, "__iter__") else str(o)))
    except sg.Error as e:
        return {"error": {"code": e.code, "message": e.message, "fix": e.fix, "data": e.data}}


def wait_ready(timeout_s=600):
    """Waits until the game answers server info and its clock runs (unpausing it); returns the clock."""
    import time
    end = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < end:
        info = call("server", "info")
        if "error" not in info:
            clock = call("read", "clock")
            if "error" not in clock and not clock.get("paused"):
                return clock
            call("server", "command", command="pause false", confirm=True)
            last = clock
        else:
            last = info
        time.sleep(3)
    raise SystemExit(f"not ready after {timeout_s} s: {last}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pipe", required=True)
    ap.add_argument("--hub", required=True)
    ap.add_argument("verb")
    ap.add_argument("what", nargs="?")
    ap.add_argument("options", nargs="?", default="{}")
    a = ap.parse_args()
    what, options = a.what, a.options
    if what is not None and what.lstrip().startswith("{"):
        what, options = None, what
    connect(a.pipe, a.hub)
    if a.verb == "wait":
        print(json.dumps(wait_ready(), indent=1))
        return
    if a.verb == "batch":
        # what is a JSON list of [verb, what|null, {options}]; each reply is printed after its call.
        for verb, what, options in json.loads(a.what):
            print(f"### {verb} {what or ''} {json.dumps(options)}")
            print(json.dumps(call(verb, what, **options), indent=1))
        sg.close()
        return
    print(json.dumps(call(a.verb, what, **json.loads(options)), indent=1))
    sg.close()


if __name__ == "__main__":
    main()
