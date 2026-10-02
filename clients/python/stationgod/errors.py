"""What a call can raise (clients.md, Interface): GameError when the mod answered with an error, Unreachable when no
answer came, TooOld when the server cannot do what was asked."""


class StationGodError(Exception):
    """Base of every error this library raises."""


class GameError(StationGodError):
    """The mod answered with an error: code is for programs, message for people, data is the error's data object."""

    def __init__(self, code, message, data=None):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.data = data or {}


class PermissionDenied(GameError):
    """permission_denied: the call needs a higher level than this connection has. Nothing ran."""


class CheatNotArmed(GameError):
    """cheat_not_armed: the owner has not approved cheats for this connection now. Nothing ran."""


class InvalidArgument(GameError):
    """invalid_argument (or invalid_shape): the arguments break the catalogue. Nothing ran."""


class NotFound(GameError):
    """A *_not_found code: thing_not_found, device_not_found, ..."""


class SubscriptionRefused(GameError):
    """subscription_limit: poll read_devices with the same items instead."""


class Unauthorized(GameError):
    """unauthorized: bad or missing key proof, unknown client name, or a key not allowed on this transport."""


class Unreachable(StationGodError):
    """No answer: no pipe, the connection broke, or no reply in time. maybe_ran is true when the call was written
    and is not a read, so it may have changed the game."""

    def __init__(self, message, maybe_ran=False):
        super().__init__(message)
        self.message = message
        self.maybe_ran = maybe_ran


class WorldChanged(Unreachable):
    """The connection came back to another world (a different welcome.server.world.id): calls in flight are not sent
    again, because the reference ids they carry may name other things now."""


class TooOld(StationGodError):
    """The server cannot do what was asked: a feature it did not list in welcome.features, or a method it does not
    have."""


class MethodNotFound(GameError, TooOld):
    """method_not_found: no such method on this mod. Both a GameError and a TooOld."""


_BY_CODE = {
    "permission_denied": PermissionDenied,
    "cheat_not_armed": CheatNotArmed,
    "invalid_argument": InvalidArgument,
    "invalid_shape": InvalidArgument,
    "subscription_limit": SubscriptionRefused,
    "unauthorized": Unauthorized,
    "method_not_found": MethodNotFound,
}


def game_error(error):
    """The exception for an error object {code, message, data} from a reply."""
    error = error if isinstance(error, dict) else {}
    code = str(error.get("code") or "unknown")
    message = str(error.get("message") or "")
    data = error.get("data") if isinstance(error.get("data"), dict) else None
    kind = _BY_CODE.get(code) or (NotFound if code.endswith("_not_found") else GameError)
    return kind(code, message, data)
