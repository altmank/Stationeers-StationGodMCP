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
from . import lib
from ._errors import Error, Stopped
from ._helpers import changes, help, hold, jobs, ref, script, wait, watch
from ._replies import MainList, Reply, Row
from ._transport import VERSION as __version__
from ._verbs import (build, check, find, inspect, move, plan, read, remove, server, show, survey, swap, trade, undo,
                     write)

__all__ = [
    "Error", "Stopped", "Reply", "MainList", "Row", "connect", "close",
    "find", "inspect", "survey", "read", "write", "plan", "check", "build", "swap", "remove", "move", "trade", "show",
    "undo", "server", "help", "ref", "wait", "watch", "changes", "jobs", "hold", "script",
]


def connect(hub_path=None, *hub_args, client="sg-py"):
    """Starts a child hub and makes it the one sg calls go to; returns its transport (`.welcome`, `.close()`).

    The hub is hub_path, else the STATIONGODMCP_HUB environment variable, else StationGodMCP.Server on PATH; hub_args
    are passed to it after --hub. The child hub ends when this program does. A second sg.connect() closes the
    first connection (and its child hub) once the new one is up. Inside a run it raises sg.Error: the run's
    connection belongs to the hub."""
    _refuse_inside_a_run("sg.connect")
    transport = _transport.connect(hub_path, *hub_args, client=client)
    old = _session._transport
    if old is not None and old is not transport:
        old.close()
    _session.use(transport)
    return transport


def close():
    """Closes the connection sg.connect() opened (the child hub then ends). Inside a run it raises sg.Error."""
    _refuse_inside_a_run("sg.close")
    transport = _session._transport
    _session.use(None)
    if transport is not None:
        transport.close()


def _refuse_inside_a_run(name):
    if _session.current_run() is not None:
        raise Error("invalid_argument", f"{name}() inside a run: a run's connection belongs to the hub",
                    "leave the connection to the run; sg is already connected")
