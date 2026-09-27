#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StationGodMCP;

/// <summary>
/// The local named pipe. Several server instances listen at once, so the MCP sidecar and other local clients (the
/// script dashboard) do not queue behind one another. A connection is kept for as long as the client wants it: one
/// reply line per request line until the client closes, as the TCP server does; a client that sends one line, reads
/// one reply and closes works exactly as before. A client that connects and sends nothing within the first-line
/// timeout is dropped, so it cannot hold an instance. Nothing is logged per request: only listening, failures (at
/// most one line a minute per instance, with a count of the ones held back) and recovery.
/// </summary>
internal sealed class StationGodPipeServer : IDisposable
{
    private const int InstanceCount = 4;
    private const int RequestTimeoutMilliseconds = 30000;
    private const int JoinMilliseconds = 1000;
    private const int BufferSize = 4096;

    private readonly string _pipeName;
    private readonly StationGodRequestDispatcher _dispatcher;
    private readonly object _pipeLock = new object();
    private readonly List<Thread> _listenerThreads = new List<Thread>();
    private readonly HashSet<NamedPipeServerStream> _activePipes = new HashSet<NamedPipeServerStream>();
    private volatile bool _stopping;

    internal StationGodPipeServer(string pipeName, StationGodRequestDispatcher dispatcher)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
    }

    internal void Start()
    {
        if (_listenerThreads.Count > 0)
        {
            return;
        }

        for (int instance = 1; instance <= InstanceCount; instance++)
        {
            Thread thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = $"StationGodMCP named-pipe listener {instance}"
            };
            _listenerThreads.Add(thread);
            thread.Start(instance);
        }

        StationGodMod.Log(
            $"Authoritative MCP bridge listening on \\\\.\\pipe\\{_pipeName} ({InstanceCount} instances).");
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_pipeLock)
        {
            foreach (NamedPipeServerStream pipe in _activePipes)
            {
                FirstLineWatchdog.Close(pipe);
            }

            _activePipes.Clear();
        }

        foreach (Thread thread in _listenerThreads)
        {
            if (thread.IsAlive)
            {
                thread.Join(JoinMilliseconds);
            }
        }

        _listenerThreads.Clear();
    }

    private void ListenLoop(object? state)
    {
        PipeFailures failures = new PipeFailures((int)state!);
        bool listening = true;
        while (listening && !_stopping)
        {
            listening = ListenOnce(failures);
        }
    }

    // One instance's wait for a client and the connection that follows. False when the server is stopping.
    private bool ListenOnce(PipeFailures failures)
    {
        NamedPipeServerStream? pipe = null;
        try
        {
            pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, InstanceCount, PipeTransmissionMode.Byte,
                PipeOptions.None);
            if (!Track(pipe))
            {
                return false;
            }

            pipe.WaitForConnection();
            if (_stopping)
            {
                return false;
            }

            failures.Recovered();
            Serve(pipe);
            return true;
        }
        catch (Exception exception) when (_stopping &&
                                          (exception is ObjectDisposedException || exception is IOException))
        {
            return false;
        }
        catch (Exception exception)
        {
            // NamedPipeServerStream's constructor or WaitForConnection failing: log, back off, listen again.
            if (_stopping)
            {
                return false;
            }

            failures.Failed(exception);
            return true;
        }
        finally
        {
            Untrack(pipe);
        }
    }

    private bool Track(NamedPipeServerStream pipe)
    {
        lock (_pipeLock)
        {
            if (_stopping)
            {
                return false;
            }

            _activePipes.Add(pipe);
            return true;
        }
    }

    private void Untrack(NamedPipeServerStream? pipe)
    {
        if (pipe == null)
        {
            return;
        }

        lock (_pipeLock)
        {
            _activePipes.Remove(pipe);
        }

        FirstLineWatchdog.Close(pipe);
    }

    // One connection: a reply line for each request line until the client closes. Only the first line is timed; a
    // client that keeps the connection open between requests does so on purpose. A client going away is the normal
    // end of a connection, not an error.
    private void Serve(NamedPipeServerStream pipe)
    {
        try
        {
            // Inside the try: disposing the writer flushes, which throws once the client has closed its end.
            using StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), true, BufferSize, true);
            using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), BufferSize, true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            string? requestJson = FirstLineWatchdog.ReadFirstLine(pipe, reader);
            while (!_stopping && requestJson != null)
            {
                if (!string.IsNullOrWhiteSpace(requestJson))
                {
                    writer.WriteLine(_dispatcher.Dispatch(requestJson, RequestTimeoutMilliseconds));
                }

                requestJson = reader.ReadLine();
            }
        }
        catch (IOException)
        {
            // The client closed its end, or sent nothing in time: the normal end of a connection.
        }
        catch (ObjectDisposedException)
        {
            // The server is stopping and disposed the pipe.
        }
        catch (InvalidOperationException)
        {
            // PipeStream reports a broken pipe this way on some runtimes.
        }
    }
}

