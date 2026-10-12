"""Windows named pipes with overlapped I/O, through ctypes.

A handle opened for overlapped I/O lets one thread wait in ReadFile while another writes: version 2 of the protocol
needs that, because replies, pings and events arrive while the client may be sending (protocol.md, Transports). A
handle opened synchronously, as the dashboard's transport opens it today, serialises the two.

PipeHandle wraps a connected handle (client or server end); connect() opens the client end; PipeListener creates
server instances, which the tests use to play the mod.
"""
import ctypes
import threading
import time
from ctypes import wintypes

PIPE_PREFIX = "\\\\.\\pipe\\"

ERROR_FILE_NOT_FOUND = 2
ERROR_INVALID_HANDLE = 6
ERROR_BROKEN_PIPE = 109
ERROR_SEM_TIMEOUT = 121
ERROR_PIPE_BUSY = 231
ERROR_NO_DATA = 232
ERROR_PIPE_NOT_CONNECTED = 233
ERROR_MORE_DATA = 234
ERROR_PIPE_CONNECTED = 535
ERROR_OPERATION_ABORTED = 995
ERROR_IO_PENDING = 997

_ENDED = frozenset({ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_NOT_CONNECTED, ERROR_OPERATION_ABORTED,
                    ERROR_INVALID_HANDLE})

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
PIPE_ACCESS_DUPLEX = 0x00000003
PIPE_TYPE_BYTE = 0x00000000
PIPE_READMODE_BYTE = 0x00000000
PIPE_WAIT = 0x00000000
PIPE_UNLIMITED_INSTANCES = 255
INFINITE = 0xFFFFFFFF
WAIT_OBJECT_0 = 0


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_void_p), ("InternalHigh", ctypes.c_void_p), ("Offset", wintypes.DWORD),
                ("OffsetHigh", wintypes.DWORD), ("hEvent", wintypes.HANDLE)]


_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


def _function(name, restype, *argtypes):
    function = getattr(_kernel32, name)
    function.argtypes = list(argtypes)
    function.restype = restype
    return function


_LPOVERLAPPED = ctypes.POINTER(OVERLAPPED)
_LPDWORD = ctypes.POINTER(wintypes.DWORD)
_CreateFileW = _function("CreateFileW", wintypes.HANDLE, wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                         ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE)
_CreateNamedPipeW = _function("CreateNamedPipeW", wintypes.HANDLE, wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                              wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p)
_ConnectNamedPipe = _function("ConnectNamedPipe", wintypes.BOOL, wintypes.HANDLE, _LPOVERLAPPED)
_WaitNamedPipeW = _function("WaitNamedPipeW", wintypes.BOOL, wintypes.LPCWSTR, wintypes.DWORD)
_ReadFile = _function("ReadFile", wintypes.BOOL, wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, _LPDWORD,
                      _LPOVERLAPPED)
_WriteFile = _function("WriteFile", wintypes.BOOL, wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, _LPDWORD,
                       _LPOVERLAPPED)
_GetOverlappedResult = _function("GetOverlappedResult", wintypes.BOOL, wintypes.HANDLE, _LPOVERLAPPED, _LPDWORD,
                                 wintypes.BOOL)
_CancelIoEx = _function("CancelIoEx", wintypes.BOOL, wintypes.HANDLE, _LPOVERLAPPED)
_CreateEventW = _function("CreateEventW", wintypes.HANDLE, ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL,
                          wintypes.LPCWSTR)
_ResetEvent = _function("ResetEvent", wintypes.BOOL, wintypes.HANDLE)
_WaitForSingleObject = _function("WaitForSingleObject", wintypes.DWORD, wintypes.HANDLE, wintypes.DWORD)
_CloseHandle = _function("CloseHandle", wintypes.BOOL, wintypes.HANDLE)
INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value


class PipeMissing(OSError):
    """No pipe of that name: the game is not running, not hosting, or no save is loaded."""


class PipeBusy(OSError):
    """Every instance of the pipe is taken by other clients."""


def full_name(name):
    return name if name.startswith(PIPE_PREFIX) else PIPE_PREFIX + name


def _error(code, what):
    return OSError(code, f"{what}: {ctypes.FormatError(code).strip()} (Windows error {code})")


class _Event:
    def __init__(self):
        self.handle = _CreateEventW(None, True, False, None)
        if not self.handle:
            raise _error(ctypes.get_last_error(), "CreateEventW")

    def close(self):
        if self.handle:
            _CloseHandle(self.handle)
            self.handle = None


