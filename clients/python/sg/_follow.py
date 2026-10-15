"""sg.follow: a find the game keeps current (protocol.md, Subscriptions: the find topic)."""
import copy
import threading
import time
from collections import deque

from . import _session
from ._errors import Error, Stopped, _PassEnd

# The verbs a subscription can follow: find alone today.
FOLLOWED_VERBS = frozenset({"find"})

_WAIT_S = 1.0


def follow(verb, what, interval_s=10, **options):
    """A find the game keeps current: the whole list once, then only what changed, pushed by the game.

    follow("find", "frame") subscribes to the find topic with the find's own options (no limit, offset, cursor or
    order: the subscription holds the whole list; no fields or omit) and returns a Following once the first list has
    arrived: .result is the reply as a find call's would be with every list whole, kept current as updates arrive;
    .as_of_s the game time of the reading; .next(timeout) waits for the next change and returns it ({lists, rest} as
    the event carries them), None when the timeout passes first; .live and .end_reason say whether the game still pushes
    it; .close() unsubscribes (and so does leaving a with block). interval_s (1 to 3600, 10 by default) is how often
    the game reads its live facts again; changes to the world it follows (things built or removed, rooms, damage) come
    sooner. Raises sg.Error when the hub or the game refuses it (subscription_limit, invalid_argument), and too_old when
    the hub lists no subscriptions."""
    if verb not in FOLLOWED_VERBS:
        raise Error("invalid_argument", f"sg.follow follows a find; '{verb}' is not one")
    welcome = _session.current_transport().welcome or {}
    if "subscriptions" not in (welcome.get("features") or []):
        raise Error("too_old", "this hub lists no subscriptions, so nothing can be followed; call the find instead")
    following = Following(dict(options, topic="find", what=what, interval_s=float(interval_s)))
    following._start()
    return following


class Following:
    """One followed find (sg.follow): its subscribe call, the list as last pushed, and the changes in order.

    Events arrive on the transport's reader thread, so the state they change is under a lock."""

    def __init__(self, params):
        self._params = params
        self._lock = threading.Lock()
        self._arrived = threading.Condition(self._lock)
        self._transport = None
        self._id = None
        self._result = {}
        self._keyed = {}
        self._as_of_s = None
        self._seq = 0
        self._live = False
        self._end_reason = None
        self._changes = deque()
        self._unnamed = []

    @property
    def result(self):
        """The followed reply now, every list whole: a copy the caller may keep."""
        with self._lock:
            return copy.deepcopy(self._result)

    @property
    def as_of_s(self):
        """The game time the reading behind result began."""
        with self._lock:
            return self._as_of_s

    @property
    def live(self):
        """True while the game still pushes changes on a live connection."""
        with self._lock:
            return self._live and not self._transport.closed

    @property
    def end_reason(self):
        """Why it ended (closed, world_changed, limit, read_failed), None while it is live."""
        with self._lock:
            return self._end_reason

    def next(self, timeout):
        """The next change ({lists, rest} as the game sent it, after result took it in), or None when timeout seconds
        pass first or the following ended. Inside a run it waits as sg.wait does, a second at a time (the hub's wait,
        so a stop ends it); a program waits on the change itself."""
        started = time.monotonic()
        in_run = _session.current_run() is not None
        while True:
            with self._lock:
                if self._changes:
                    return self._changes.popleft()
                left = timeout - (time.monotonic() - started)
                if left <= 0 or not self._live:
                    return None
                if not in_run:
                    self._arrived.wait(left)
                    continue
            answer = _session.request("wait", {"s": min(_WAIT_S, left), "kind": "watch",
                                               "in_loop": _session.in_loop()})
            if isinstance(answer, dict) and answer.get("instant"):
                return None

    def close(self):
        """Unsubscribes while it is live and stops listening; a hub that has already forgotten it answers
        unknown_subscription, which is ignored."""
        if self._transport is None:
            return
        self._transport.off_event(self._on_event)
        with self._lock:
            live, subscription = self._live, self._id
            self._live = False
            if self._end_reason is None:
                self._end_reason = "closed"
        if live and subscription is not None:
            try:
                _session.request("unsubscribe", {"subscription": subscription}, line=False)
            except Error:
                pass

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, traceback):
        self.close()
        return False

    def _start(self):
        transport = _session.current_transport()
        transport.on_event(self._on_event)
        try:
            reply = _session.request("subscribe", self._params, line=False)
        except (Error, Stopped, _PassEnd):
            transport.off_event(self._on_event)
            raise
        with self._lock:
            if not isinstance(reply, dict) or reply.get("subscription") is None:
                transport.off_event(self._on_event)
                raise Error("protocol_error", "the hub answered subscribe without a subscription id")
            self._transport = transport
            self._id = reply["subscription"]
            self._result = reply.get("result") or {}
            self._keyed = dict(reply.get("keyed") or {})
            self._as_of_s = reply.get("as_of_s")
            self._live = True
            unnamed, self._unnamed = self._unnamed, []
            for event in unnamed:
                self._apply(event)

    def _on_event(self, event):
        kind = event.get("event")
        with self._lock:
            if kind == "world_changed":
                self._ended("world_changed")
            elif kind in ("find_update", "subscription_ended"):
                if self._id is None:
                    self._unnamed.append(event)
                else:
                    self._apply(event)

    def _apply(self, event):
        """Under the lock: an event of this subscription changes result or ends it."""
        if event.get("subscription") != self._id:
            return
        if event.get("event") == "subscription_ended":
            self._ended(event.get("reason") or "subscription_ended")
            return
        self._seq = event.get("seq", self._seq)
        self._as_of_s = event.get("as_of_s", self._as_of_s)
        self._result, self._keyed = apply_update(self._result, self._keyed, event)
        self._changes.append({"lists": event.get("lists") or {}, "rest": event.get("rest"),
                              "whole": bool(event.get("whole"))})
        self._arrived.notify_all()

    def _ended(self, reason):
        self._live = False
        if self._end_reason is None:
            self._end_reason = reason
        self._arrived.notify_all()


def apply_update(result, keyed, event):
    """(result, keyed) after a find_update: a whole one replaces both; otherwise rest (when sent) replaces every key
    that is not a keyed list, and each keyed list drops its removed keys, replaces its changed entries in place and
    appends its added ones. keyed maps each keyed list to the field its entries are keyed by."""
    after_keyed = dict(event.get("keyed") or {})
    if event.get("whole"):
        return copy.deepcopy(event.get("result") or {}), after_keyed
    state = dict(result)
    rest = event.get("rest")
    if rest is not None:
        state = {name: value for name, value in state.items() if name in keyed and name in after_keyed}
        state.update(copy.deepcopy(rest))
    else:
        for name in list(state):
            if name in keyed and name not in after_keyed:
                del state[name]
    for name, change in (event.get("lists") or {}).items():
        field = after_keyed.get(name) or keyed.get(name) or "reference_id"
        entries = state.get(name) if name in keyed and isinstance(state.get(name), list) else []
        removed = {str(key) for key in change.get("removed") or []}
        changed = {_key(entry, field): entry for entry in change.get("changed") or []}
        kept = []
        for entry in entries:
            key = _key(entry, field)
            if key in removed:
                continue
            kept.append(copy.deepcopy(changed.pop(key, entry)))
        kept.extend(copy.deepcopy(change.get("added") or []))
        state[name] = kept
    for name in after_keyed:
        state.setdefault(name, [])
    return state, after_keyed


def _key(entry, field):
    value = entry.get(field) if isinstance(entry, dict) else None
    return str(value) if value is not None else None
