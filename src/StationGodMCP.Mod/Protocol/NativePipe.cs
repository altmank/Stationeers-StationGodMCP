#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace StationGodMCP.Protocol;

/// <summary>
/// One server instance of the named pipe, created through the Windows API for overlapped I/O, so one thread can wait
/// in a read while another writes: what a synchronous pipe handle cannot do. Reads and writes each have their own
/// OVERLAPPED and event in unmanaged memory; Cancel wakes both, and each cancels its own pending operation and waits
/// for it to finish before returning, so no operation outlives the memory it uses. Dispose only after the threads that
/// read and write have returned.
/// </summary>
internal sealed class NativePipe : IByteTransport
{
    private const int ReadBufferSize = 65536;

    private readonly IntPtr _handle;
    private readonly Operation _read;
    private readonly Operation _write;
    private readonly IntPtr _stop;
    private readonly IntPtr _readBuffer;
    private readonly object _lifetime = new object();
    private bool _disposed;

    private NativePipe(IntPtr handle)
    {
        _handle = handle;
        _read = new Operation();
        _write = new Operation();
        _stop = Kernel32.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        _readBuffer = Marshal.AllocHGlobal(ReadBufferSize);
    }

    public string Kind => "pipe";

    /// <summary>A new listening instance, or null with the Windows error (ERROR_PIPE_BUSY when all are in use).</summary>
    internal static NativePipe? Create(string pipeName, int maximumInstances, out int error)
    {
        IntPtr handle = Kernel32.CreateNamedPipeW(@"\\.\pipe\" + pipeName,
            Kernel32.PipeAccessDuplex | Kernel32.FileFlagOverlapped,
            Kernel32.PipeTypeByte | Kernel32.PipeReadModeByte | Kernel32.PipeWait | Kernel32.PipeRejectRemoteClients,
            (uint)maximumInstances, ReadBufferSize, ReadBufferSize, 0, IntPtr.Zero);
        if (handle == Kernel32.InvalidHandle)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        error = 0;
        return new NativePipe(handle);
    }

    /// <summary>The event that is set when BeginConnect's client has connected.</summary>
    internal IntPtr ConnectEvent => _read.Event;

    /// <summary>
    /// Starts waiting for a client. True when one is already connected; false when the wait is pending (ConnectEvent
    /// is set when it ends). Throws on any other failure.
    /// </summary>
    internal bool BeginConnect()
    {
        _read.Reset();
        if (Kernel32.ConnectNamedPipe(_handle, _read.Overlapped))
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        return error switch
        {
            Kernel32.ErrorPipeConnected => true,
            Kernel32.ErrorIoPending => false,
            _ => throw new IOException($"ConnectNamedPipe failed ({error}).")
        };
    }

    /// <summary>After ConnectEvent is set: true when a client is connected.</summary>
    internal bool EndConnect() => Kernel32.GetOverlappedResult(_handle, _read.Overlapped, out _, false);

    /// <summary>Stops a pending BeginConnect and waits until it has ended.</summary>
    internal void AbandonConnect()
    {
        Kernel32.CancelIoEx(_handle, _read.Overlapped);
        Kernel32.GetOverlappedResult(_handle, _read.Overlapped, out _, true);
    }