class PipeHandle:
    """One connected pipe end opened for overlapped I/O. read() and write() may run at the same time on different
    threads; close() from any thread cancels both and makes read() return b''."""

    def __init__(self, handle):
        self._handle = handle
        self._read_lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._read_event = _Event()
        self._write_event = _Event()
        self._closed = False
        self._close_lock = threading.Lock()

    @property
    def closed(self):
        return self._closed

    def read(self, size=65536):
        """The next bytes that arrive, at most size; b'' once the other end closed or close() was called."""
        with self._read_lock:
            if self._closed:
                return b""
            buffer = ctypes.create_string_buffer(size)
            count = self._run(_ReadFile, buffer, size, self._read_event, "ReadFile")
            return b"" if count is None else buffer.raw[:count]

    def write(self, data):
        """Writes every byte of data; OSError when the pipe is closed or broken."""
        view = memoryview(bytes(data))
        with self._write_lock:
            while len(view):
                if self._closed:
                    raise OSError(ERROR_BROKEN_PIPE, "the pipe is closed")
                chunk = ctypes.create_string_buffer(view.tobytes(), len(view))
                count = self._run(_WriteFile, chunk, len(view), self._write_event, "WriteFile")
                if count is None:
                    raise OSError(ERROR_BROKEN_PIPE, "the pipe was closed by the other end")
                view = view[count:]

    def _run(self, operation, buffer, size, event, what):
        """One overlapped ReadFile or WriteFile, waited for; the byte count, or None when the pipe has ended."""
        overlapped = OVERLAPPED()
        overlapped.hEvent = event.handle
        _ResetEvent(event.handle)
        transferred = wintypes.DWORD(0)
        if not operation(self._handle, buffer, size, None, ctypes.byref(overlapped)):
            code = ctypes.get_last_error()
            if code in _ENDED:
                return None
            if code not in (ERROR_IO_PENDING, ERROR_MORE_DATA):
                raise _error(code, what)
        if self._closed:
            # close() ran between our check and the call; its CancelIoEx may have come before this operation began.
            _CancelIoEx(self._handle, ctypes.byref(overlapped))
        # The OVERLAPPED stays alive in this frame until the operation completes, cancelled or not.
        if not _GetOverlappedResult(self._handle, ctypes.byref(overlapped), ctypes.byref(transferred), True):
            code = ctypes.get_last_error()
            if code == ERROR_MORE_DATA:
                return transferred.value
            if code in _ENDED:
                return None
            raise _error(code, what)
        return transferred.value

    def close(self):
        with self._close_lock:
            if self._closed:
                return
            self._closed = True
            _CancelIoEx(self._handle, None)
        # Wait for a read or write in progress to see the cancel before the handle and events go.
        with self._read_lock, self._write_lock:
            _CloseHandle(self._handle)
            self._read_event.close()
            self._write_event.close()


def connect(name, timeout):
    """Opens the client end of \\\\.\\pipe\\<name> for overlapped I/O. A busy pipe and a missing one are both retried
    until timeout (the mod recreates an instance after each connection, so it is briefly absent); the error then says
    which it was."""
    path = full_name(name)
    deadline = time.monotonic() + timeout
    while True:
        handle = _CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, None, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, None)
        if handle != INVALID_HANDLE_VALUE and handle:
            return PipeHandle(handle)
        code = ctypes.get_last_error()
        if code not in (ERROR_FILE_NOT_FOUND, ERROR_PIPE_BUSY):
            raise _error(code, f"cannot open {path}")
        if time.monotonic() >= deadline:
            if code == ERROR_PIPE_BUSY:
                raise PipeBusy(code, f"the pipe {path} stayed busy (every instance is taken by other clients)")
            raise PipeMissing(code, f"no pipe {path}: the game is not running, not hosting, or no save is loaded")
        if code == ERROR_PIPE_BUSY:
            _WaitNamedPipeW(path, 100)
        else:
            time.sleep(0.005)


class PipeListener:
    """The server end, for tests and probes: accept() creates an instance, waits for a client and returns its
    PipeHandle. close() stops an accept() in progress."""

    def __init__(self, name):
        self.path = full_name(name)
        self._closed = False
        self._pending = None
        self._lock = threading.Lock()

    def accept(self):
        handle = _CreateNamedPipeW(self.path, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                   PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES,
                                   65536, 65536, 0, None)
        if handle == INVALID_HANDLE_VALUE or not handle:
            raise _error(ctypes.get_last_error(), f"CreateNamedPipeW {self.path}")
        event = _Event()
        overlapped = OVERLAPPED()
        overlapped.hEvent = event.handle
        try:
            with self._lock:
                if self._closed:
                    raise OSError(ERROR_OPERATION_ABORTED, "the listener is closed")
                self._pending = handle
            if not _ConnectNamedPipe(handle, ctypes.byref(overlapped)):
                code = ctypes.get_last_error()
                if code == ERROR_IO_PENDING:
                    transferred = wintypes.DWORD(0)
                    if not _GetOverlappedResult(handle, ctypes.byref(overlapped), ctypes.byref(transferred), True):
                        raise _error(ctypes.get_last_error(), "ConnectNamedPipe")
                elif code != ERROR_PIPE_CONNECTED:
                    raise _error(code, "ConnectNamedPipe")
            with self._lock:
                self._pending = None
                if self._closed:
                    raise OSError(ERROR_OPERATION_ABORTED, "the listener is closed")
            return PipeHandle(handle)
        except BaseException:
            with self._lock:
                self._pending = None
            _CloseHandle(handle)
            raise
        finally:
            event.close()

    def close(self):
        with self._lock:
            self._closed = True
            if self._pending is not None:
                _CancelIoEx(self._pending, None)
