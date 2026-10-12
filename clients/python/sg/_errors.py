"""The three exceptions an sg call can raise, and the mapping from a hub error object to one of them.

The hub marks a refusal by the run's write rule with "stopped": true and the end of a report pass with
"pass_end": true (docs/architecture/hub.md, *The hub protocol*). Both must unwind through `except Exception`, so they
are BaseExceptions: the user's `finally` and `with sg.hold` blocks still run, and the run ends the way the hub said.
"""


class Error(Exception):
    """The hub or the game refused an sg call. `fix` says what to send next; `maybe_ran` is true when a write may
    have acted although the call failed; `call` is the call that was sent (verb, what, args)."""

    def __init__(self, code, message, fix=None, maybe_ran=False, call=None, data=None):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.fix = fix
        self.maybe_ran = bool(maybe_ran)
        self.call = call
        self.data = data


class Stopped(BaseException):
    """The run was stopped (server stop, a user cancel, a world change, an unseen target). Raised once, at the next sg
    call; `rows` are the call's targets as report rows when the write rule refused it."""

    def __init__(self, code, message, fix=None, line=None, rows=None):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.fix = fix
        self.line = line
        self.rows = rows or []


class _PassEnd(BaseException):
    """A report run's pass ends here (the second counted wait after a write). Private: the worker reports it as the
    run's `pass_end` state; user code should let it pass."""

    def __init__(self, code="pass_end", message="the report pass ends here"):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message


def from_error(error, call=None, line=None):
    """The exception for a hub error object: stopped -> Stopped, pass_end -> _PassEnd, anything else -> Error."""
    if not isinstance(error, dict):
        error = {"code": "protocol_error", "message": f"the hub answered an error that is not an object: {error!r}"}
    code = error.get("code") or "error"
    message = error.get("message") or ""
    data = error.get("data") if isinstance(error.get("data"), dict) else {}
    if error.get("stopped"):
        return Stopped(code, message, error.get("fix"), data.get("line", line), data.get("rows"))
    if error.get("pass_end"):
        return _PassEnd(code, message)
    return Error(code, message, error.get("fix"), error.get("maybe_ran", False), call, error.get("data"))
