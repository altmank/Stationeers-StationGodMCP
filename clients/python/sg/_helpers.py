"""The sg helpers that are not verbs: wait, watch, changes, hold, ref and help (hub.md, *The hub protocol* and *Runs*)."""
import threading
import time
from collections import deque
from collections.abc import Mapping

from . import _session
from ._catalogue import VERBS
from ._errors import Error, Stopped, _PassEnd
from ._replies import Reply, wrap
from ._verbs import read as _read

LIGHT_POLL_S = 1.0
HEAVY_POLL_S = 5.0

# The mod's SamplingInterval.MinimumSeconds: one game tick at 2 Hz. `every` clamps to it (T-B-7; the help says so).
MIN_EVERY_S = 0.5

# The keys read logic's reads entries take (catalogue/verbs/read.json): a watch whose every entry is a mapping of
# those alone is the form the devices subscription covers.
_READ_ENTRY_KEYS = frozenset({"id", "logic", "slots", "atmosphere", "reagents"})


def wait(s):
    """Waits s seconds and returns None. Inside a run, time.sleep is this function. After a stop the hub answers at
    once (the run is unwinding), so a wait in a finally block never hangs."""
    _session.request("wait", {"s": float(s), "kind": "wait", "in_loop": _session.in_loop()})
    return None


def watch(reads, until, timeout):
    """Reads logic values until until(rows) is true or timeout seconds pass; returns (rows, met).

    rows are the last reading's rows, one per reads entry, in order. It never raises at timeout. A hub whose welcome
    lists subscriptions, and reads whose every entry is a devices-form read, subscribe to the devices topic instead of
    polling: the subscribe reply's reading is the first rows, each update event replaces them, and the watch makes no
    read logic call after that. Anything else polls read logic, as it always has. Both sample every second, or every
    5 s when an entry asks for atmosphere (a heavy read). In a report run the hub answers the watch's wait with
    {"instant": true} and the watch returns its current rows as met. A stop the run has already heard of ends the
    watch without another wait: it reads once (it does not subscribe then) and returns. A stop that arrives on the
    watch's own wait raises sg.Stopped as it always has, and a subscription is ended first. A subscription the hub
    refuses (a limit), ends (subscription_ended) or drops with a world change falls back to polling for the rest of
    the timeout. The watch unsubscribes before it returns."""
    interval = HEAVY_POLL_S if any(isinstance(entry, dict) and entry.get("atmosphere") for entry in reads) \
        else LIGHT_POLL_S
    started = time.monotonic()
    subscription = None if _stopped() else _subscribe(reads, interval)
    try:
        rows = subscription.rows() if subscription is not None else _watch_rows(reads)
        while True:
            if until(rows):
                return rows, True
            if _stopped():
                return rows, False
            left = timeout - (time.monotonic() - started)
            if left <= 0:
                return rows, False
            answer = _session.request("wait", {"s": min(interval, left), "kind": "watch",
                                               "in_loop": _session.in_loop()})
            if isinstance(answer, dict) and answer.get("instant"):
                return rows, True
            rows = subscription.rows() if subscription is not None and subscription.live else _watch_rows(reads)
    finally:
        if subscription is not None:
            subscription.end()


def changes(reads, timeout, every=1):
    """Yields the read rows each time a value changes, until timeout seconds pass or the run stops.

    every (seconds) is the sampling window: the devices subscription's interval_s, never under 0.5 s (one game tick),
    and the default 1 s is the rate polling uses. Each yield is the whole reading, one row per reads entry, in order,
    and a reading equal to the last yielded is skipped. A hub whose welcome lists subscriptions, and reads whose every
    entry is a devices-form read, subscribe to the devices topic: every update the mod pushes (it samples on game ticks
    and sends one when a value other than the clock changes) is yielded in order, with no read logic call after the
    first. Anything else polls read logic every `every` seconds and yields only a reading that differs from the last.
    In a report run the hub answers the first wait with {"instant": true}: the current rows are yielded once and the
    generator ends. It never raises at the timeout; a stop ends it, and a stop that arrives on its own wait raises
    sg.Stopped as any wait does. A subscription the hub refuses (a limit), ends, or drops with a world change falls
    back to polling for the rest of the timeout, and one still live when the generator ends is unsubscribed."""
    interval = max(float(every), MIN_EVERY_S)
    started = time.monotonic()
    subscription = None if _stopped() else _subscribe(reads, interval, queue_events=True)
    try:
        if _stopped():
            return
        rows = subscription.rows() if subscription is not None else _watch_rows(reads)
        while True:
            left = timeout - (time.monotonic() - started)
            if left <= 0 or _stopped():
                return
            answer = _session.request("wait", {"s": min(interval, left), "kind": "watch",
                                               "in_loop": _session.in_loop()})
            if isinstance(answer, dict) and answer.get("instant"):
                yield rows
                return
            while True:
                fresh = _next_reading(subscription, reads, rows)
                if fresh is None:
                    break
                rows = fresh
                yield rows
    finally:
        if subscription is not None:
            subscription.end()


