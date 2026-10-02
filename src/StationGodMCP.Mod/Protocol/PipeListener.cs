#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace StationGodMCP.Protocol;

/// <summary>
/// The local named pipe with overlapped instances. One thread keeps a few instances waiting for clients (at most
/// SpareInstances, and never more instances than MaxPipeConnections in all, which Windows itself enforces); each client
/// that connects becomes a Connection with its own reader and writer. When every instance is in use a new client waits
/// (the pipe is busy) until a connection ends.
/// </summary>
internal sealed class PipeListener : IDisposable
{
    private const int SpareInstances = 4;
    private const int RetryMilliseconds = 1000;
    private const int StopWaitMilliseconds = 2000;

    private readonly string _pipeName;
    private readonly ProtocolHost _host;
    private readonly IntPtr _stop;
    private readonly IntPtr _slotFreed;
    private readonly List<NativePipe> _waiting = new List<NativePipe>();
    private Thread? _thread;
    private volatile bool _stopping;
    private int _failures;
    private DateTime _lastFailureLog = DateTime.MinValue;

    internal PipeListener(string pipeName, ProtocolHost host)
    {
        _pipeName = pipeName;
        _host = host;
        _stop = Kernel32.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        _slotFreed = Kernel32.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        _host.ConnectionEnded += OnConnectionEnded;
    }

    /// <summary>Raised on the listener thread for each client connected, before its reader starts (tests use it).</summary>
    internal event Action<Connection>? Connected;

    internal void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(Listen) { IsBackground = true, Name = "StationGodMCP named-pipe listener" };
        _thread.Start();
        ProtocolLog.Info($"Authoritative MCP bridge listening on \\\\.\\pipe\\{_pipeName} " +
                         $"(overlapped, up to {_host.Settings.MaxPipeConnections} connections).");
    }

    public void Dispose()
    {
        _stopping = true;
        Kernel32.SetEvent(_stop);
        if (_thread != null && _thread.IsAlive)
        {
            _thread.Join(StopWaitMilliseconds);
        }

        _host.CloseAll(StopWaitMilliseconds);
        _host.ConnectionEnded -= OnConnectionEnded;
        if (_thread == null || !_thread.IsAlive)
        {
            Kernel32.CloseHandle(_stop);
            Kernel32.CloseHandle(_slotFreed);
        }

        _thread = null;
    }

    private void OnConnectionEnded(Connection connection) => Kernel32.SetEvent(_slotFreed);

    private void Listen()
    {
        try
        {
            while (!_stopping)
            {
                bool busy = FillSpares();
                IntPtr[] handles = new IntPtr[2 + _waiting.Count];
                handles[0] = _stop;
                handles[1] = _slotFreed;
                for (int index = 0; index < _waiting.Count; index++)
                {
                    handles[2 + index] = _waiting[index].ConnectEvent;
                }

                uint waited = Kernel32.WaitForMultipleObjects((uint)handles.Length, handles, false,
                    busy || _waiting.Count == 0 ? RetryMilliseconds : Kernel32.Infinite);
                if (waited == Kernel32.WaitObject0 || _stopping)
                {
                    return;
                }

                AcceptReady();
            }
        }
        catch (Exception exception)
        {
            // The listener thread itself failing: logged; the mod's next start makes a new listener.
            ProtocolLog.Warning($"The named-pipe listener stopped: {exception.Message}");
        }
        finally
        {
            foreach (NativePipe pipe in _waiting)
            {
                pipe.AbandonConnect();
                pipe.Dispose();
            }

            _waiting.Clear();
        }
    }

    // Tops the waiting instances up to SpareInstances. True when an instance could not be made (all in use, or a
    // failure), so the wait should end in time to try again.
    private bool FillSpares()
    {
        while (_waiting.Count < SpareInstances)
        {
            NativePipe? pipe = NativePipe.Create(_pipeName, _host.Settings.MaxPipeConnections, out int error);
            if (pipe == null)
            {
                if (error != Kernel32.ErrorPipeBusy)
                {
                    Failed($"creating an instance failed ({error})");
                }

                return true;
            }

            bool connected;
            try
            {
                connected = pipe.BeginConnect();
            }
            catch (Exception exception)
            {
                // ConnectNamedPipe failing on a fresh instance: dropped, and tried again on the next round.
                pipe.Dispose();
                Failed(exception.Message);
                return true;
            }

            if (connected)
            {
                Hand(pipe);
            }
            else
            {
                _waiting.Add(pipe);
            }
        }

        return false;
    }

    private void AcceptReady()
    {
        for (int index = _waiting.Count - 1; index >= 0; index--)
        {
            NativePipe pipe = _waiting[index];
            if (Kernel32.WaitForMultipleObjects(1, new[] { pipe.ConnectEvent }, false, 0) != Kernel32.WaitObject0)
            {
                continue;
            }

            _waiting.RemoveAt(index);
            if (pipe.EndConnect())
            {
                Hand(pipe);
            }
            else
            {
                pipe.Dispose();
            }
        }
    }

    private void Hand(NativePipe pipe)
    {
        if (_failures > 0)
        {
            ProtocolLog.Info($"The named pipe recovered after {_failures} failure(s).");
            _failures = 0;
        }

        Connection connection = new Connection(pipe, _host);
        Connected?.Invoke(connection);
        connection.Start();
    }

    // At most one log line a minute; a failure that repeats must not flood the log.
    private void Failed(string reason)
    {
        _failures++;
        DateTime now = DateTime.UtcNow;
        if (now - _lastFailureLog >= TimeSpan.FromMinutes(1))
        {
            ProtocolLog.Warning($"Named pipe: {reason} ({_failures} failure(s) so far).");
            _lastFailureLog = now;
        }
    }
}
