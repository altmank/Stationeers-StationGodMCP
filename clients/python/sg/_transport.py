"""sg's one transport: the hub protocol (docs/architecture/hub.md) over a pair of byte streams.

Messages are compact JSON, one per line (docs/architecture/protocol.md, *Framing*). A reader thread reads every line
the hub sends: replies are handed to the thread waiting on that call's id, events to the registered handlers, and
welcomes to whoever is waiting for the next session. So a watch thread and the main thread can call at the same time.

Two ways to get one:
- `Transport(reader, writer)` over streams already open (the script worker's stdin and stdout);
- `connect(hub_path, *hub_args)`, which starts a child hub (`StationGodMCP.Server --hub`) and talks over its stdin
  and stdout. The child dies with the program: its stdin is closed at exit, and on Windows it sits in a Job Object
  that kills it when this process ends, however it ends.
"""
import atexit
import json
import os
import shutil
import subprocess
import sys
import threading

from ._errors import Error

VERSION = "0.4.14"
PROTOCOL = [2]
FEATURES = ["pipeline"]
HUB_ENVIRONMENT_VARIABLE = "STATIONGODMCP_HUB"
HUB_PROGRAM = "StationGodMCP.Server"
HELLO_TIMEOUT_S = 10.0


def encode(message):
    """One message as the bytes of one line: compact JSON, UTF-8, then a line feed. NaN and the infinities raise
    ValueError (JSON has no such numbers); a lone surrogate in a string is written as its \\uXXXX escape, so the line
    is always valid UTF-8."""
    text = json.dumps(message, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n"
    return text.encode("utf-8", "backslashreplace")


class _Pending:
    """A call written and not answered yet: the reader thread fills `reply` and sets `done`."""

    def __init__(self):
        self.done = threading.Event()
        self.reply = None


class Transport:
    """One hub-protocol connection. `hello()` opens a session; `call()` sends a call and waits for its reply."""

    def __init__(self, reader, writer, client="sg-py", process=None, on_end_of_stream=None, closable=True):
        """on_end_of_stream: called (on the reader thread) once the hub's stream ends or breaks. closable=False:
        close() and `with` do nothing (the script worker's transport, which user code can reach)."""
        self._closable = closable
        self._on_end_of_stream = on_end_of_stream
        self._reader = reader
        self._writer = writer
        self._client = client
        self._process = process
        self._job = None
        self._write_lock = threading.Lock()
        self._state_lock = threading.Lock()
        self._pending = {}
        self._next_id = 0
        self._welcomes = []
        self._welcome_ready = threading.Condition(self._state_lock)
        self._event_handlers = []
        self._closed = False
        self._close_reason = None
        self.welcome = None
        self._thread = threading.Thread(target=self._read_loop, name="sg-transport-reader", daemon=True)
        self._thread.start()

    # ----- sessions -----

    def hello(self, timeout=None):
        """Sends hello and returns the welcome the hub answers with (also kept as `self.welcome`). None when the hub
        closed the stream instead, which is how a warm worker learns it is no longer needed."""
        self._write({"type": "hello", "protocol": PROTOCOL,
                     "client": {"name": self._client, "version": VERSION, "library": f"sg-py/{VERSION}"},
                     "features": FEATURES})
        return self.next_welcome(timeout)

    def next_welcome(self, timeout=None):
        """Waits for the hub's next welcome without sending hello. None on end of stream."""
        with self._welcome_ready:
            while not self._welcomes and not self._closed:
                if not self._welcome_ready.wait(timeout):
                    raise Error("hub_timeout", "the hub sent no welcome in time",
                                "check that the hub is running and speaks the hub protocol")
            if not self._welcomes:
                return None
            welcome = self._welcomes.pop(0)
        if welcome.get("type") != "welcome":
            error = welcome.get("error") or {}
            raise Error(error.get("code", "protocol_error"), error.get("message", "the hub refused hello"),
                        "use a hub of the same release as sg")
        self.welcome = welcome
        return welcome

    # ----- calls -----

    def call(self, method, params=None):
        """Sends a call and blocks until its reply arrives; returns the reply message (a dict with `ok` and `result`
        or `error`). Thread-safe: each call has its own id and its own wait."""
        pending = _Pending()
        with self._state_lock:
            if self._closed:
                raise self._closed_error(maybe_ran=False)
            self._next_id += 1
            call_id = str(self._next_id)
            self._pending[call_id] = pending
        try:
            data = encode({"type": "call", "id": call_id, "method": method, "params": params or {}})
        except (TypeError, ValueError, RecursionError) as error:  # the caller's arguments, not the connection
            with self._state_lock:
                self._pending.pop(call_id, None)
            raise Error("invalid_argument", f"the call's arguments cannot be sent as JSON: {error}",
                        "pass only str, int, finite float, bool, None, lists and dicts") from None
        try:
            self._write_bytes(data)
        except Exception:  # noqa: BLE001 - a closed or broken writer (OSError, ValueError): nothing was sent
            with self._state_lock:
                self._pending.pop(call_id, None)
            raise self._closed_error(maybe_ran=False) from None
        pending.done.wait()
        if pending.reply is None:
            raise self._closed_error(maybe_ran=True)
        return pending.reply

    def send(self, message):
        """Writes a message that has no reply (run_analysis, run_end, stream_end). A message JSON cannot carry raises
        TypeError or ValueError (nothing is written); a closed writer raises sg.Error."""
        data = encode(message)
        try:
            self._write_bytes(data)
        except (OSError, ValueError):
            raise self._closed_error(maybe_ran=False) from None

    def on_event(self, handler):
        """Calls handler(event) for every `event` message (subscriptions, ping). It runs on the reader thread, so it
        must be quick and must not make sg calls itself (their replies come through that same thread): hand the event
        to another thread (a queue.Queue) for anything more."""
        self._event_handlers.append(handler)

    def off_event(self, handler):
        """Removes a handler on_event added, once what it served is over. The reader thread walks a copy of the
        handlers for each event, so removing one never skips another, and a handler already running finishes."""
        if handler in self._event_handlers:
            self._event_handlers.remove(handler)

    # ----- closing -----

    @property
    def closed(self):
        return self._closed

    def close(self):
        """Closes the write side; a child hub then ends its session (hub.md, *Session end*) and exits. Does nothing
        on a transport made with closable=False (a worker's: its connection belongs to the hub)."""
        if not self._closable:
            return
        try:
            self._writer.close()
        except OSError:
            pass
        if self._process is not None:
            try:
                self._process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self._process.kill()
            self._process = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
        return False

    # ----- internals -----

    def _write(self, message):
        self._write_bytes(encode(message))

    def _write_bytes(self, data):
        with self._write_lock:
            self._writer.write(data)
            self._writer.flush()

    def _read_loop(self):
        try:
            while True:
                line = self._reader.readline()
                if not line:
                    break
                line = line.strip()
                if not line:
                    continue
                try:
                    message = json.loads(line.decode("utf-8"))
                except ValueError:
                    continue  # not ours to answer; the hub never sends a broken line
                self._dispatch(message)
        except (OSError, ValueError):
            pass  # the stream broke or was closed under us: the same as end of stream
        finally:
            self._finish("the hub closed the connection")
            if self._on_end_of_stream is not None:
                self._on_end_of_stream()
            try:
                self._reader.close()
            except OSError:
                pass

    def _dispatch(self, message):
        kind = message.get("type")
        if kind == "reply":
            call_id = message.get("id")
            with self._state_lock:
                pending = self._pending.pop(call_id, None) if call_id is not None else None
                if pending is None:
                    if call_id is None and not message.get("ok", True):
                        # A refused hello (unsupported_protocol) has a null id: it answers the session.
                        self._welcomes.append(message)
                        self._welcome_ready.notify_all()
                    return
            pending.reply = message
            pending.done.set()
        elif kind == "welcome":
            with self._welcome_ready:
                # The latest welcome is the session's state: a hub whose game was unreachable at hello sends a fresh one
                # when the mod's welcome first arrives, and its features (subscriptions) are what watch/changes read.
                self.welcome = message
                self._welcomes.append(message)
                self._welcome_ready.notify_all()
        elif kind == "event":
            for handler in list(self._event_handlers):
                try:
                    handler(message)
                except Exception:  # noqa: BLE001 - a handler's bug must not stop the reader
                    pass
        elif kind == "goodbye":
            self._close_reason = f"the hub said goodbye ({message.get('reason')})"

    def _finish(self, reason):
        with self._welcome_ready:
            self._closed = True
            if self._close_reason is None:
                self._close_reason = reason
            pending = list(self._pending.values())
            self._pending.clear()
            self._welcome_ready.notify_all()
        for waiting in pending:
            waiting.done.set()  # reply stays None: the caller raises

    def _closed_error(self, maybe_ran):
        return Error("unreachable", self._close_reason or "the hub connection is closed",
                     "start a new connection (sg.connect())", maybe_ran)


# ----- the child hub -----

def find_hub(hub_path=None):
    """The hub program: the argument, else STATIONGODMCP_HUB, else StationGodMCP.Server on PATH."""
    for candidate in (hub_path, os.environ.get(HUB_ENVIRONMENT_VARIABLE)):
        if candidate:
            return candidate
    found = shutil.which(HUB_PROGRAM)
    if found:
        return found
    raise Error("hub_not_found", f"no hub to start: pass hub_path, set {HUB_ENVIRONMENT_VARIABLE}, "
                f"or put {HUB_PROGRAM} on PATH", f"sg.connect(r'C:\\path\\to\\{HUB_PROGRAM}.exe')")


def connect(hub_path=None, *hub_args, client="sg-py"):
    """Starts a child hub (`<hub> --hub <hub_args>`), says hello and returns the Transport. hub_args are passed on
    as they are (for example "--pipe", "StationGodMCP-Test"). A path ending in .dll is run with `dotnet`."""
    program = find_hub(hub_path)
    command = ["dotnet", program] if program.lower().endswith(".dll") else [program]
    command += ["--hub", *hub_args]
    job = _KillJob.create()  # made before the child starts, so assigning it right after leaves the shortest gap
    try:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, bufsize=0)
    except BaseException:
        if job is not None:
            job.close()
        raise
    if job is not None and not job.assign(process.pid):
        job = None
    transport = Transport(process.stdout, process.stdin, client=client, process=process)
    transport._job = job
    try:
        welcome = transport.hello(timeout=HELLO_TIMEOUT_S)
    except BaseException:
        process.kill()
        transport.close()
        raise
    if welcome is None:
        code = process.poll()
        process.kill()
        transport.close()
        raise Error("unreachable", f"the hub exited before its welcome (exit code {code})",
                    "run the hub by hand with --hub to see why")
    atexit.register(transport.close)
    return transport


