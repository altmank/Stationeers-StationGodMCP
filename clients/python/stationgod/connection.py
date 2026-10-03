"""One connection to the mod: the transport (pipe or TCP), line framing, the TCP shared secret, the hello / welcome
exchange, and the reader thread that hands every line from the server to the client.

The connection knows nothing about retrying; client.py decides that when on_closed reports what was in flight.
"""
import json
import logging
import os
import socket
import threading

from . import pipe
from .errors import GameError, TooOld, Unauthorized, Unreachable, game_error

log = logging.getLogger("stationgod")

LIBRARY_VERSION = "0.2.0"
LIBRARY = f"stationgod-py/{LIBRARY_VERSION}"
FEATURES = ["shape", "shape.paths", "subscriptions", "cancel"]
HANDSHAKE_TIMEOUT_S = 10.0  # how long the server may take to answer the secret or hello



# ---- transports -------------------------------------------------------------------------------------------------

class PipeTarget:
    kind = "pipe"

    def __init__(self, name):
        self.name = name

    def describe(self):
        return pipe.full_name(self.name)

    def open(self, timeout):
        try:
            return _PipeStream(pipe.connect(self.name, timeout))
        except pipe.PipeBusy as error:
            raise Unreachable(f"the pipe stayed busy: every instance of {self.describe()} is taken") from error
        except pipe.PipeMissing as error:
            raise Unreachable(f"no pipe {self.describe()}: the game is not running, not hosting, "
                              "or no save is loaded") from error
        except OSError as error:
            raise Unreachable(f"cannot open {self.describe()} ({error})") from error


class TcpTarget:
    kind = "tcp"

    def __init__(self, host, port):
        self.host = host
        self.port = port

    def describe(self):
        return f"{self.host}:{self.port}"

    def open(self, timeout):
        try:
            sock = socket.create_connection((self.host, self.port), timeout=timeout)
        except OSError as error:
            raise Unreachable(f"no StationGod server answered at {self.describe()} ({error})") from error
        sock.settimeout(None)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        return _SocketStream(sock)


class _PipeStream:
    def __init__(self, handle):
        self._handle = handle

    def read(self):
        return self._handle.read(65536)

    def write(self, data):
        self._handle.write(data)

    def close(self):
        self._handle.close()


class _SocketStream:
    def __init__(self, sock):
        self._sock = sock
        self._write_lock = threading.Lock()
        self._closed = False

    def read(self):
        try:
            return self._sock.recv(65536)
        except OSError:
            return b""

    def write(self, data):
        with self._write_lock:
            self._sock.sendall(data)

    def close(self):
        if self._closed:
            return
        self._closed = True
        try:
            self._sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self._sock.close()


class _Lines:
    """Line framing (protocol.md, Framing): LF ends a message, a CR before it is dropped, blank lines are skipped."""

    def __init__(self, stream):
        self._stream = stream
        self._buffer = bytearray()

    def next(self):
        """The next line's bytes, or None when the stream ended."""
        while True:
            end = self._buffer.find(b"\n")
            while end >= 0:
                line = bytes(self._buffer[:end])
                del self._buffer[:end + 1]
                if line.endswith(b"\r"):
                    line = line[:-1]
                if line.strip():
                    return line
                end = self._buffer.find(b"\n")
            chunk = self._stream.read()
            if not chunk:
                return None
            self._buffer += chunk


