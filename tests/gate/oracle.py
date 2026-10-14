"""The suite's own eyes on the world: a direct connection to the test instance's mod, beside the hub, for setting a
case up and for reading what really happened. It never goes through the hub under test.
"""
import itertools
import os
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "tools"))
from modwire.connection import PipeTarget, open_connection  # noqa: E402


class Oracle:
    """Calls verb whats on the mod directly, one at a time, and keeps the events it hears."""

    def __init__(self, pipe):
        for attempt in range(6):
            try:
                self.conn = open_connection(PipeTarget(pipe), client="sg-gate-oracle")
                break
            except Exception:  # noqa: BLE001 - a busy or slow game: try again, then give up
                if attempt == 5:
                    raise
                time.sleep(5)
        self.welcome = self.conn.welcome
        self._ids = itertools.count(1)
        self._replies = {}
        self._cond = threading.Condition()
        self.events = []
        self.conn.start(self._on_message, lambda *a: None)

    def _on_message(self, conn, msg, size=0):
        with self._cond:
            if msg.get("type") == "reply":
                self._replies[msg.get("id")] = msg
                self._cond.notify_all()
            elif msg.get("type") == "event":
                self.events.append(msg)

    def call(self, method, timeout=30, **params):
        rid = "o%d" % next(self._ids)
        self.conn.send({"type": "call", "id": rid, "method": method, "params": params})
        end = time.time() + timeout
        with self._cond:
            while rid not in self._replies:
                left = end - time.time()
                if left <= 0:
                    raise TimeoutError(method)
                self._cond.wait(left)
            reply = self._replies.pop(rid)
        if not reply.get("ok"):
            raise RuntimeError("%s refused: %s" % (method, reply.get("error")))
        return reply["result"]

    def world_id(self):
        return ((self.welcome.get("server") or {}).get("world") or {}).get("id")

    def logic(self, reference_id, logic_type):
        # One id with a string logic answers the one-value form; the list asks for the rows form the cases read.
        return self.call("read", what="logic", id=str(reference_id), logic=[logic_type])["results"][0]["value"]

    def set_logic(self, reference_id, logic_type, value, allow=None):
        """Writes one value in the world: the direct call acts (confirm), so the case sees it happen."""
        params = {"what": "logic", "id": str(reference_id), "logic": logic_type, "value": value, "confirm": True}
        if allow is not None:
            params["allow"] = allow
        return self.call("write", **params)

    def watch_logic(self, reference_id, logic_type, seconds, every=0.1):
        """Samples a value for a while: [(t, value)], for 'what was the last write' questions."""
        samples = []
        start = time.time()
        while time.time() - start < seconds:
            samples.append((round(time.time() - start, 2), self.logic(reference_id, logic_type)))
            time.sleep(every)
        return samples

    def close(self):
        try:
            self.conn.send({"type": "bye"})
        except OSError:
            pass
        finally:
            self.conn.close()
