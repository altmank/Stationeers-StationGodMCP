"""What the worker learns from a run's code before any of it runs (hub.md, *Runs*: run_analysis), and how the code
is compiled as a function body.

The code is parsed once. Its statements become the body of a function `__sg_run__` built in the syntax tree, not by
indenting text, so every line number the run reports is the user's own line. A final bare expression becomes the
function's return value; `return x` works as written.
"""
import ast

from ._catalogue import WRITE_WHATS
from ._session import SCRIPT_FILENAME

RUN_FUNCTION = "__sg_run__"


class Analysis:
    """The run_analysis fields, plus the compiled code (None on a syntax error)."""

    def __init__(self):
        self.literals = []
        self.while_lines = []
        self.writes = False
        self.holds = False
        self.syntax_error = None
        self.code = None

    def message(self):
        return {"type": "run_analysis", "literals": self.literals, "while_lines": self.while_lines,
                "writes": self.writes, "holds": self.holds, "syntax_error": self.syntax_error}


def analyse(source):
    """Parses and compiles source; fills every run_analysis field."""
    analysis = Analysis()
    try:
        tree = ast.parse(source, SCRIPT_FILENAME)
        analysis.literals = literals(tree)
        analysis.while_lines = while_lines(tree)
        analysis.writes, analysis.holds = writes_and_holds(tree)
        analysis.code = compile(as_function(tree), SCRIPT_FILENAME, "exec")
    except SyntaxError as error:
        analysis.syntax_error = f"line {error.lineno or 1}: SyntaxError: {error.msg}"
        analysis.code = None
    return analysis


def literals(tree):
    """Every int and str constant, in source order, without repeats; f-string pieces are str constants in the
    tree and count too. bools are not ints here. A negative int literal (-5) adds -5 as well as 5."""
    constants = []
    for node in ast.walk(tree):
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub) and _is_int(node.operand):
            constants.append((node.lineno, node.col_offset, -node.operand.value))
        elif isinstance(node, ast.Constant) and (_is_int(node) or isinstance(node.value, str)):
            constants.append((getattr(node, "lineno", 0), getattr(node, "col_offset", 0), node.value))
    found, seen = [], set()
    for _, _, value in sorted(constants, key=lambda entry: (entry[0], entry[1])):
        key = (type(value), value)
        if key not in seen:
            seen.add(key)
            found.append(value)
    return found


def _is_int(node):
    return isinstance(node, ast.Constant) and isinstance(node.value, int) and not isinstance(node.value, bool)


def while_lines(tree):
    """The lines of every statement inside a while body, continuation lines included, sorted."""
    lines = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.While):
            for statement in node.body:
                lines.update(range(statement.lineno, (statement.end_lineno or statement.lineno) + 1))
    return sorted(lines)


def writes_and_holds(tree):
    """(writes, holds): whether the code calls a write (a verb whose what writes; a what that is not a constant
    counts as writing when the verb has any writing what) and whether it uses sg.hold. A write verb named without
    being called (f = sg.write) counts, since it can be called later. `import sg as s` and `from sg import write`
    are followed."""
    module_names, function_names = _sg_names(tree)
    writes = holds = False
    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id in module_names:
            name = node.attr
        elif isinstance(node, ast.Name) and node.id in function_names:
            name = function_names[node.id]
        else:
            continue
        if name == "hold":
            holds = True
        elif name in WRITE_WHATS and _may_write(name, _call_of(tree, node)):
            writes = True
    return writes, holds


def _sg_names(tree):
    """Names the code binds to the sg module, and {local name: sg function} from `from sg import ...`."""
    modules, functions = {"sg"}, {}
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                if alias.name == "sg":
                    modules.add(alias.asname or "sg")
        elif isinstance(node, ast.ImportFrom) and node.module == "sg":
            for alias in node.names:
                functions[alias.asname or alias.name] = alias.name
    return modules, functions


def _call_of(tree, function_node):
    """The Call whose function is function_node, or None when it is only named."""
    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and node.func is function_node:
            return node
    return None


def _may_write(verb, call):
    """Whether a call of verb may write: its what is a constant naming a writing what, or not a constant at all."""
    writing = WRITE_WHATS[verb]
    if writing == ("-",):
        return True
    what = None
    if call is not None and call.args:
        what = call.args[0]
    elif call is not None:
        what = next((keyword.value for keyword in call.keywords if keyword.arg == "what"), None)
    if isinstance(what, ast.Constant) and isinstance(what.value, str):
        return what.value in writing
    return True


def as_function(tree):
    """The module's statements as the body of `def __sg_run__():`, a final bare expression returned."""
    body = list(tree.body)
    if body and isinstance(body[-1], ast.Expr):
        last = body[-1]
        body[-1] = ast.copy_location(ast.Return(value=last.value), last)
    if not body:
        body = [ast.Pass()]
    function = ast.FunctionDef(name=RUN_FUNCTION, args=ast.arguments(posonlyargs=[], args=[], vararg=None,
                                                                     kwonlyargs=[], kw_defaults=[], kwarg=None,
                                                                     defaults=[]),
                               body=body, decorator_list=[], returns=None, type_comment=None)
    function.lineno, function.col_offset = 1, 0
    module = ast.Module(body=[function], type_ignores=[])
    ast.fix_missing_locations(module)
    return module
