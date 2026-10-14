"""A saved script, as the hub reads it at save time: the ``main(...)`` signature ``run_script {name, args}`` checks its
arguments against, and the id-shaped constants a ``shared`` script may not carry.

The hub is C# and cannot parse Python, so ``write script`` starts this module once, in a short-lived process, and keeps
what it answers in the script's header (see the store's format). Called as

    python -s -P -c "<BOOT>" <the folder that holds sg>

with the script's source on stdin, and one JSON object on stdout:

    {"ok": true, "parameters": [{"name": "who", "type": "str", "default": false, "kind": "pos_or_kw"}], "ids": []}
    {"ok": false, "error": "line 3: invalid syntax"}

``parameters`` is empty for a ``main()`` that takes none; ``ids`` is every constant used as an id (see ``ids``). The
parse is partial by necessity: ``run_script`` refuses missing required arguments, unknown argument names and the
argument types it can map, and a shared script is refused only for the id literals ``ids`` finds.
"""
import ast
import json
import sys

BOOT = "import sys; sys.path.insert(0, sys.argv[1]); import sg._script; sg._script.main()"

# The option names and dict keys an id is passed under (the catalogue's names for targets).
ID_KEYS = ("id", "ids", "reference_id", "to_id", "from_id")

# An int at or above this is id-shaped: no count, wait in seconds or tick count in a script reaches it.
ID_SIZE = 100000

KIND_POS_OR_KW = "pos_or_kw"
KIND_KEYWORD_ONLY = "keyword_only"
KIND_VAR_POSITIONAL = "var_positional"
KIND_VAR_KEYWORD = "var_keyword"


def parse(source):
    """The source's signature and id literals: {ok, parameters, ids}, or {ok: false, error}."""
    try:
        tree = ast.parse(source, "<script>")
    except SyntaxError as error:
        return {"ok": False, "error": f"line {error.lineno or 1}: {error.msg}"}
    except (RecursionError, MemoryError, ValueError) as error:
        return {"ok": False, "error": f"line 1: {type(error).__name__}: {error}"}

    main = next((node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "main"), None)
    if main is None:
        if any(isinstance(node, ast.AsyncFunctionDef) and node.name == "main" for node in tree.body):
            return {"ok": False, "error": "main may not be async"}
        return {"ok": False, "error": "no def main(...): a saved script's entry point is main"}

    problem = signature_problem(main)
    if problem is not None:
        return {"ok": False, "error": problem}
    return {"ok": True, "parameters": parameters(main), "ids": ids(tree)}


def signature_problem(main):
    """Why main's signature cannot be called by name, or None."""
    if main.args.posonlyargs:
        return "main may not take positional-only parameters: run_script passes arguments by name"
    return None


def parameters(main):
    """main's parameters in signature order: the name, the annotation as written (None without one), whether it has a
    default, and its kind. var_positional and var_keyword entries carry no default."""
    found = []
    for name, default in zip(main.args.args, [None] * (len(main.args.args) - len(main.args.defaults)) + main.args.defaults):
        found.append(parameter(name, KIND_POS_OR_KW, default))
    if main.args.vararg is not None:
        found.append(parameter(main.args.vararg, KIND_VAR_POSITIONAL, None))
    for name, default in zip(main.args.kwonlyargs, main.args.kw_defaults):
        found.append(parameter(name, KIND_KEYWORD_ONLY, default))
    if main.args.kwarg is not None:
        found.append(parameter(main.args.kwarg, KIND_VAR_KEYWORD, None))
    return found


def parameter(arg, kind, default):
    return {"name": arg.arg, "type": type_text(arg.annotation), "default": default is not None, "kind": kind}


def type_text(annotation):
    """An annotation as written: its own text, a string annotation's text, or None."""
    if annotation is None:
        return None
    if isinstance(annotation, ast.Constant) and isinstance(annotation.value, str):
        return annotation.value
    try:
        return ast.unparse(annotation)
    except (AttributeError, ValueError, RecursionError):
        return None


def ids(tree):
    """The int and str constants the code uses as ids: a constant (or any constant inside its list, tuple, dict or
    f-string literal) that is the value of an id keyword or dict key, plus every int constant of ID_SIZE and up, and
    every digits-only str constant whose value is ID_SIZE and up (ids are usually written as strings).

    Not found: an id built at run time (arithmetic, int(), f-string interpolation), an id below ID_SIZE reached
    through a variable and passed as the option's value, and composite string ids ("5-1234")."""
    found, seen = [], set()

    def add(value):
        if isinstance(value, bool) or not isinstance(value, (int, str)):
            return
        key = (type(value), value)
        if key not in seen:
            seen.add(key)
            found.append(value)

    def collect(node):
        if isinstance(node, ast.Constant):
            add(node.value)
        elif isinstance(node, (ast.List, ast.Tuple, ast.Set)):
            for item in node.elts:
                collect(item)
        elif isinstance(node, ast.Dict):
            for value in node.values:
                collect(value)
        elif isinstance(node, ast.FormattedValue):
            collect(node.value)
        elif isinstance(node, ast.JoinedStr):
            for part in node.values:
                collect(part)

    for node in ast.walk(tree):
        if isinstance(node, ast.keyword) and node.arg in ID_KEYS:
            collect(node.value)
        elif isinstance(node, ast.Dict):
            for key, value in zip(node.keys, node.values):
                if isinstance(key, ast.Constant) and key.value in ID_KEYS:
                    collect(value)
        elif isinstance(node, ast.Constant):
            if isinstance(node.value, int) and not isinstance(node.value, bool) and node.value >= ID_SIZE:
                add(node.value)
            elif isinstance(node.value, str) and node.value.isascii() and node.value.isdigit() \
                    and int(node.value) >= ID_SIZE:
                add(node.value)
    return found


def main():
    """The one-shot process: the source on stdin, the parse's JSON on stdout."""
    source = sys.stdin.read()
    sys.stdout.write(json.dumps(parse(source), ensure_ascii=False))
    sys.stdout.flush()


if __name__ == "__main__":
    main()