class _KillJob:
    """A Windows Job Object with kill-on-close. The handle is kept open for the life of this process and never
    closed: when this process ends, however it ends, Windows closes the handle and kills every process in the job.
    Elsewhere closing stdin at exit (atexit) is the whole mechanism, and the hub exits on stdin EOF.

    A child that starts processes of its own before it is assigned leaves those outside the job; the gap is two
    system calls long (the hub starts its workers much later)."""

    def __init__(self, kernel32, handle):
        self._kernel32 = kernel32
        self.handle = handle

    @classmethod
    def create(cls):
        """The job, or None off Windows or when it cannot be made."""
        if sys.platform != "win32":
            return None
        try:
            return cls._create()
        except OSError:
            return None  # no job: stdin EOF still ends the child

    @classmethod
    def _create(cls):
        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

        class IoCounters(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in (
                "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class BasicLimits(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong), ("PerJobUserTimeLimit", ctypes.c_longlong),
                        ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                        ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                        ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD),
                        ("SchedulingClass", wintypes.DWORD)]

        class ExtendedLimits(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", BasicLimits), ("IoInfo", IoCounters),
                        ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                        ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        job_object_extended_limit_information = 9
        job_object_limit_kill_on_job_close = 0x2000
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

        handle = kernel32.CreateJobObjectW(None, None)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = job_object_limit_kill_on_job_close
        if not kernel32.SetInformationJobObject(handle, job_object_extended_limit_information,
                                                ctypes.byref(limits), ctypes.sizeof(limits)):
            error = ctypes.get_last_error()
            kernel32.CloseHandle(handle)
            raise ctypes.WinError(error)
        return cls(kernel32, handle)

    def close(self):
        """Frees the job (only when no child was put in it: closing it kills its processes)."""
        self._kernel32.CloseHandle(self.handle)
        self.handle = None

    def assign(self, pid):
        """Puts the process in the job; False when Windows refuses."""
        process_set_quota, process_terminate = 0x0100, 0x0001
        process = self._kernel32.OpenProcess(process_set_quota | process_terminate, False, pid)
        if not process:
            return False
        try:
            return bool(self._kernel32.AssignProcessToJobObject(self.handle, process))
        finally:
            self._kernel32.CloseHandle(process)
