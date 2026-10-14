"""Saved scripts as modules inside a run: ``from sg.lib import <name>``, and ``sg.script(<name>)`` (SPEC-NOTES 5b).

The hub gives a run the store's root and the run's world in its welcome (``run.scripts_dir``, ``run.world``), and the
worker puts them on the run; the module reads the store itself, so the hub is never asked to resolve a name mid-run
(T-B-4). A script is one file of the store: ``<root>/shared/<name>.py``, else ``<root>/world/<world>/<name>.py``,
carrying the store's header over its code; the code below the header is the module body.

``load(name)`` reads, parses and runs a script once per worker and caches the module by its file, so two imports (or
an import and ``sg.script``) share one module. The script's ``run_analysis`` goes to the hub as the run's own did: its
literals count as written for the run's gate, a script that writes makes the run one that writes, and its while lines
are known to the run's session, so a wait in the script's own loop is a loop wait. Its ``main(...)``
runs in this run's session, so report, the seen rule, held restores, stop and undo cover the whole call tree.

Errors are ``sg.Error`` with the store's codes: ``script_not_found``, ``script_world``, ``script_invalid``. A name
looked up as an attribute and naming no script raises ``script_not_found`` rather than ``AttributeError``, so
``hasattr(sg.lib, name)`` raises for it too; the names the import system asks for itself (dunders such as
``__path__``) keep ``AttributeError``, so ``import sg.lib.<name>`` fails as an ordinary missing submodule.
"""
import builtins
import keyword
import os
import types

import sg  # the package itself, as the run's own code sees it: a loaded script calls the same sg the run does

from . import _analysis, _session
from ._errors import Error
from ._table import run_print

# The store's header (ScriptHeader, the hub's format): its first line, its closing line.
HEADER_FIRST = "# stationgod script v1"
HEADER_END = "# ---"

LONGEST_NAME = 64

# The path of a script -> its module, so one worker's imports (and sg.script) share one module object.
_modules = {}


def load(name):
    """The saved script ``name`` as a module, ready to call (its ``main`` is the entry point).

    The name resolves as the store reads it: ``shared/<name>.py``, else ``world/<world>/<name>.py``; the file must
    carry the store's header, and the code below it is the module body. The module is loaded once per worker.
    An unknown name, a name saved in another world, a file without the header, a file that does not parse and one
    without ``main(...)`` each raise ``sg.Error``."""
    run = _session.current_run()
    if run is None or not run.scripts_dir:
        raise Error("not_available",
                    "no script store here: a run gets one from a hub that has it (--scripts-dir), and this session "
                    "has none",
                    "run the code inline, or have the hub serving this session started with --scripts-dir")
    path = _file(name, run.scripts_dir, run.world)
    if path in _modules:
        return _modules[path]

    code = _code(path)
    if code is None:
        raise Error("script_invalid",
                    f"{path} has no stationgod script header (write script writes one): its description and "
                    "parameters are unknown",
                    "save it again with write script")
    analysis = _analysis.analyse(code)
    if analysis.syntax_error is not None:
        raise Error("script_invalid", f"{path} does not parse: {analysis.syntax_error}",
                    "save it again from code that parses (write script)")
    _session.current_transport().send(analysis.message())
    run.add_while_lines(_compiled_name(name), analysis.while_lines)
    module = _execute(name, path, code)
    _modules[path] = module
    return module


def __getattr__(name):
    """``from sg.lib import <name>``: the saved script as a module (PEP 562)."""
    if name.startswith("__") and name.endswith("__"):
        raise AttributeError(f"sg.lib has no {name}")
    return load(name)


def _file(name, root, world):
    """The store file a name refers to: shared first, then this world's, as the store reads it. A name that is not a
    script's name names nothing; a script of another world is refused by name."""
    if not _is_name(name):
        raise Error("script_not_found", f"'{name}' is not a saved script's name",
                    "a saved script's name is one word of letters, digits and _, starting with a letter or _ "
                    "(find script lists them)")
    shared = os.path.join(root, "shared", name + ".py")
    if os.path.isfile(shared):
        return shared
    if world:
        here = os.path.join(root, "world", str(world), name + ".py")
        if os.path.isfile(here):
            return here
    elsewhere = _other_world(name, root, world)
    if elsewhere is not None:
        raise Error("script_world",
                    f"'{name}' is a world script of {elsewhere}: a world script runs only in the world it was "
                    "saved in",
                    "run it in that world, or save a shared copy (write script)")
    raise Error("script_not_found", f"no saved script is named '{name}'",
                "find script lists the scripts this world can see; write script saves one")


def _is_name(name):
    """The store's name rule, as far as a path lookup needs it: one word of letters, digits and _, no Python keyword."""
    return (isinstance(name, str) and 0 < len(name) <= LONGEST_NAME and name.isascii() and name.isidentifier()
            and not keyword.iskeyword(name))


def _other_world(name, root, world):
    """Another world's folder holding this script, or None: a world script its world alone may run."""
    worlds = os.path.join(root, "world")
    if not os.path.isdir(worlds):
        return None
    for folder in sorted(os.listdir(worlds)):
        if world is not None and folder.casefold() == str(world).casefold():
            continue
        if _is_script(os.path.join(worlds, folder, name + ".py")):
            return folder
    return None


def _code(path):
    """The script's code, or None when the file carries no header (a hand-added file is not a script of this store)."""
    return _body(_read(path))


def _is_script(path):
    """Whether the file carries the store's header; a file that cannot be read does not."""
    try:
        return _body(_read(path)) is not None
    except Error:
        return False


def _read(path):
    """The file's text; a file that cannot be read is refused as an invalid script."""
    try:
        with open(path, "r", encoding="utf-8") as file:
            return file.read()
    except (OSError, UnicodeDecodeError) as error:
        raise Error("script_invalid", f"{path} could not be read ({error})",
                    "check the store folder the hub was started with (--scripts-dir)") from None


def _body(text):
    """The code below the store's header, or None for text that is not one (the store's own reading, minimally)."""
    lines = text.split("\n")
    if not lines or lines[0].rstrip("\r") != HEADER_FIRST:
        return None
    for number, line in enumerate(lines[1:], start=1):
        line = line.rstrip("\r")
        if line == HEADER_END:
            return "\n".join(lines[number + 1:])
        if not line.startswith("# "):
            return None
    return None


def _compiled_name(name):
    """The file name a script's code is compiled under: its tracebacks, and the name the run's session knows its
    frames by (the loop lines above are kept under it)."""
    return f"<script {name}>"


def _execute(name, path, code):
    """The code as the module named name: the run's own view of a script (sg, print) and its file. The module must
    define the entry point main(...), as a saved script always does."""
    module = types.ModuleType(name)
    module.__file__ = path
    module.__dict__.update({"__builtins__": builtins, "sg": sg, "print": run_print})
    exec(compile(code, _compiled_name(name), "exec"), module.__dict__)
    if not callable(module.__dict__.get("main")):
        raise Error("script_invalid", f"{path} defines no main(...): a saved script's entry point is main",
                    "save it again with a def main(...) (write script)")
    return module
