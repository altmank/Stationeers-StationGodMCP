"""The failed-run text (SPEC note 22): what an agent needs to fix the code without running it again to look.

In order: `line N: <source>`; the exception type and message; for sg.Error the call sent, its code, fix and
maybe_ran; for a KeyError on a reply dict the keys it has, nearest first; the values of the names on that line, each
cut at 80 characters.
"""
import difflib
import io
import tokenize

from ._calls import call_text
from ._errors import Error, Stopped
from ._session import SCRIPT_FILENAME

VALUE_WIDTH = 80


def exception_name(exception):
    """sg's own exceptions by their sg names; others by their class name."""
    if isinstance(exception, Error):
        return "sg.Error"
    if isinstance(exception, Stopped):
        return "sg.Stopped"
    return type(exception).__name__


def failed_text(exception, source):
    """The note-22 text for an exception that ended a run whose code was source."""
    frame, line = _script_frame(exception)
    source_lines = source.splitlines()
    parts = []
    if line is not None:
        text = source_lines[line - 1].strip() if 0 < line <= len(source_lines) else ""
        parts.append(f"line {line}: {text}")
    parts.append(f"{exception_name(exception)}: {exception}")
    if isinstance(exception, Error):
        parts.append(f"call: {call_text(exception.call)}; code: {exception.code}; fix: {exception.fix}; "
                     f"maybe_ran: {str(exception.maybe_ran).lower()}")
    keys = getattr(exception, "reply_keys", None)
    if isinstance(exception, KeyError) and keys is not None:
        parts.append("the reply has keys: " + ", ".join(nearest_first(exception, keys)))
    if frame is not None and line is not None and 0 < line <= len(source_lines):
        values = line_values(source_lines[line - 1], frame)
        if values:
            parts.append("values: " + "; ".join(values))
    return "\n".join(parts)


def stopped_text(stop):
    """A short text for a run that a stop ended (not note 22: nothing in the code failed)."""
    where = f"line {stop.line}: " if stop.line is not None else ""
    fix = f" (fix: {stop.fix})" if stop.fix else ""
    return f"{where}sg.Stopped: {stop.code}: {stop.message}{fix}"


def _script_frame(exception):
    """The innermost traceback frame in the run's code, and its line."""
    found = None
    traceback = exception.__traceback__
    while traceback is not None:
        if traceback.tb_frame.f_code.co_filename == SCRIPT_FILENAME:
            found = traceback
        traceback = traceback.tb_next
    if found is None:
        return None, None
    return found.tb_frame, found.tb_lineno


def nearest_first(error, keys):
    """keys ordered by how close each is to the missing key."""
    wanted = str(error.args[0]) if error.args else ""
    keys = [str(key) for key in keys]
    return difflib.get_close_matches(wanted, keys, n=len(keys), cutoff=0.0) or keys


def line_values(text, frame):
    """`name = value` for each name on the line that the frame can see, in order, each value cut to 80 characters.
    Names come from the line's tokens, so words inside strings are not taken for names."""
    shown = []
    for name in _names(text):
        if name in shown or name == "sg":
            continue
        if name in frame.f_locals:
            value = frame.f_locals[name]
        elif name in frame.f_globals and name not in ("print", "__builtins__"):
            value = frame.f_globals[name]
        else:
            continue
        if type(value).__name__ in ("module", "function", "builtin_function_or_method", "type"):
            continue
        shown.append(name)
    return [f"{name} = {_cut(safe_repr(_value(frame, name)))}" for name in shown]


def _value(frame, name):
    return frame.f_locals[name] if name in frame.f_locals else frame.f_globals[name]


def safe_repr(value):
    """repr that cannot end the worker: a main list's repr may page from the hub, and that may fail now."""
    try:
        return repr(value)
    except BaseException as error:  # noqa: BLE001 - Stopped included: the run is over either way
        return f"<{type(value).__name__}: repr failed: {error}>"


def _cut(text):
    return text if len(text) <= VALUE_WIDTH else text[:VALUE_WIDTH - 3] + "..."


def _names(text):
    names = []
    try:
        for token in tokenize.generate_tokens(io.StringIO(text.strip() + "\n").readline):
            if token.type == tokenize.NAME:
                names.append(token.string)
    except (tokenize.TokenError, IndentationError, SyntaxError):
        pass  # an unfinished line (an open bracket): the names read so far are enough
    return names
