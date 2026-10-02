"""Subscriptions in the library (clients.md, Subscriptions in the library): the request is kept, so after a
reconnect into the same world the client subscribes again with it; into another world, or when the mod ends it with
world_changed, the subscription closes and the caller hears of the world change. Polling instead is the caller's
choice: subscribe raises SubscriptionRefused (subscription_limit) or TooOld (no subscriptions on this server)."""
import logging
import threading

log = logging.getLogger("stationgod")


class Subscription:
    """One device subscription. state is the last read_devices result received (the first reading from the subscribe
    reply, then each update's), with seq and frame beside it."""

    def __init__(self, client, request):
        self._client = client
        self.request = dict(request)
        self.id = None              # the server's id on the current connection, None while not subscribed
        self.state = None
        self.seq = 0
        self.frame = None
        self.game_time_s = None
        self.interval_s = None
        self.closed = False
        self.end_reason = None      # why it closed: closed, world_changed, revoked, limit, or a refusal on resubscribe
        self._updates = 0
        self._condition = threading.Condition()
        self._callbacks = []

    def wait(self, timeout=None):
        """Blocks until the next update (or a fresh first reading after a resubscribe); True if one came, False on
        timeout or when the subscription closed."""
        with self._condition:
            seen = self._updates
            self._condition.wait_for(lambda: self._updates != seen or self.closed, timeout)
            return self._updates != seen

    def on_update(self, callback):
        """callback(state) on the library's reader thread after each update; one that raises is logged."""
        self._callbacks.append(callback)
        return callback

    def close(self):
        self._client._close_subscription(self)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    # ---- called by the client ----

    def _first(self, reply):
        with self._condition:
            self.id = reply.get("subscription")
            self.interval_s = reply.get("interval_s")
            self.frame = reply.get("frame")
            self.state = reply.get("result")
            self.seq = 0
            self._updates += 1
            self._condition.notify_all()

    def _update(self, event):
        with self._condition:
            self.state = event.get("result")
            self.seq = event.get("seq", self.seq + 1)
            self.frame = event.get("frame")
            self.game_time_s = event.get("game_time_s")
            self._updates += 1
            self._condition.notify_all()
            state = self.state
        for callback in list(self._callbacks):
            try:
                callback(state)
            except Exception:
                log.exception("stationgod: a subscription callback raised")

    def _detach(self):
        self.id = None

    def _end(self, reason):
        with self._condition:
            if self.closed:
                return
            self.closed = True
            self.end_reason = reason
            self.id = None
            self._condition.notify_all()