def _next_reading(subscription, reads, last):
    """The next reading that differs from last, or None when there is none yet: the subscription's oldest unyielded
    update, or one read where the subscription is not live (while it is, a subscribed run makes no read logic call)."""
    if subscription is not None:
        fresh = subscription.take()
        while fresh is not None and fresh == last:
            fresh = subscription.take()
        if fresh is not None:
            return fresh
        if subscription.live:
            return None
    rows = _watch_rows(reads)
    return rows if rows != last else None


def jobs(timeout):
    """Yields one item per job the run started that ends, until timeout seconds pass or the run stops.

    Each item is the mod's job object as its job_ended event carries it: {job_id, tool, status, error, created_ids,
    removed, changed, placed}, the error and the lists only when the job's final reply had them. The run hears its own
    jobs alone (the hub drops a job_ended event for a job it did not start); a session with no run keeps the whole
    view. The jobs topic is a subscription, so this needs a hub whose welcome lists subscriptions; where it does not,
    or the hub refuses the subscribe, the iterator ends without yielding (no read can see a job end). It never raises
    at the timeout; a stop ends it, and a stop that arrives on its own wait raises sg.Stopped as any wait does. In a
    report run the hub answers the first wait with {"instant": true} and it ends without yielding. It waits a second
    at a time at the most, so an ended job is yielded within about a second of the mod reporting it; a stream_end
    drops the subscription with the stream and an ended subscription ends the iterator, since nothing more can
    arrive."""
    started = time.monotonic()
    if _stopped() or not _subscriptions_offered():
        return
    subscription = _JobsSubscription()
    if not subscription.start():
        return
    try:
        while True:
            left = timeout - (time.monotonic() - started)
            if left <= 0 or _stopped():
                return
            job = subscription.take()
            if job is None:
                if not subscription.live:
                    return
                answer = _session.request("wait", {"s": min(LIGHT_POLL_S, left), "kind": "watch",
                                                   "in_loop": _session.in_loop()})
                if isinstance(answer, dict) and answer.get("instant"):
                    return
                continue
            yield job
    finally:
        subscription.end()


def _watch_rows(reads):
    reply = _read("logic", reads=reads)
    return list(reply.get("results") or [])


def _rows_of(reading):
    """A reading's rows, in reads order; empty when the reading does not carry results."""
    return list(reading.get("results") or []) if isinstance(reading, Mapping) else []


def _stopped():
    """Whether the run's stop has already been delivered: the watch then takes one reading and returns, subscribing to
    nothing, because waits no longer wait and polling would spin."""
    run = _session.current_run()
    return run is not None and run.stopped


def _subscriptions_offered():
    """Whether the hub's welcome lists subscriptions: the devices and jobs topics both need it."""
    welcome = _session.current_transport().welcome or {}
    return "subscriptions" in (welcome.get("features") or [])


def _subscribe(reads, interval, queue_events=False):
    """A started devices subscription for the watch (or the changes iterator, queue_events), or None when it polls:
    the hub does not advertise subscriptions, a reads entry is not the devices form, or the hub refused the subscribe
    (a subscription limit, or too many of them), which is the mod's own way of saying to poll instead."""
    if not _subscriptions_offered():
        return None
    if not all(_devices_form(entry) for entry in reads):
        return None
    subscription = _DevicesSubscription(reads, interval, queue_events)
    return subscription if subscription.start() else None


def _devices_form(entry):
    """Whether one read logic reads entry is the form the devices topic covers: a mapping whose keys are read logic's
    entry keys alone (id, logic, slots, atmosphere, reagents)."""
    return isinstance(entry, Mapping) and bool(entry) and not set(entry) - _READ_ENTRY_KEYS


