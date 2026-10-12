"""What the worker learns from a run's code before any of it runs (hub.md, *Runs*: run_analysis), and how the code
is compiled as a function body.

The code is parsed once. Its statements become the body of a function `__sg_run__` built in the syntax tree, not by
indenting text, so every line number the run reports is the user's own line. A final bare expression becomes the
function's return value; `return x` works as written. So that the body still behaves like a module:
- every name it binds is declared `global` in the function, so a `def f(): global count` in the code works (a
  `global` statement of the code's own becomes `pass`: it is a no-op at module level);
- `from __future__ import ...` moves to the very top, ahead of the function;
- `from x import *` becomes, in place, a call that copies the module's public names into the run's globals (a
  function may not hold a star import), so it works inside `try` or `if` too;
- a top-level annotation `x: int = 1` becomes `x = 1` (a global name cannot be annotated);
- a top-level `yield` or `await` is refused as a syntax error.

`writes` and `holds` are advisory: code can always reach a write the AST pass cannot see (getattr, exec). The hub does
not trust them for safety: a run moves to the confirmed run's rules at its first write call.
"""
import ast
import importlib
import math

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
    except (RecursionError, MemoryError, ValueError) as error:  # code nested too deep for the parser, or too big
        analysis.syntax_error = f"line 1: {type(error).__name__}: {error}"
        analysis.code = None
    return analysis


def literals(tree):
    """Every int, float and str constant, in source order, without repeats; f-string pieces are str constants in
    the tree and count too. Floats are there for selectors written with them (box=[[1.5, 2.0, 3.5], ...]); each stays a
    JSON number, so the hub compares by value (2.0 equals 2). bools, NaN and the infinities are left out. A negative
    number literal (-5, -1.5) adds the negative as well as the positive."""
    constants = []
    for node in ast.walk(tree):
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub) and _is_number(node.operand):
            constants.append((node.lineno, node.col_offset, -node.operand.value))
        elif isinstance(node, ast.Constant) and (_is_number(node) or isinstance(node.value, str)):
            constants.append((getattr(node, "lineno", 0), getattr(node, "col_offset", 0), node.value))
    found, seen = [], set()
    for _, _, value in sorted(constants, key=lambda entry: (entry[0], entry[1])):
        key = (type(value), value)
        if key not in seen:
            seen.add(key)
            found.append(value)
    return found


def _is_number(node):
    """An int or finite float constant (not a bool)."""
    if not isinstance(node, ast.Constant) or isinstance(node.value, bool):
        return False
    if isinstance(node.value, int):
        return True
    return isinstance(node.value, float) and math.isfinite(node.value)


def while_lines(tree):
    """The lines of every while's test and of every statement inside its body, continuation lines included,
    sorted."""
    lines = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.While):
            lines.update(range(node.test.lineno, (node.test.end_lineno or node.test.lineno) + 1))
            for statement in node.body:
                lines.update(range(statement.lineno, (statement.end_lineno or statement.lineno) + 1))
    return sorted(lines)


def writes_and_holds(tree):
    """(writes, holds): whether the code calls a write (a verb whose what writes; a what that is not a constant
    counts as writing when the verb has any writing what) and whether it uses sg.hold. A write verb named without
    being called (f = sg.write) counts, since it can be called later. `import sg as s` and `from sg import write`
    are followed, as are `sg._verbs` and its imports."""
    module_names, function_names = _sg_names(tree)
    writes = holds = False
    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute) and _is_sg_module(node.value, module_names):
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


def _is_sg_module(node, module_names):
    """sg (or a name bound to it or to sg._verbs), or sg._verbs written out."""
    if isinstance(node, ast.Name):
        return node.id in module_names
    return isinstance(node, ast.Attribute) and node.attr == "_verbs" and _is_sg_module(node.value, module_names)


