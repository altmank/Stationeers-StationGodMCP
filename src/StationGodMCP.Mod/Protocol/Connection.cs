#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>A byte stream both ends may read and write at once (an overlapped pipe instance, a socket).</summary>
internal interface IByteTransport : IDisposable
{
    /// <summary>"pipe" or "tcp".</summary>
    string Kind { get; }

    /// <summary>
    /// Bytes read into buffer; 0 when the other end closed; NativePipe.Cancelled after Cancel. Throws TimeoutException
    /// when nothing came within timeoutMilliseconds (negative: no limit).
    /// </summary>
    int Read(byte[] buffer, int timeoutMilliseconds);

    /// <summary>Writes all count bytes; false when the connection is gone, Cancel was called, or the time ran out.</summary>
    bool Write(byte[] buffer, int count, int timeoutMilliseconds);

    /// <summary>Wakes a waiting Read or Write; safe from any thread, more than once, and after Dispose.</summary>
    void Cancel();
}

/// <summary>The log lines the protocol layer writes; the mod points them at its log, tests at nothing.</summary>
internal static class ProtocolLog
{
    internal static Action<string> InfoSink { get; set; } = static _ => { };

    internal static Action<string> WarningSink { get; set; } = static _ => { };

    /// <summary>A reply's method and size once written (mod_info's reply_bytes).</summary>
    internal static Action<string?, long> ReplyWritten { get; set; } = static (_, _) => { };

    internal static void Info(string message) => InfoSink(message);

    internal static void Warning(string message) => WarningSink(message);
}

/// <summary>
/// One client connection. A reader thread reads lines and hands them to the connection's session (version 1 today);
/// a writer thread writes whatever is queued for the client, in order, so replies are written while a read waits.
/// The reader owns the connection's life: when it ends it stops the writer, waits for it, and frees the transport.
/// </summary>
internal sealed class Connection
{
    private const int ReadChunk = 65536;
    private static int _lastId;

    private readonly IByteTransport _transport;
    private readonly ProtocolHost _host;
    private readonly ConcurrentQueue<Outgoing> _outbound = new ConcurrentQueue<Outgoing>();
    private readonly SemaphoreSlim _outboundReady = new SemaphoreSlim(0);
    private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
    private Thread? _reader;
    private Thread? _writer;
    private volatile bool _stopping;

    internal Connection(IByteTransport transport, ProtocolHost host)
    {
        _transport = transport;
        _host = host;
        ClientId = "c" + Interlocked.Increment(ref _lastId);
    }

    /// <summary>The server's name for this connection, unique while the mod runs.</summary>
    internal string ClientId { get; }

    internal string Transport => _transport.Kind;

    /// <summary>Set once the reader has ended and the transport is freed.</summary>
    internal WaitHandle Stopped => _stopped.WaitHandle;

    internal bool IsStopping => _stopping;

    internal void Start()
    {
        _host.Opened(this);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"StationGodMCP {Transport} {ClientId} reader" };
        _reader.Start();
    }

    /// <summary>Queues one line for the client (a reply or an event); written in order by the writer thread.</summary>
    internal void Send(string line, string? method = null)
    {
        if (_stopping)
        {
            return;
        }

        _outbound.Enqueue(new Outgoing(line, method));
        _outboundReady.Release();
    }

    /// <summary>Ends the connection: pending reads and writes are woken, and the reader frees it.</summary>
    internal void Close()
    {
        _stopping = true;
        _transport.Cancel();
        _outboundReady.Release();
    }

    private void ReadLoop()
    {
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = $"StationGodMCP {Transport} {ClientId} writer" };
        _writer.Start();
        try
        {
            Talk();
        }
        catch (TimeoutException)
        {
            // Nothing within the first-line timeout: the instance is freed for another client.
        }
        catch (IOException)
        {
            // The client went away: the normal end of a connection.
        }
        catch (Exception exception)
        {
            // Anything else on this connection's thread: only this connection is lost.
            if (!_stopping)
            {
                ProtocolLog.Warning($"Connection {ClientId} failed: {exception.Message}");
            }
        }
        finally
        {
            Close();
            _writer.Join();
            _transport.Dispose();
            _host.Ended(this);
            _stopped.Set();
        }
    }

    private void Talk()
    {
        LineFramer framer = new LineFramer();
        List<string> lines = new List<string>();
        byte[] buffer = new byte[ReadChunk];
        Session? session = null;
        long firstLineBy = QueuedCall.DeadlineAfter(_host.Settings.FirstLineTimeoutMilliseconds);
        while (!_stopping)
        {
            int timeout = session == null ? RemainingMilliseconds(firstLineBy) : -1;
            int read = _transport.Read(buffer, timeout);
            if (read <= 0)
            {
                return;
            }

            framer.Feed(buffer, read, lines);
            foreach (string line in lines)
            {
                session ??= _host.SessionFor(this, line);

                session.OnLine(line);
                if (_stopping)
                {
                    return;
                }
            }

            lines.Clear();
        }
    }

    private static int RemainingMilliseconds(long deadline)
    {
        double remaining = -QueuedCall.MillisecondsSince(deadline);
        return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
    }

    private void WriteLoop()
    {
        UTF8Encoding utf8 = new UTF8Encoding(false);
        byte[] bytes = new byte[4096];
        try
        {
            while (true)
            {
                _outboundReady.Wait();
                if (_stopping)
                {
                    return;
                }

                if (!_outbound.TryDequeue(out Outgoing message))
                {
                    continue;
                }

                int count = utf8.GetMaxByteCount(message.Line.Length) + 1;
                if (bytes.Length < count)
                {
                    bytes = new byte[Math.Max(count, bytes.Length * 2)];
                }

                int length = utf8.GetBytes(message.Line, 0, message.Line.Length, bytes, 0);
                bytes[length++] = (byte)'\n';
                if (!_transport.Write(bytes, length, -1))
                {
                    return;
                }

                ProtocolLog.ReplyWritten(message.Method, length);
            }
        }
        catch (Exception exception)
        {
            // A write failing for a reason other than the client leaving: only this connection is lost.
            if (!_stopping)
            {
                ProtocolLog.Warning($"Connection {ClientId} could not write: {exception.Message}");
            }
        }
        finally
        {
            Close();
        }
    }

    private readonly struct Outgoing
    {
        internal Outgoing(string line, string? method)
        {
            Line = line;
            Method = method;
        }

        internal string Line { get; }

        internal string? Method { get; }
    }
}

/// <summary>What a connection does with each line after the first one chose the protocol.</summary>
internal abstract class Session
{
    internal abstract void OnLine(string line);
}

/// <summary>
/// Version 1: one request line, one reply line, in order. Each request waits for its answer before the next line is
/// taken, as the listener thread waited before.
/// </summary>
internal sealed class LineSession : Session
{
    private readonly Connection _connection;
    private readonly ProtocolHost _host;

    internal LineSession(Connection connection, ProtocolHost host)
    {
        _connection = connection;
        _host = host;
    }

    internal override void OnLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Not disposed: the main thread or the deadline watch may still set it after this connection gave up waiting.
        ManualResetEventSlim answered = new ManualResetEventSlim(false);
        LineCall call = new LineCall(line, _host.Settings.LineTimeoutMilliseconds, _connection, (reply, method) =>
        {
            _connection.Send(reply, method);
            answered.Set();
        });
        _host.Submit(call);
        while (!answered.Wait(500))
        {
            if (_connection.IsStopping)
            {
                return;
            }
        }
    }
}
