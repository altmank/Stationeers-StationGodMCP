"""The hub as an MCP client sees it: start StationGodMCP.Server on a test instance's pipe, speak MCP over its stdio,
close its stdin (session end) or kill it.

The failure-semantics suite (PLAN 3.3) drives every case through this, the way Claude Code would.
"""
import json
import os
import subprocess
import threading
import time

DEFAULT_EXE = os.path.join(os.environ.get("LOCALAPPDATA", ""), "StationGodMCP", "build-hub", "bin", "Release",
                           "net8.0", "StationGodMCP.Server.exe")


class HubProcess:
    """One hub process serving MCP on stdio. tool() calls a verb and answers (text, is_error)."""

    def __init__(self, pipe, exe=None, extra_args=(), journal_dir=None, record=None, env=None, tcp=None):
        """tcp=(port, secret) connects over TCP (through the suite's proxy) instead of the pipe."""
        args = [exe or os.environ.get("STATIONGOD_HUB_EXE", DEFAULT_EXE)]
        env = dict(env or {})
        if tcp:
            args += ["--host", "127.0.0.1", "--port", str(tcp[0])]
            env["STATIONGODMCP_SECRET"] = tcp[1]
        else:
            args += ["--pipe", pipe]
        if journal_dir:
            args += ["--journal-dir", journal_dir]
        if record:
            args += ["--record", record]
        args += list(extra_args)
        full_env = dict(os.environ)
        full_env.update(env or {})
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     env=full_env)
        self.stderr_lines = []
        self._next = 0
        self._lock = threading.Lock()
        self._pending = {}
        self._notifications = []
        threading.Thread(target=self._read_out, daemon=True).start()
        threading.Thread(target=self._read_err, daemon=True).start()
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "sg-gate", "version": "1"}})
        self.notify("notifications/initialized", {})

    # ---- MCP plumbing ----------------------------------------------------------------------------------------------

    def _read_out(self):
        for raw in self.proc.stdout:
            try:
                msg = json.loads(raw)
            except ValueError:
                continue
            if "id" in msg and msg["id"] in self._pending:
                slot = self._pending[msg["id"]]
                slot["reply"] = msg
                slot["event"].set()
            else:
                self._notifications.append(msg)
        for slot in list(self._pending.values()):
            slot["event"].set()

    def _read_err(self):
        for raw in self.proc.stderr:
            self.stderr_lines.append(raw.decode("utf-8", "replace").rstrip())

    def _send(self, msg):
        self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def notify(self, method, params):
        self._send({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method, params, timeout=660):
        with self._lock:
            self._next += 1
            rid = self._next
            slot = {"event": threading.Event(), "reply": None}
            self._pending[rid] = slot
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        if not slot["event"].wait(timeout):
            raise TimeoutError("%s did not answer in %s s" % (method, timeout))
        self._pending.pop(rid, None)
        if slot["reply"] is None:
            raise ConnectionError("the hub closed before answering %s" % method)
        return slot["reply"]

    def request_async(self, method, params):
        """Sends a request and returns a function that waits for its reply."""
        holder = {}

        def run():
            try:
                holder["reply"] = self.request(method, params)
            except Exception as error:  # noqa: BLE001 - handed back to the waiter
                holder["error"] = error

        thread = threading.Thread(target=run, daemon=True)
        thread.start()

        def wait(timeout=660):
            thread.join(timeout)
            if "error" in holder:
                raise holder["error"]
            return holder.get("reply")
        return wait

    # ---- tools -----------------------------------------------------------------------------------------------------

    def tool(self, name, arguments, timeout=660):
        """Calls a tool; answers (text, is_error) where text is the joined text content."""
        reply = self.request("tools/call", {"name": name, "arguments": arguments}, timeout)
        if "error" in reply:
            return json.dumps(reply["error"]), True
        result = reply.get("result") or {}
        text = "\n".join(part.get("text", "") for part in result.get("content") or [] if part.get("type") == "text")
        return text, bool(result.get("isError"))

    def tool_json(self, name, arguments, timeout=660):
        """Calls a tool whose reply is JSON; answers (object, is_error)."""
        text, is_error = self.tool(name, arguments, timeout)
        try:
            return json.loads(text), is_error
        except ValueError:
            return {"text": text}, is_error

    def run_script(self, code, confirm=False, max_wait=None, timeout=660):
        args = {"code": code}
        if confirm:
            args["confirm"] = True
        if max_wait is not None:
            args["max_wait"] = max_wait
        return self.tool("run_script", args, timeout)

    # ---- endings ---------------------------------------------------------------------------------------------------

    def close_stdin(self):
        """Session end as Claude Code does it: stdin closes; the client kills the hub about 0.4 s later."""
        self.proc.stdin.close()

    def end_session_like_claude(self, kill_after_s=0.4):
        start = time.time()
        self.close_stdin()
        try:
            self.proc.wait(kill_after_s)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(5)
        return time.time() - start

    def kill(self):
        self.proc.kill()
        self.proc.wait(5)

    def alive(self):
        return self.proc.poll() is None