/// <summary>One pipe instance's failures: logged at most once a minute with a count held back; backed off.</summary>
internal sealed class PipeFailures
{
    private const int FirstBackoffMilliseconds = 100;
    private const int MaximumBackoffMilliseconds = 5000;
    private const int MaximumDoublings = 10;
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private readonly int _instance;
    private int _consecutive;
    private int _suppressed;
    private DateTime _lastLog = DateTime.MinValue;

    internal PipeFailures(int instance)
    {
        _instance = instance;
    }

    internal void Recovered()
    {
        if (_consecutive > 0)
        {
            StationGodMod.Log($"Named-pipe instance {_instance} recovered after {_consecutive} failure(s).");
            _consecutive = 0;
            _suppressed = 0;
        }
    }

    // A constructor or connection that keeps failing must not spin: back off 100 ms, doubling to 5 s.
    internal void Failed(Exception exception)
    {
        _consecutive++;
        DateTime now = DateTime.UtcNow;
        if (now - _lastLog >= LogInterval)
        {
            string held = _suppressed > 0 ? $" ({_suppressed} similar failure(s) not logged)" : string.Empty;
            StationGodMod.LogWarning($"Named-pipe instance {_instance} failed: {exception.Message}{held}");
            _lastLog = now;
            _suppressed = 0;
        }
        else
        {
            _suppressed++;
        }

        long backoff = (long)FirstBackoffMilliseconds << Math.Min(_consecutive - 1, MaximumDoublings);
        Thread.Sleep((int)Math.Min(MaximumBackoffMilliseconds, backoff));
    }
}

/// <summary>
/// A synchronous pipe read cannot time out, and disposing the stream does not reliably wake a thread blocked in
/// ReadFile, so a timer cancels the reading thread's synchronous I/O (CancelSynchronousIo) and closes the pipe if the
/// first line has not arrived in time. The read then fails with an IOException and the instance is freed.
/// </summary>
internal sealed class FirstLineWatchdog
{
    private const int TimeoutMilliseconds = 10000;

    // THREAD_TERMINATE is the access right CancelSynchronousIo requires.
    private const uint ThreadTerminate = 0x0001;

    private readonly NamedPipeServerStream _pipe;
    private readonly IntPtr _thread;
    private int _finished;

    private FirstLineWatchdog(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
        _thread = OpenThread(ThreadTerminate, false, GetCurrentThreadId());
    }

    internal static string? ReadFirstLine(NamedPipeServerStream pipe, StreamReader reader)
    {
        FirstLineWatchdog watchdog = new FirstLineWatchdog(pipe);
        Timer timer = new Timer(watchdog.Fire, null, TimeoutMilliseconds, Timeout.Infinite);
        try
        {
            string? line = reader.ReadLine();
            if (Interlocked.CompareExchange(ref watchdog._finished, 1, 0) != 0)
            {
                throw new IOException("The client sent nothing within the first-line timeout.");
            }

            return line;
        }
        finally
        {
            Interlocked.Exchange(ref watchdog._finished, 1);
            watchdog.Close(timer);
        }
    }

    private void Fire(object? state)
    {
        if (Interlocked.CompareExchange(ref _finished, 1, 0) != 0)
        {
            return;
        }

        if (_thread != IntPtr.Zero)
        {
            CancelSynchronousIo(_thread);
        }

        Close(_pipe);
    }

    /// <summary>
    /// Disposes a pipe without throwing: this runs on timer and listener threads, where an exception would end the
    /// thread or the process.
    /// </summary>
    internal static void Close(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Dispose();
        }
        catch (Exception)
        {
            // NamedPipeServerStream.Dispose on a pipe whose client broke it mid-write: it is closed either way.
        }
    }

    // Waits for a callback already running to finish before the thread handle it uses is closed.
    private void Close(Timer timer)
    {
        using (ManualResetEvent disposed = new ManualResetEvent(false))
        {
            if (timer.Dispose(disposed))
            {
                disposed.WaitOne();
            }
        }

        if (_thread != IntPtr.Zero)
        {
            CloseHandle(_thread);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelSynchronousIo(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