class _DevicesSubscription:
    """One watch's devices subscription: the subscribe call, its latest reading, and the events that change it.

    The handler runs on the transport's reader thread, so the reading it keeps, the queue it fills and the flags it
    sets are under a lock. `live` goes false when the subscription ends or a world change drops it, and the watch then
    polls for the rest of its timeout. A changes iterator asks for the queue: one entry per update event, taken in
    order; a watch queues nothing and keeps only the latest reading."""

    def __init__(self, reads, interval, queue_events=False):
        self._reads = reads
        self._interval = interval
        self._lock = threading.Lock()
        self._transport = None
        self._id = None
        self._reading = None
        self._live = False
        self._pending = deque() if queue_events else None

    @property
    def live(self):
        with self._lock:
            return self._live

    def start(self):
        """Subscribes; False when the hub refused it, which leaves nothing open and the watch polling."""
        transport = _session.current_transport()
        transport.on_event(self._on_event)
        try:
            reply = _session.request("subscribe", {"reads": self._reads, "interval_s": self._interval},
                                     line=False)
        except (Stopped, _PassEnd):
            transport.off_event(self._on_event)
            raise
        except Error:
            transport.off_event(self._on_event)
            return False
        with self._lock:
            self._id = reply.get("subscription") if isinstance(reply, dict) else None
            self._reading = reply.get("result") if isinstance(reply, dict) else None
            self._live = self._id is not None
        if not self._live:
            transport.off_event(self._on_event)
            return False
        self._transport = transport
        return True

    def rows(self):
        """The latest reading's rows, in reads order; empty before a reading arrives."""
        with self._lock:
            reading = self._reading
        return _rows_of(reading)

    def take(self):
        """The oldest update's rows not yet handed out, or None when none is queued (a watch queues none)."""
        if self._pending is None:
            return None
        with self._lock:
            return self._pending.popleft() if self._pending else None

    def end(self):
        """Unsubscribes when a live subscription is still known and stops listening for its events. A hub that has
        already forgotten it (a stream end, a world change) refuses unknown_subscription; nothing the watch can act on
        either way, since the subscription is gone with what ended it."""
        if self._transport is None:
            return
        self._transport.off_event(self._on_event)
        with self._lock:
            live, subscription = self._live, self._id
        if not live or subscription is None:
            return
        try:
            _session.request("unsubscribe", {"subscription": subscription}, line=False)
        except Error:
            pass

    def _on_event(self, event):
        """On the reader thread: keeps the latest update's reading and follows the events that end a subscription.
        An event is taken when it names this subscription, or when the reply has not named it yet (the hub replies
        before the subscription's first update, and a session's only open subscription is this one)."""
        kind = event.get("event")
        with self._lock:
            if kind == "world_changed":
                self._live = False
                return
            if self._id is not None and event.get("subscription") != self._id:
                return
            if kind == "update":
                self._reading = event.get("result") or self._reading
                if self._pending is not None and isinstance(event.get("result"), Mapping):
                    self._pending.append(_rows_of(event.get("result")))
            elif kind == "subscription_ended":
                self._live = False


class _JobsSubscription:
    """One sg.jobs subscription: the jobs-topic subscribe call, the jobs the mod reports as ended, and its end.

    The handler runs on the transport's reader thread, so the queue it fills and the flags it sets are under a lock.
    `live` goes false when the subscription ends or a world change drops it; there is no fallback to polling, because
    no read can see a job end."""

    def __init__(self):
        self._lock = threading.Lock()
        self._transport = None
        self._id = None
        self._pending = deque()
        self._live = False

    @property
    def live(self):
        with self._lock:
            return self._live

    def start(self):
        """Subscribes to the jobs topic; False when the hub refused it, which leaves nothing open and the iterator
        ending without a yield."""
        transport = _session.current_transport()
        transport.on_event(self._on_event)
        try:
            reply = _session.request("subscribe", {"topic": "jobs"}, line=False)
        except (Stopped, _PassEnd):
            transport.off_event(self._on_event)
            raise
        except Error:
            transport.off_event(self._on_event)
            return False
        with self._lock:
            self._id = reply.get("subscription") if isinstance(reply, dict) else None
            self._live = self._id is not None
        if not self._live:
            transport.off_event(self._on_event)
            return False
        self._transport = transport
        return True

    def take(self):
        """The oldest job the mod reported as ended and the iterator has not yielded, or None."""
        with self._lock:
            return self._pending.popleft() if self._pending else None

    def end(self):
        """Unsubscribes when a live subscription is still known and stops listening for its events. A hub that has
        already forgotten it (a stream end, a world change) refuses unknown_subscription; nothing the iterator can act
        on either way, since the subscription is gone with what ended it."""
        if self._transport is None:
            return
        self._transport.off_event(self._on_event)
        with self._lock:
            live, subscription = self._live, self._id
        if not live or subscription is None:
            return
        try:
            _session.request("unsubscribe", {"subscription": subscription}, line=False)
        except Error:
            pass

    def _on_event(self, event):
        """On the reader thread: queues each job_ended job, in the mod's order, and follows the events that end a
        subscription. An event is taken when it names this subscription, or when the reply has not named it yet."""
        kind = event.get("event")
        with self._lock:
            if kind == "world_changed":
                self._live = False
                return
            if self._id is not None and event.get("subscription") != self._id:
                return
            if kind == "job_ended" and isinstance(event.get("job"), Mapping):
                self._pending.append(event["job"])
            elif kind == "subscription_ended":
                self._live = False


