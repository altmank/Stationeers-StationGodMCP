"""The client: calls over one kept connection, several in flight, reconnecting, resending only what is safe, and
subscriptions kept across reconnects into the same world (clients.md).

The resend rule, from today's dashboard transport (StationeersScriptDashboard stationscript/transport.py) with the
catalogue deciding what is a read:
- a call whose line was never written is sent again on the next connection;
- a call that was written and not answered is sent again (once) only if its effective class is read and it has no
  x-effects, so highlight and show_preview, which draw, are never sent twice;
- nothing is sent again when the new welcome shows another world.id;
- any other call fails with Unreachable(maybe_ran=True).
An error reply is never resent, whatever its code.
"""
import itertools
import logging
import threading
import time

from . import _methods, output
from .catalogue import Catalogue, extract
from .connection import PipeTarget, TcpTarget, key_variable, open_connection, read_key
from .errors import InvalidArgument, StationGodError, TooOld, Unreachable, WorldChanged, game_error
from .subscriptions import Subscription

log = logging.getLogger("stationgod")

DEFAULT_PIPE = "StationGodMCP"
DEFAULT_PORT = 8765
DEFAULT_DEADLINE_MS = 30000
REPLY_GRACE_S = 5.0
BACKOFF_FIRST_S = 0.1
BACKOFF_MAX_S = 5.0
OPTIONS = ("fields", "output_file", "max_bytes", "deadline_ms", "shape")


class _Call:
    def __init__(self, call_id, method, params, shape, deadline_ms, output_name, expires):
        self.id = call_id
        self.method = method
        self.params = params
        self.shape = shape
        self.deadline_ms = deadline_ms
        self.output_name = output_name     # None: no file; "": a name per call; else the file name
        self.expires = expires
        self.started = time.perf_counter()
        self.state = "new"                 # new, queued, writing, written
        self.safe = False
        self.resends = 0
        self.checked = False
        self.hook = None                   # hook(connection, reply) on the reader thread, before the caller wakes
        self.done = threading.Event()
        self.reply = None
        self.size = 0
        self.error = None
        self.final = None                  # (result,) once the caller has taken it
        self.internal = False              # the library's own call (world topic, catalogue): not metered

    def finish(self, reply=None, size=0, error=None, before_wake=None):
        """Settles the call once; before_wake(call) (the metering hook) runs before the caller wakes, so a caller that
        attributes calls to whatever it is doing sees each call metered before its own next step."""
        if self.done.is_set():
            return False
        self.reply, self.size, self.error = reply, size, error
        if before_wake is not None:
            before_wake(self)
        self.done.set()
        return True


class PendingCall:
    """A call in flight, returned by call_async. result() waits for it and returns the method's result or raises."""

    def __init__(self, client, call):
        self._client = client
        self._call = call

    @property
    def id(self):
        return self._call.id

    def done(self):
        return self._call.done.is_set()

    def result(self, timeout=None):
        call = self._call
        limit = None if timeout is None else time.monotonic() + timeout
        while not call.done.is_set():
            now = time.monotonic()
            wait = call.expires - now
            if limit is not None:
                if limit <= now:
                    raise TimeoutError(f"{call.method} has not been answered yet")
                wait = min(wait, limit - now)
            if wait <= 0:
                self._client._abandon(call)
                if not call.done.is_set():
                    written = call.state == "written"
                    call.finish(error=Unreachable(
                        f"no reply to {call.method} in time: " + (
                            "the game took the call and did not answer"
                            + ("; it may have reached the game" if not call.safe else "")
                            if written else "it could not be sent (no connection to the game)"),
                        maybe_ran=written and not call.safe), before_wake=self._client._meter)
                break
            call.done.wait(min(wait, 0.5))
        return self._client._outcome(call)

    def cancel(self):
        """Stops waiting; if the call has not started in the mod, the mod drops it (protocol cancel)."""
        self._client._abandon(self._call)
        self._call.finish(error=Unreachable(f"{self._call.method} was cancelled by the caller",
                                            maybe_ran=self._call.state == "written" and not self._call.safe))