    public int Read(byte[] buffer, int timeoutMilliseconds)
    {
        int count = Math.Min(buffer.Length, ReadBufferSize);
        while (true)
        {
            _read.Reset();
            bool done = Kernel32.ReadFile(_handle, _readBuffer, (uint)count, IntPtr.Zero, _read.Overlapped);
            int error = done ? 0 : Marshal.GetLastWin32Error();
            if (!done && error != Kernel32.ErrorIoPending)
            {
                return Ended(error) ? 0 : throw new IOException($"ReadFile failed ({error}).");
            }

            uint waited = Kernel32.WaitForMultipleObjects(2, new[] { _read.Event, _stop }, false,
                timeoutMilliseconds < 0 ? Kernel32.Infinite : (uint)timeoutMilliseconds);
            bool completed = waited == Kernel32.WaitObject0;
            if (!completed)
            {
                Kernel32.CancelIoEx(_handle, _read.Overlapped);
            }

            bool ok = Kernel32.GetOverlappedResult(_handle, _read.Overlapped, out uint read, true);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok && read > 0)
            {
                Marshal.Copy(_readBuffer, buffer, 0, (int)read);
                return (int)read;
            }

            if (!completed)
            {
                return waited == Kernel32.WaitTimeout ? throw new TimeoutException() : Cancelled;
            }

            if (!ok)
            {
                return Ended(error) ? 0 : throw new IOException($"Reading the pipe failed ({error}).");
            }
        }
    }

    /// <summary>Read's answer when Cancel woke it.</summary>
    internal const int Cancelled = -1;

    public WriteResult Write(byte[] buffer, int count, int timeoutMilliseconds)
    {
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            int offset = 0;
            while (offset < count)
            {
                _write.Reset();
                IntPtr from = pinned.AddrOfPinnedObject() + offset;
                bool done = Kernel32.WriteFile(_handle, from, (uint)(count - offset), IntPtr.Zero, _write.Overlapped);
                int error = done ? 0 : Marshal.GetLastWin32Error();
                if (!done && error != Kernel32.ErrorIoPending)
                {
                    return WriteResult.Closed;
                }

                uint waited = Kernel32.WaitForMultipleObjects(2, new[] { _write.Event, _stop }, false,
                    timeoutMilliseconds < 0 ? Kernel32.Infinite : (uint)timeoutMilliseconds);
                if (waited != Kernel32.WaitObject0)
                {
                    Kernel32.CancelIoEx(_handle, _write.Overlapped);
                }

                bool ok = Kernel32.GetOverlappedResult(_handle, _write.Overlapped, out uint written, true);
                if (waited == Kernel32.WaitTimeout)
                {
                    return WriteResult.TimedOut;
                }

                if (!ok || waited != Kernel32.WaitObject0)
                {
                    return WriteResult.Closed;
                }

                offset += (int)written;
            }

            return WriteResult.Written;
        }
        finally
        {
            pinned.Free();
        }
    }

    public void Cancel()
    {
        lock (_lifetime)
        {
            if (!_disposed)
            {
                Kernel32.SetEvent(_stop);
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Kernel32.CloseHandle(_handle);
            Kernel32.CloseHandle(_stop);
            _read.Dispose();
            _write.Dispose();
            Marshal.FreeHGlobal(_readBuffer);
        }
    }

    private static bool Ended(int error) =>
        error == Kernel32.ErrorBrokenPipe || error == Kernel32.ErrorPipeNotConnected || error == Kernel32.ErrorNoData;

    /// <summary>An OVERLAPPED in unmanaged memory and its manual-reset event.</summary>
    private sealed class Operation : IDisposable
    {
        private static readonly int Size = 2 * IntPtr.Size + 8 + IntPtr.Size;
        private static readonly int EventOffset = 2 * IntPtr.Size + 8;

        internal Operation()
        {
            Overlapped = Marshal.AllocHGlobal(Size);
            Event = Kernel32.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            Reset();
        }

        internal IntPtr Overlapped { get; }

        internal IntPtr Event { get; }

        internal void Reset()
        {
            for (int offset = 0; offset < EventOffset; offset += 4)
            {
                Marshal.WriteInt32(Overlapped, offset, 0);
            }

            Marshal.WriteIntPtr(Overlapped, EventOffset, Event);
            Kernel32.ResetEvent(Event);
        }

        public void Dispose()
        {
            Kernel32.CloseHandle(Event);
            Marshal.FreeHGlobal(Overlapped);
        }
    }
}

/// <summary>The Windows calls the overlapped pipe uses.</summary>
internal static class Kernel32
{
    internal const uint PipeAccessDuplex = 0x00000003;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const uint PipeTypeByte = 0x00000000;
    internal const uint PipeReadModeByte = 0x00000000;
    internal const uint PipeWait = 0x00000000;
    internal const uint PipeRejectRemoteClients = 0x00000008;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 0x102;
    internal const int ErrorBrokenPipe = 109;
    internal const int ErrorNoData = 232;
    internal const int ErrorPipeBusy = 231;
    internal const int ErrorPipeConnected = 535;
    internal const int ErrorPipeNotConnected = 233;
    internal const int ErrorIoPending = 997;
    internal static readonly IntPtr InvalidHandle = new IntPtr(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ConnectNamedPipe(IntPtr pipe, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(IntPtr file, IntPtr buffer, uint count, IntPtr read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(IntPtr file, IntPtr buffer, uint count, IntPtr written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);
}