def _sg_names(tree):
    """Names the code binds to sg or sg._verbs, and {local name: sg function} from `from sg import ...`."""
    modules, functions = {"sg"}, {}
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                if alias.name in ("sg", "sg._verbs") and alias.asname:
                    modules.add(alias.asname)
        elif isinstance(node, ast.ImportFrom) and node.module in ("sg", "sg._verbs"):
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
    """The module's statements as the body of `def __sg_run__():` (see the module docstring), a final bare
    expression returned."""
    refuse_top_level_yield(tree.body)
    futures = [statement for statement in tree.body if _is_future_import(statement)]
    body = [_ModuleLevel().visit(statement) for statement in tree.body if not _is_future_import(statement)]
    if body and isinstance(body[-1], ast.Expr):
        last = body[-1]
        body[-1] = ast.copy_location(ast.Return(value=last.value), last)
    if not body:
        body = [ast.Pass()]
    function = _function(body)
    names = bound_names(futures, function)
    if names:
        function.body.insert(0, ast.copy_location(ast.Global(names=names), body[0]))
    module = ast.Module(body=futures + [function], type_ignores=[])
    ast.fix_missing_locations(module)
    return module


def _function(body):
    function = ast.FunctionDef(name=RUN_FUNCTION, args=ast.arguments(posonlyargs=[], args=[], vararg=None,
                                                                     kwonlyargs=[], kw_defaults=[], kwarg=None,
                                                                     defaults=[]),
                               body=body, decorator_list=[], returns=None, type_comment=None)
    if "type_params" in ast.FunctionDef._fields:
        function.type_params = []
    function.lineno, function.col_offset = 1, 0
    return function


def bound_names(futures, function):
    """The names the run function binds itself (its locals, captured ones included), read from its compiled code.
    Compiling here also reports the code's own syntax errors at their true lines."""
    module = ast.fix_missing_locations(ast.Module(body=futures + [function], type_ignores=[]))
    code = compile(module, SCRIPT_FILENAME, "exec")
    run = next(constant for constant in code.co_consts
               if hasattr(constant, "co_name") and constant.co_name == RUN_FUNCTION)
    return sorted(set(run.co_varnames) | set(run.co_cellvars))


def _is_future_import(statement):
    return isinstance(statement, ast.ImportFrom) and statement.module == "__future__"


def import_star(module_name, namespace):
    """`from module_name import *` into namespace (the run's globals): the module's __all__, else its names that do
    not start with an underscore."""
    module = importlib.import_module(module_name)
    names = getattr(module, "__all__", None)
    if names is None:
        names = [name for name in vars(module) if not name.startswith("_")]
    for name in names:
        namespace[name] = getattr(module, name)


class _ModuleLevel(ast.NodeTransformer):
    """Rewrites what a module allows and a function does not, at the code's own level (not inside its defs):
    `global` statements (no-ops at module level) become `pass`, star imports become import_star calls, and annotated
    assignments lose the annotation."""

    def visit_FunctionDef(self, node):
        return node

    visit_AsyncFunctionDef = visit_ClassDef = visit_Lambda = visit_FunctionDef

    def visit_Global(self, node):
        return ast.copy_location(ast.Pass(), node)

    def visit_ImportFrom(self, node):
        if node.level or not any(alias.name == "*" for alias in node.names):
            return node
        call = ast.parse(f"__import__('sg._analysis', fromlist=['import_star']).import_star({node.module!r}, "
                         "globals())", mode="eval").body
        for child in ast.walk(call):
            ast.copy_location(child, node)
            child.end_lineno, child.end_col_offset = node.end_lineno, node.end_col_offset
        return ast.copy_location(ast.Expr(value=call), node)

    def visit_AnnAssign(self, node):
        if node.value is None:
            return ast.copy_location(ast.Pass(), node) if isinstance(node.target, ast.Name) else node
        return ast.copy_location(ast.Assign(targets=[node.target], value=node.value), node)


def refuse_top_level_yield(statements):
    """Raises SyntaxError for a yield or await at the code's own level (where a module could not have one)."""
    pending = list(statements)
    while pending:
        node = pending.pop()
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef, ast.Lambda)):
            continue
        if isinstance(node, (ast.Yield, ast.YieldFrom, ast.Await)):
            word = "await" if isinstance(node, ast.Await) else "yield"
            error = SyntaxError(f"'{word}' outside function: a run's code is a function body, not a generator")
            error.lineno = node.lineno
            raise error
        pending.extend(ast.iter_child_nodes(node))