class Client(_methods.Methods):
    """A connection to StationGod. Safe to use from several threads."""

    def __init__(self, pipe=DEFAULT_PIPE, host=None, port=DEFAULT_PORT, client=None, key_env=None, protocol="auto",
                 secret_env="STATIONGODMCP_SECRET", connect_timeout=None, output_dir=None, check_arguments=True):
        if protocol not in ("auto", "v1"):
            raise ValueError("protocol must be 'auto' or 'v1'")
        self._target = TcpTarget(host, port) if host else PipeTarget(pipe)
        self.client_name = client
        self.key_env = key_env or (key_variable(host=host, port=port) if host else key_variable(pipe_name=pipe))
        self._protocol_option = protocol
        self._secret_env = secret_env
        self._connect_timeout = connect_timeout if connect_timeout is not None else (3.0 if host else 1.0)
        self._output_dir = output_dir
        self.check_arguments = check_arguments
        self._lock = threading.RLock()
        self._slots = threading.Condition(self._lock)
        self._connect_lock = threading.Lock()
        self._conn = None
        self._ids = itertools.count(1)
        self._builtin = Catalogue(_methods.TABLE, _methods.CATALOGUE_HASH)
        self._catalogues = {_methods.CATALOGUE_HASH: self._builtin}
        self.catalogue = self._builtin
        self._requeued = []
        self._subscriptions = []
        self._by_server_id = {}
        self._world_subscription = None
        self._last_world_id = None
        self._world_callbacks = []
        self._reconnecting = False
        self._closed = False
        self._welcome = None
        self._version = None
        self.world = None
        self.game_state = None
        self.cheat = None
        self.on_call = None     # on_call(method, ms, reply_bytes, elapsed_ms, queue_ms): a metering hook
        self.on_event = None    # on_event(event): every event message, after the library has handled it

    # ---- properties --------------------------------------------------------------------------------------------

    @property
    def welcome(self):
        """The last welcome message (None on version 1)."""
        return self._welcome

    @property
    def protocol(self):
        """1 or 2 once connected, else None."""
        return self._version

    @property
    def level(self):
        return (self._welcome or {}).get("level")

    @property
    def features(self):
        return frozenset((self._welcome or {}).get("features") or [])

    @property
    def limits(self):
        return dict((self._welcome or {}).get("limits") or {})

    @property
    def connected(self):
        conn = self._conn
        return conn is not None and conn.alive

    def open(self):
        """Connects now (connect() does this); raises Unreachable when no game answers."""
        self._connection()
        return self

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    # ---- calls -------------------------------------------------------------------------------------------------

    def call(self, method, /, **params):
        """Calls method with params as the catalogue names them and returns its result. Options: fields (sent as
        shape.fields), limit given as a dict ({"things": 20}, sent as shape.limit; an integer limit is the method's own
        argument), max_bytes (shape.max_bytes), shape (a whole shape object), deadline_ms, and output_file (written
        here, never sent). None means omitted."""
        return self.call_async(method, **params).result()

    def call_async(self, method, /, **params):
        """Sends the call and returns a PendingCall at once (unless the in-flight limit is reached, when it waits for
        a slot); its result() gives the result."""
        return self._start(method, params)

    def _start(self, method, params, hook=None, internal=False):
        options = {name: params.pop(name) for name in OPTIONS if name in params}
        if isinstance(params.get("limit"), dict):
            options["limit"] = params.pop("limit")
        params = {name: value for name, value in params.items() if value is not None}
        shape = dict(options.get("shape") or {})
        for name in ("fields", "limit", "max_bytes"):
            if options.get(name) is not None:
                shape[name] = options[name]
        deadline_ms = options.get("deadline_ms")
        call = _Call(str(next(self._ids)), method, params, shape or None, deadline_ms, None, 0.0)
        call.expires = time.monotonic() + self._connect_timeout + self._budget_s(method, params, deadline_ms)
        call.hook, call.internal = hook, internal
        try:
            call.output_name = output.target(options.get("output_file"))
            self._submit(call)
        except StationGodError as error:
            call.finish(error=error)
        return PendingCall(self, call)

    def iterate(self, method, /, page_size=None, **params):
        """Every entry of every page of a method with x-paging, one at a time. Not a snapshot: things that appear or
        vanish between pages can be missed or seen twice (see the method's order in the catalogue)."""
        paging = self.catalogue.paging(method)
        if not paging:
            raise ValueError(f"{method} has no x-paging in the catalogue")
        if params.get("output_file"):
            raise ValueError("iterate does not take output_file")
        offset = params.pop(paging["offset"], None) or 0
        if page_size is not None:
            params[paging["limit"]] = page_size
        while True:
            page = self.call(method, **params, **{paging["offset"]: offset})
            entries = page.get(paging["list"]) or []
            yield from entries
            has_more = page.get(paging["has_more"]) if paging.get("has_more") else None
            if not entries or has_more is False:
                return
            if has_more is None and page_size is not None and len(entries) < page_size:
                return
            offset += len(entries)

    def _budget_s(self, method, params, deadline_ms):
        """How long a written call waits: its deadline plus the method's x-duration plus 5 s."""
        deadline_s = (deadline_ms if deadline_ms is not None else DEFAULT_DEADLINE_MS) / 1000.0
        return deadline_s + self.catalogue.duration_s(method, params) + REPLY_GRACE_S

    def _submit(self, call):
        while True:
            conn = self._connection()
            if conn.version == 2 and self.check_arguments and not call.checked:
                problems = self.catalogue.check(call.method, call.params)
                if problems:
                    raise InvalidArgument("invalid_argument", " ".join(p["problem"] for p in problems),
                                          {"problems": problems, "checked_by": "client"})
                call.checked = True
            call.safe = self.catalogue.resend_safe(call.method, call.params)
            with self._lock:
                while conn.alive and len(conn.pending) >= conn.max_in_flight and not call.done.is_set():
                    remaining = call.expires - time.monotonic()
                    if remaining <= 0:
                        raise Unreachable(f"{call.method} waited for a free call slot until its time ran out")
                    self._slots.wait(min(remaining, 0.5))
                if call.done.is_set():
                    return
                if not conn.alive:
                    continue
                conn.pending[call.id] = call
                call.state = "writing"
            ok = True
            try:
                conn.send(self._message(conn, call))
            except OSError:
                ok = False
            with self._lock:
                call.state = "written" if ok else "queued"
                if getattr(conn, "broken_handled", False) and conn.pending.pop(call.id, None) is call:
                    self._break_call(call)
            if not ok:
                self._ensure_reconnecting()
            return

    def _message(self, conn, call):
        if conn.version == 1:
            message = {"id": call.id, "method": call.method, "params": call.params}
            if call.shape:
                message["shape"] = call.shape  # an old mod ignores it; a new one shapes on version 1 too
            return message
        message = {"type": "call", "id": call.id, "method": call.method, "params": call.params}
        if call.shape and "shape" in conn.features:
            message["shape"] = call.shape
        if call.deadline_ms is not None:
            message["deadline_ms"] = call.deadline_ms
        return message

    def _outcome(self, call):
        if call.final is not None:
            return call.final[0]
        if call.error is not None:
            raise call.error
        reply = call.reply
        if not reply.get("ok"):
            raise game_error(reply.get("error"))
        result = reply.get("result")
        if call.output_name is not None:
            result = output.write(call.method, call.output_name, result, output.folder(self._output_dir))
        call.final = (result,)
        return result

    def _meter(self, call):
        hook = self.on_call
        if hook is None or call.internal:
            return
        reply = call.reply or {}
        try:
            hook(call.method, (time.perf_counter() - call.started) * 1000.0, call.size, reply.get("elapsed_ms"),
                 reply.get("queue_ms"))
        except Exception:
            log.exception("stationgod: the on_call hook raised")

    def _abandon(self, call):
        conn = None
        with self._lock:
            if call in self._requeued:
                self._requeued.remove(call)
            for candidate in (self._conn,):
                if candidate is not None and candidate.pending.get(call.id) is call:
                    del candidate.pending[call.id]
                    self._slots.notify_all()
                    conn = candidate
        if conn is not None and conn.version == 2 and "cancel" in conn.features and conn.alive:
            try:
                conn.send({"type": "cancel", "id": call.id})
            except OSError:
                pass

    # ---- connecting --------------------------------------------------------------------------------------------

    def _connection(self):
        conn = self._conn
        if conn is not None and conn.alive:
            return conn
        adopted = None
        with self._connect_lock:
            conn = self._conn
            if conn is None or not conn.alive:
                if self._closed:
                    raise Unreachable("the client is closed")
                conn = open_connection(
                    self._target, protocol=self._protocol_option, client=self.client_name,
                    key=read_key(self.key_env) if self.client_name else None,
                    secret=read_key(self._secret_env), connect_timeout=self._connect_timeout)
                adopted = self._adopt(conn)
        if adopted is not None:
            self._after_adopt(conn, adopted)
        return conn

    def _adopt(self, conn):
        """Starts the reader and decides what the new connection means; returns whether the world changed."""
        conn.broken_handled = False
        conn.start(self._on_message, self._on_closed)
        with self._lock:
            self._conn = conn
            self._version = conn.version
            self._welcome = conn.welcome
        if conn.version != 2:
            return False
        self._load_catalogue(conn)
        changed = self._last_world_id is not None and conn.world_id != self._last_world_id
        self._last_world_id = conn.world_id
        self.world = conn.world
        server = (conn.welcome or {}).get("server") or {}
        self.game_state = server.get("game_state", self.game_state)
        self.cheat = (conn.welcome or {}).get("cheat")
        return changed

    def _after_adopt(self, conn, world_changed):
        with self._lock:
            requeued, self._requeued = self._requeued, []
            subscriptions = [sub for sub in self._subscriptions if not sub.closed]
        if world_changed:
            for call in requeued:
                call.finish(error=WorldChanged(
                        f"the game came back with another world; {call.method} was not sent again",
                        maybe_ran=call.state == "written" and not call.safe), before_wake=self._meter)
            for sub in subscriptions:
                self._drop_subscription(sub, "world_changed")
            self._fire_world_changed(conn.world)
        else:
            for call in requeued:
                if call.done.is_set():
                    continue
                try:
                    self._submit(call)
                except StationGodError as error:
                    call.finish(error=error, before_wake=self._meter)
            for sub in subscriptions:
                try:
                    self._subscribe(sub)
                except StationGodError as error:
                    self._drop_subscription(sub, f"refused on resubscribe: {error}")
        if conn.version == 2 and "subscriptions" in conn.features:
            self._start("subscribe", {"topic": "world"}, hook=self._note_world_subscription, internal=True)

    def _note_world_subscription(self, conn, reply):
        if reply.get("ok"):
            self._world_subscription = (conn, (reply.get("result") or {}).get("subscription"))

    def _load_catalogue(self, conn):
        digest = ((conn.welcome or {}).get("catalogue") or {}).get("hash")
        if not digest:
            return
        known = self._catalogues.get(digest)
        if known is None:
            try:
                fetched = self._direct(conn, "catalogue", {}, 30.0)
                known = Catalogue(extract(fetched), digest, "server")
                self._catalogues[digest] = known
            except StationGodError as error:
                log.warning("stationgod: the server's catalogue (%s) could not be fetched, using the built-in one: %s",
                            digest, error)
                known = self._builtin
        self.catalogue = known

    def _direct(self, conn, method, params, timeout):
        """A call on this connection only, during adoption (nothing is resent)."""
        call = _Call(str(next(self._ids)), method, params, None, None, None, time.monotonic() + timeout)
        call.internal = True
        with self._lock:
            conn.pending[call.id] = call
            call.state = "writing"
        try:
            conn.send(self._message(conn, call))
        except OSError as error:
            raise Unreachable(f"the connection broke while asking for {method}") from error
        call.state = "written"
        if not call.done.wait(timeout):
            with self._lock:
                conn.pending.pop(call.id, None)
            raise Unreachable(f"no reply to {method} in {timeout:g} s")
        if call.error is not None:
            raise call.error
        if not call.reply.get("ok"):
            raise game_error(call.reply.get("error"))
        return call.reply.get("result")

    def _ensure_reconnecting(self):
        with self._lock:
            if self._reconnecting or self._closed:
                return
            self._reconnecting = True
        threading.Thread(target=self._reconnect_loop, name="stationgod-reconnect", daemon=True).start()

    def _needs_connection(self):
        with self._lock:
            self._requeued = [call for call in self._requeued if not call.done.is_set()]
            return bool(self._requeued) or any(not sub.closed for sub in self._subscriptions)

    def _reconnect_loop(self):
        """Reopens a broken connection while calls wait to be resent or subscriptions are open: at once, then with
        backoff from 100 ms doubling to 5 s."""
        delay = BACKOFF_FIRST_S
        try:
            while not self._closed and self._needs_connection():
                conn = self._conn
                if conn is not None and conn.alive:
                    with self._lock:
                        stragglers, self._requeued = self._requeued, []
                    for call in stragglers:
                        try:
                            self._submit(call)
                        except StationGodError as error:
                            call.finish(error=error, before_wake=self._meter)
                    return
                try:
                    self._connection()
                    delay = BACKOFF_FIRST_S
                except (StationGodError, OSError) as error:
                    log.debug("stationgod: reconnect failed: %s", error)
                    time.sleep(delay)
                    delay = min(delay * 2, BACKOFF_MAX_S)
        finally:
            with self._lock:
                self._reconnecting = False

    # ---- the reader thread -------------------------------------------------------------------------------------

    def _on_message(self, conn, message, size):
        kind = message.get("type")
        if conn.version == 2:
            if kind == "reply":
                self._on_reply(conn, message, size)
            elif kind == "event":
                self._on_event(conn, message)
            elif kind == "goodbye":
                conn.goodbye = message.get("reason")
                log.info("stationgod: the server said goodbye: %s", conn.goodbye)
        elif kind is None:
            self._on_reply(conn, message, size)

    def _on_reply(self, conn, message, size):
        call_id = message.get("id")
        with self._lock:
            call = conn.pending.pop(str(call_id), None) if call_id is not None else None
            if call is None and call_id is None and conn.version == 1 and len(conn.pending) == 1:
                call = conn.pending.popitem()[1]   # a version-1 error that could not read the request's id
            if call is not None:
                self._slots.notify_all()
        if call is None:
            if call_id is None and message.get("ok") is False:
                log.warning("stationgod: the server reported %s", message.get("error"))
            return
        if call.hook is not None:
            try:
                call.hook(conn, message)
            except Exception:
                log.exception("stationgod: a reply hook raised")
        call.finish(message, size, before_wake=self._meter)

    def _on_event(self, conn, message):
        name = message.get("event")
        if name == "update":
            sub = self._by_server_id.get((id(conn), message.get("subscription")))
            if sub is not None:
                sub._update(message)
        elif name == "subscription_ended":
            sub = self._by_server_id.pop((id(conn), message.get("subscription")), None)
            if sub is not None:
                self._drop_subscription(sub, message.get("reason") or "ended")
        elif name == "world_changed":
            world = message.get("world") or {}
            with self._lock:
                fresh = world.get("id") != self._last_world_id
                if fresh:
                    self._last_world_id = world.get("id")
                    self.world = world
                    subscriptions = [sub for sub in self._subscriptions if not sub.closed]
            if fresh:
                for sub in subscriptions:
                    self._drop_subscription(sub, "world_changed")
                self._fire_world_changed(world)
        elif name == "game_state":
            self.game_state = message.get("game_state", message.get("state"))
        elif name == "cheat_armed":
            self.cheat = dict(self.cheat or {}, armed=True, until_utc=message.get("until_utc"))
        elif name == "cheat_disarmed":
            self.cheat = dict(self.cheat or {}, armed=False, until_utc=None)
        hook = self.on_event
        if hook is not None and name != "ping":
            try:
                hook(message)
            except Exception:
                log.exception("stationgod: the on_event hook raised")

    def _on_closed(self, conn):
        with self._lock:
            conn.broken_handled = True
            if self._conn is conn:
                self._conn = None
            broken = [call for call in conn.pending.values() if call.state != "writing"]
            for call in broken:
                del conn.pending[call.id]
                self._break_call(call)
            self._slots.notify_all()
            for key in [key for key in self._by_server_id if key[0] == id(conn)]:
                self._by_server_id.pop(key)._detach()
        if conn.goodbye == "revoked":
            for sub in list(self._subscriptions):
                self._drop_subscription(sub, "revoked")
        if not self._closed and self._needs_connection():
            self._ensure_reconnecting()

    def _break_call(self, call):
        """A call the broken connection held (under the lock): send it again, or fail it."""
        if call.done.is_set():
            return
        if self._closed:
            call.finish(error=Unreachable("the client was closed", maybe_ran=call.state == "written" and not call.safe),
                        before_wake=self._meter)
        elif call.state != "written":
            call.state = "queued"
            self._requeued.append(call)
        elif call.safe and call.resends < 1:
            call.resends += 1
            call.state = "queued"
            self._requeued.append(call)
        else:
            reason = ("it was sent again once already" if call.safe else
                      "it may have reached the game, so it is not sent again")
            call.finish(error=Unreachable(f"the connection broke while {call.method} was in flight; {reason}",
                                          maybe_ran=not call.safe), before_wake=self._meter)

    # ---- subscriptions -----------------------------------------------------------------------------------------

    def subscribe(self, items=None, include=None, gateway_id=None, interval_s=None, topic=None):
        """Subscribes to device values (protocol.md, Subscriptions). Raises SubscriptionRefused when the mod refuses
        it (subscription_limit) and TooOld on a server without subscriptions; in both cases poll read_devices."""
        conn = self._connection()
        if conn.version != 2 or "subscriptions" not in conn.features:
            raise TooOld("this server has no subscriptions (version 1, or 'subscriptions' not in welcome.features); "
                         "poll read_devices instead")
        request = {name: value for name, value in (("topic", topic), ("items", items), ("include", include),
                                                    ("gateway_id", gateway_id), ("interval_s", interval_s))
                   if value is not None}
        sub = Subscription(self, request)
        self._subscribe(sub)
        with self._lock:
            self._subscriptions.append(sub)
        return sub

    def _subscribe(self, sub):
        def register(conn, reply):
            if reply.get("ok"):
                sub._first(reply.get("result") or {})
                with self._lock:
                    self._by_server_id[(id(conn), sub.id)] = sub

        return self._start("subscribe", dict(sub.request), hook=register).result()

    def _drop_subscription(self, sub, reason):
        with self._lock:
            if sub in self._subscriptions:
                self._subscriptions.remove(sub)
            for key, value in list(self._by_server_id.items()):
                if value is sub:
                    del self._by_server_id[key]
        sub._end(reason)

    def _close_subscription(self, sub):
        server_id = sub.id
        self._drop_subscription(sub, "closed")
        conn = self._conn
        if server_id is not None and conn is not None and conn.alive:
            try:
                self.call("unsubscribe", subscription=server_id)
            except StationGodError:
                pass

    # ---- world changes -----------------------------------------------------------------------------------------

    def on_world_changed(self, callback):
        """callback(world) when the game is in another world than before ({id, save, epoch}): every reference id
        the caller holds may name something else now."""
        self._world_callbacks.append(callback)
        return callback

    def _fire_world_changed(self, world):
        for callback in list(self._world_callbacks):
            try:
                callback(world)
            except Exception:
                log.exception("stationgod: a world-changed callback raised")

    # ---- closing -----------------------------------------------------------------------------------------------

    def close(self):
        with self._lock:
            self._closed = True
            conn, self._conn = self._conn, None
            requeued, self._requeued = self._requeued, []
            subscriptions = list(self._subscriptions)
        for sub in subscriptions:
            self._drop_subscription(sub, "closed")
        for call in requeued:
            call.finish(error=Unreachable("the client was closed", maybe_ran=False))
        if conn is not None:
            if conn.version == 2 and conn.alive:
                try:
                    conn.send({"type": "bye"})
                except OSError:
                    pass
            conn.close()