def encode(message):
    return (json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


# ---- the connection ---------------------------------------------------------------------------------------------

class Connection:
    """A signed-in connection; welcome is the server's welcome message. After start(), the reader thread calls
    on_message(connection, message, size) for every line and on_closed(connection) once when the connection ends,
    whoever ended it."""

    def __init__(self, stream, lines, target, welcome):
        self.stream = stream
        self.lines = lines
        self.target = target
        self.welcome = welcome
        self.features = frozenset(welcome.get("features") or [])
        limits = welcome.get("limits") or {}
        self.max_in_flight = int(limits.get("max_in_flight") or 16)
        world = (welcome.get("server") or {}).get("world") or {}
        self.world = world if world else None
        self.world_id = world.get("id")
        self.goodbye = None
        self.alive = True
        self.pending = {}            # call id -> client call, guarded by the client's lock
        self.in_flight = 0           # guarded by the client's lock
        self._close_lock = threading.Lock()
        self._closed_reported = False
        self.on_message = None
        self.on_closed = None

    def start(self, on_message, on_closed):
        self.on_message = on_message
        self.on_closed = on_closed
        threading.Thread(target=self._read, name=f"stationgod-reader-{self.target.describe()}", daemon=True).start()

    def send(self, message):
        """Writes one message; OSError when the connection is broken (and it is then closed)."""
        try:
            self.stream.write(encode(message))
        except OSError:
            self.close()
            raise

    def close(self):
        self.alive = False
        self.stream.close()

    def _read(self):
        try:
            while True:
                line = self.lines.next()
                if line is None:
                    break
                try:
                    message = json.loads(line.decode("utf-8"))
                except ValueError:
                    log.warning("stationgod: a line from the server is not JSON: %r", line[:80])
                    continue
                if isinstance(message, dict):
                    try:
                        self.on_message(self, message, len(line) + 1)
                    except Exception:  # a bug in a handler must not stop the reader
                        log.exception("stationgod: handling a server message failed")
        except Exception:
            log.exception("stationgod: the reader stopped")
        finally:
            self.close()
            with self._close_lock:
                if self._closed_reported:
                    return
                self._closed_reported = True
            self.on_closed(self)


def _read_with_timeout(stream, lines, timeout, what):
    """The next line as a dict during the handshake, closing the stream if none comes in time."""
    expired = []

    def expire():
        expired.append(True)
        stream.close()

    timer = threading.Timer(timeout, expire)
    timer.daemon = True
    timer.start()
    try:
        line = lines.next()
    finally:
        timer.cancel()
    if expired:
        raise Unreachable(f"no answer to {what} within {timeout:g} s")
    if line is None:
        return None
    try:
        message = json.loads(line.decode("utf-8"))
    except ValueError:
        stream.close()
        raise Unreachable(f"the server answered {what} with a line that is not JSON: {line[:80]!r}")
    return message if isinstance(message, dict) else {}


def open_connection(target, *, client=None, secret=None, connect_timeout=1.0, handshake_timeout=HANDSHAKE_TIMEOUT_S):
    """Connects. Over TCP the shared secret goes first; after it TCP talks as the pipe does. Sends hello offering
    protocol 2 and needs a welcome."""
    stream = target.open(connect_timeout)
    lines = _Lines(stream)
    try:
        if target.kind == "tcp":
            _sign_in(target, stream, lines, secret, handshake_timeout)
        hello = {"type": "hello", "protocol": [2],
                 "client": {"name": client or "stationgod-py", "version": LIBRARY_VERSION, "library": LIBRARY},
                 "features": FEATURES}
        stream.write(encode(hello))
        answer = _read_with_timeout(stream, lines, handshake_timeout, "hello")
        if answer is None:
            raise Unreachable(f"{target.describe()} closed the connection after hello")
        if answer.get("type") == "welcome":
            return Connection(stream, lines, target, answer)
        if answer.get("type") == "reply" and answer.get("ok") is False:
            error = game_error(answer.get("error"))
            if error.code == "unsupported_protocol":
                raise TooOld(f"{target.describe()} does not speak protocol version 2: {error.message}")
            raise error
        raise Unreachable(f"unexpected answer to hello: {json.dumps(answer)[:120]}")
    except BaseException:
        stream.close()
        raise


def _sign_in(target, stream, lines, secret, handshake_timeout):
    """TCP: the shared secret, before anything else."""
    if not secret:
        raise Unauthorized("unauthorized", f"{target.describe()}: TCP needs the shared secret (secret_env)")
    stream.write(encode({"type": "auth", "secret": secret}))
    answer = _read_with_timeout(stream, lines, handshake_timeout, "the shared secret")
    if not answer or answer.get("ok") is not True:
        raise game_error((answer or {}).get("error") or {"code": "unauthorized",
                                                         "message": "the shared secret was refused"})


def read_secret(variable):
    value = os.environ.get(variable) if variable else None
    return value.strip() if value and value.strip() else None


__all__ = ["Connection", "PipeTarget", "TcpTarget", "open_connection", "read_secret",
           "GameError"]
