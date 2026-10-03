"""StationGod's Python client library: one connection to the StationGodMCP mod over its named pipe or TCP, protocol
version 2, safe resending, subscriptions and client-side output files.

    import stationgod
    game = stationgod.connect()                          # the local pipe StationGodMCP
    clock = game.call("game_clock")
    health = game.thing_health(reference_ids=["364"], fields=["reference_id", "damage_ratio"])

See docs/architecture/clients.md in the StationGodMCP repository.
"""
from .client import Client, PendingCall
from .connection import LIBRARY_VERSION as __version__
from .errors import (GameError, InvalidArgument, MethodNotFound, NotFound, StationGodError, SubscriptionRefused, TooOld, Unauthorized, Unreachable, WorldChanged)
from .subscriptions import Subscription


def connect(pipe="StationGodMCP", host=None, port=8765, client=None, **options):
    """Connects now and returns the Client. pipe names the local pipe; host and port choose TCP instead (the shared
    secret is read from the environment variable secret_env, default STATIONGODMCP_SECRET). client is the name sent in
    hello. Other options: secret_env, connect_timeout, output_dir, check_arguments."""
    return Client(pipe=pipe, host=host, port=port, client=client, **options).open()


__all__ = ["connect", "Client", "PendingCall", "Subscription", "StationGodError", "GameError", "InvalidArgument",
           "NotFound", "SubscriptionRefused", "Unauthorized", "MethodNotFound", "Unreachable", "WorldChanged", "TooOld",
           "__version__"]
