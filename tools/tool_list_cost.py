"""What a tool list costs a fresh Claude Code context, in real tokens (the served probe's calibration method).

    py -3.12 tools/tool_list_cost.py measure <name>=<tools file> [<name>=<tools file> ...] [--out file.json]
    py -3.12 tools/tool_list_cost.py serve <tools file>          (the stub MCP server the measure runs start)

A tools file is {"instructions": str, "tools": [MCP tool objects]}. measure runs one-turn Opus calls with a trivial
prompt: two with no MCP server (Claude Code's default deferred loading, and every schema loaded:
ENABLE_TOOL_SEARCH=false), then one per tools file with every schema loaded, and one with the default deferred loading
for the first file. A list's full cost is its run's first-turn context minus the no-MCP run in the same loading mode:
the tool list plus instructions as the client sends them. (The probe's calibrate.py took the all-loaded run minus the
deferred no-MCP run, so its figure also counts Claude Code's own deferred built-in tools, about 16k tokens.) It also prints each list's compact JSON
size, so a share of the cost can be given to each tool (the probe's per-tool estimate). No game, no StationGod server:
the stub answers initialize and tools/list, and refuses every call.
"""
import json
import os
import subprocess
import sys
import tempfile
import time

SERVER = "stationgod"
PROMPT = "Reply with the single word ok. Do not call any tool."


def compact(obj):
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


# ---------------------------------------------------------------- the stub server
def serve(tools_path):
    with open(tools_path, encoding="utf-8") as f:
        surface = json.load(f)
    for line in sys.stdin:
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if "id" not in msg:
            continue
        method = msg.get("method")
        if method == "initialize":
            result = {"protocolVersion": (msg.get("params") or {}).get("protocolVersion", "2025-06-18"),
                      "capabilities": {"tools": {}}, "serverInfo": {"name": "StationGodMCP", "version": "0"},
                      "instructions": surface.get("instructions", "")}
        elif method == "tools/list":
            result = {"tools": surface["tools"]}
        elif method == "tools/call":
            result = {"content": [{"type": "text", "text": "not served"}], "isError": True}
        else:
            result = {}
        sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": result}) + "\n")
        sys.stdout.flush()


# ---------------------------------------------------------------- the measure
def one(label, mcp_path, tool_search, cwd):
    env = {k: v for k, v in os.environ.items()
           if k not in ("ENABLE_TOOL_SEARCH", "ANTHROPIC_API_KEY", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT")}
    if tool_search is not None:
        env["ENABLE_TOOL_SEARCH"] = tool_search
    cmd = ["claude", "-p", PROMPT, "--model", "opus", "--output-format", "stream-json", "--verbose",
           "--mcp-config", mcp_path, "--strict-mcp-config", "--setting-sources", "project,local",
           "--no-session-persistence", "--permission-mode", "dontAsk", "--max-turns", "1"]
    p = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       timeout=600)
    first, mcp_tools = None, 0
    for line in p.stdout.splitlines():
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if e.get("type") == "system" and e.get("subtype") == "init":
            mcp_tools = len([t for t in e.get("tools") or [] if t.startswith("mcp__")])
        if e.get("type") == "assistant" and first is None:
            u = e["message"].get("usage") or {}
            first = (u.get("input_tokens") or 0) + (u.get("cache_read_input_tokens") or 0) + \
                (u.get("cache_creation_input_tokens") or 0)
    if first is None:
        raise SystemExit("no usage from the %s run: %s" % (label, (p.stderr or p.stdout)[-2000:]))
    return {"label": label, "first_context": first, "mcp_tools_at_init": mcp_tools}


def measure(pairs, out):
    work = tempfile.mkdtemp(prefix="tool-list-cost-")
    empty = os.path.join(work, "empty.json")
    with open(empty, "w") as f:
        json.dump({"mcpServers": {}}, f)
    base_deferred = one("no_mcp_deferred", empty, None, work)
    base = one("no_mcp_loaded", empty, "false", work)
    rows = []
    for index, (name, path) in enumerate(pairs):
        with open(path, encoding="utf-8") as f:
            surface = json.load(f)
        mcp = os.path.join(work, "mcp-%s.json" % name)
        with open(mcp, "w") as f:
            json.dump({"mcpServers": {SERVER: {"command": sys.executable,
                                               "args": ["-I", os.path.abspath(__file__), "serve",
                                                        os.path.abspath(path)]}}}, f)
        loaded = one(name, mcp, "false", work)
        row = {"name": name, "tools": len(surface["tools"]), "first_context": loaded["first_context"],
               "full_cost": loaded["first_context"] - base["first_context"],
               "mcp_tools_at_init": loaded["mcp_tools_at_init"],
               "chars": len(compact(surface["tools"])) + len(surface.get("instructions", "")),
               "per_tool_chars": {t["name"]: len(compact(t)) for t in surface["tools"]},
               "instructions_chars": len(surface.get("instructions", ""))}
        if index == 0:
            row["deferred_cost"] = one(name + "_deferred", mcp, None, work)["first_context"] -                 base_deferred["first_context"]
        rows.append(row)
        print("%s: %d tools, full %d tokens, %d chars" % (name, row["tools"], row["full_cost"], row["chars"]))
    result = {"taken": time.strftime("%Y-%m-%dT%H:%M:%S"),
              "claude_version": subprocess.run(["claude", "--version"], capture_output=True, text=True).stdout.strip(),
              "no_mcp_first_context": {"deferred": base_deferred["first_context"], "loaded": base["first_context"]},
              "lists": rows}
    if out:
        with open(out, "w", encoding="utf-8", newline="\n") as f:
            json.dump(result, f, indent=1)
    return result


def main(argv):
    if len(argv) >= 2 and argv[0] == "serve":
        serve(argv[1])
    elif len(argv) >= 2 and argv[0] == "measure":
        out = None
        args = list(argv[1:])
        if "--out" in args:
            i = args.index("--out")
            out = args[i + 1]
            del args[i:i + 2]
        measure([tuple(a.split("=", 1)) for a in args], out)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
