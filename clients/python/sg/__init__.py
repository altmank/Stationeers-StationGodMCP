"""sg: StationGod's verbs from Python, through the hub.

In a run_script run, `sg` is already connected to the hub that runs the code. A program connects itself:

    import sg
    sg.connect()                      # starts a child hub (StationGodMCP.Server --hub) for this program
    for thing in sg.find('thing', prefab='*Battery*', near='player', radius=30)['things']:
        print(thing['reference_id'])

sg never talks to the mod directly: every call is the hub protocol over the hub's stdin and stdout
(docs/architecture/hub.md). sg.help(verb, what) gives any what's options and reply.
"""
from . import _session, _transport
from ._errors import Error, Stopped
from ._helpers import help, hold, ref, wait, watch
from ._replies import MainList, Reply, Row
from ._transport import VERSION as __version__
from ._verbs import (build, check, find, inspect, move, plan, read, remove, server, show, survey, swap, trade, undo,
                     write)

__all__ = [
    "Error", "Stopped", "Reply", "MainList", "Row", "connect", "close",
    "find", "inspect", "survey", "read", "write", "plan", "check", "build", "swap", "remove", "move", "trade", "show",
    "undo", "server", "help", "ref", "wait", "watch", "hold",
]


def connect(hub_path=None, *hub_args, client="sg-py"):
    """Starts a child hub and makes it the one sg calls go to; returns its transport (`.welcome`, `.close()`).

    The hub is hub_path, else the STATIONGODMCP_HUB environment variable, else StationGodMCP.Server on PATH; hub_args
    are passed to it after --hub. The child hub ends when this program does."""
    transport = _transport.connect(hub_path, *hub_args, client=client)
    _session.use(transport)
    return transport


def close():
    """Closes the connection sg.connect() opened (the child hub then ends)."""
    transport = _session._transport
    _session.use(None)
    if transport is not None:
        transport.close()
