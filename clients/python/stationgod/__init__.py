"""StationGod's Python client library: one connection to the StationGodMCP mod over its named pipe or TCP, protocol
version 2 with the fall back to version 1, safe resending, subscriptions and client-side output files.

    import stationgod
    game = stationgod.connect()                          # the local pipe StationGodMCP
    clock = game.call("game_clock")
    health = game.thing_health(reference_ids=["364"], fields=["reference_id", "damage_ratio"])

See docs/architecture/clients.md in the StationGodMCP repository.
"""
from .client import Client, PendingCall
from .connection import LIBRARY_VERSION as __version__
from .connection import key_variable, proof
from .errors import (CheatNotArmed, GameError, InvalidArgument, MethodNotFound, NotFound, PermissionDenied,
                     StationGodError, SubscriptionRefused, TooOld, Unauthorized, Unreachable, WorldChanged)
from .subscriptions import Subscription


def connect(pipe="StationGodMCP", host=None, port=8765, client=None, key_env=None, protocol="auto", **options):
    """Connects now and returns the Client. pipe names the local pipe; host and port choose TCP instead. client is
    the key's name; the key is read from the environment variable key_env (default STATIONGOD_KEY_<PIPE> or
    STATIONGOD_KEY_<HOST>_<PORT>). protocol "v1" speaks today's protocol from the start. Other options:
    secret_env (the legacy TCP secret's variable), connect_timeout, output_dir, check_arguments."""
    return Client(pipe=pipe, host=host, port=port, client=client, key_env=key_env, protocol=protocol,
                  **options).open()


__all__ = ["connect", "Client", "PendingCall", "Subscription", "StationGodError", "GameError", "PermissionDenied",
           "CheatNotArmed", "InvalidArgument", "NotFound", "SubscriptionRefused", "Unauthorized", "MethodNotFound",
           "Unreachable", "WorldChanged", "TooOld", "key_variable", "proof", "__version__"]