def hold_ids(ids):
    """ids as the list hold sends: one id (str or int) is a list of one; a dict (a reply row) or bytes is refused,
    since neither is an id; any other iterable (a list, a tuple, a set) is a list of ids."""
    if isinstance(ids, (str, int)) and not isinstance(ids, bool):
        return [ids]
    if isinstance(ids, (bytes, bytearray)):
        raise TypeError("sg.hold ids: an id or a list of ids, not bytes")
    if isinstance(ids, Mapping):
        raise TypeError("sg.hold ids: an id or a list of ids, not a dict (pass row['reference_id'])")
    return list(ids)


class hold:
    """with sg.hold(ids, logic, value=None, after=None): writes value now and restores after on exit (normal,
    exception or stop). after left out: the hub reads the current values first. The hub registers the restore with
    the mod before it writes, so it is also applied if the session ends abruptly.

    The context value is the hold itself: `.after` (the values to restore, from the hub) and, after the block,
    `.rows` (the restore's rows)."""

    def __init__(self, ids, logic, value=None, after=None):
        self.ids = hold_ids(ids)
        self.logic = logic
        self.value = value
        self.after = after
        self.rows = None

    def _params(self, phase, after):
        params = {"phase": phase, "ids": self.ids, "logic": self.logic}
        if self.value is not None:
            params["value"] = self.value
        if after is not None:
            params["after"] = after
        return params

    def __enter__(self):
        result = _session.request("hold", self._params("enter", self.after)) or {}
        self.after = result.get("after", self.after)
        return self

    def __exit__(self, exc_type, exc, traceback):
        try:
            result = self._exit()
        except Error:
            if exc is not None:
                return False  # the block's own exception matters more; the hub reports the refused restore
            raise
        self.rows = result.get("rows")
        return False

    def _exit(self):
        try:
            return _session.request("hold", self._params("exit", self.after)) or {}
        except (Stopped, _PassEnd) as stop:
            # The stop arrived on this exit call, so the restore may not have acted. After a stop the hub lets hold
            # exits act (as CLEANUP), so send it once more, then let the stop go on up. If that is refused too (after a
            # world change no cleanup acts), the stop is still what ended the run.
            try:
                result = _session.request("hold", self._params("exit", self.after)) or {}
                self.rows = result.get("rows")
            except Error:
                pass
            raise stop


def ref(id):
    """A ref's content: for rN {ref, ids, reply}; for script-N {ref, returned, printed}."""
    return wrap(_session.request("ref", {"id": id}))


def help(verb=None, what=None):
    """StationGod's help for scripts, as text: sg.help() lists verbs and topics; sg.help('find') a verb's whats;
    sg.help('find', 'thing') one what's Python stub; sg.help('scripts') a topic; sg.help('types', 'BuildJob') a
    subtopic."""
    args = {"lang": "python"}
    if verb is not None and verb not in VERBS:
        args["topic"] = verb
        if what is not None:
            args["subtopic"] = what
    else:
        if verb is not None:
            args["verb"] = verb
        if what is not None:
            args["what"] = what
    result = _session.request("verb", {"verb": "help", "args": args},
                              call={"verb": "help", "what": None, "args": args})
    if isinstance(result, dict) and isinstance(result.get("text"), str):
        return result["text"]
    return Reply(result) if isinstance(result, dict) else result
