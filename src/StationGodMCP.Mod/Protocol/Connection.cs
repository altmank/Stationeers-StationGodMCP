#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>A byte stream both ends may read and write at once (an overlapped pipe instance, a socket).</summary>
internal interface IByteTransport : IDisposable
{
    /// <summary>"pipe" or "tcp".</summary>
    string Kind { get; }

    /// <summary>Who is at the other end, for the log (a TCP client's address; the pipe's name).</summary>
    string Peer { get; }

    /// <summary>
    /// Bytes read into buffer; 0 when the other end closed; NativePipe.Cancelled after Cancel. Throws TimeoutException
    /// when nothing came within timeoutMilliseconds (negative: no limit).
    /// </summary>
    int Read(byte[] buffer, int timeoutMilliseconds);

    /// <summary>Writes all count bytes within timeoutMilliseconds (negative: no limit).</summary>
    WriteResult Write(byte[] buffer, int count, int timeoutMilliseconds);

    /// <summary>Wakes a waiting Read or Write; safe from any thread, more than once, and after Dispose.</summary>
    void Cancel();
}

/// <summary>
/// A line that is made when the writer reaches it rather than when it is queued (a subscription's update, which may
/// change while it waits); false when there is nothing to write any more.
/// </summary>
internal interface IOutboundLine
{
    bool TryTake(out string line);
}

/// <summary>How a write ended.</summary>
internal enum WriteResult
{
    Written,
    Closed,
    TimedOut
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
/// One client connection. A reader thread reads lines and hands them to the connection's session (behind the shared
/// secret over TCP); a writer thread writes whatever is queued for the client, in order, so replies and
/// events are written while a read waits. The reader owns the connection's life: when it ends it stops the writer,
/// waits for it, and frees the transport.
/// </summary>
internal sealed class Connection
{
    private const int ReadChunk = 65536;
    private const int FlushMilliseconds = 2000;
    private const int GoodbyeWriteMilliseconds = 1000;
    private static int _lastId;

    private readonly IByteTransport _transport;
    private readonly ProtocolHost _host;
    private readonly ConcurrentQueue<Outgoing> _outbound = new ConcurrentQueue<Outgoing>();
    private readonly SemaphoreSlim _outboundReady = new SemaphoreSlim(0);
    private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
    private Thread? _reader;
    private Thread? _writer;
    private volatile bool _stopping;
    private volatile bool _ending;
    private volatile Session? _session;
    private long _served;
    private long _bytesSent;

    internal Connection(IByteTransport transport, ProtocolHost host)
    {
        _transport = transport;
        _host = host;
        ClientId = "c" + Interlocked.Increment(ref _lastId);
        ConnectedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Stopwatch.GetTimestamp when the client connected.</summary>
    internal long ConnectedAt { get; }

    internal string Peer => _transport.Peer;

    /// <summary>The server's name for this connection, unique while the mod runs.</summary>
    internal string ClientId { get; }

    internal string Transport => _transport.Kind;

    /// <summary>The connection's session; null before its first line.</summary>
    internal Session? Session => _session;

    /// <summary>Set once the reader has ended and the transport is freed.</summary>
    internal WaitHandle Stopped => _stopped.WaitHandle;

    internal bool IsStopping => _stopping;

    /// <summary>Calls answered on this connection (mod_info).</summary>
    internal long ServedCount => Interlocked.Read(ref _served);

    internal long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>The calls run for this connection, per method (mod_info); main thread only.</summary>
    internal Pure.ConnectionCalls Calls { get; } = new Pure.ConnectionCalls();

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

    /// <summary>Queues a line the writer makes when it reaches it (a subscription's update).</summary>
    internal void Send(IOutboundLine line)
    {
        if (_stopping)
        {
            return;
        }

        _outbound.Enqueue(new Outgoing(line));
        _outboundReady.Release();
    }

    /// <summary>Counts one call answered.</summary>
    internal void Served() => Interlocked.Increment(ref _served);

    /// <summary>Ends the connection now: pending reads and writes are woken, and the reader frees it.</summary>
    internal void Close()
    {
        _stopping = true;
        _transport.Cancel();
        _outboundReady.Release();
    }

    /// <summary>Ends the connection once what is queued has been written (a goodbye, a last refusal).</summary>
    internal void EndGracefully()
    {
        _ending = true;
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
            // Nothing within the first-line timeout (the instance is freed for another client), or a sign-in that did
            // not finish in time, which the session answers.
            _session?.OnReadTimeout();
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
            if (_ending)
            {
                _writer.Join(FlushMilliseconds);
            }

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
        long firstLineBy = QueuedCall.DeadlineAfter(_host.Settings.FirstLineTimeoutMilliseconds);
        while (!_stopping && !_ending)
        {
            long? signInBy = _session == null ? firstLineBy : _session.SignInBy;
            int timeout = signInBy.HasValue ? RemainingMilliseconds(signInBy.Value) : -1;
            int read = _transport.Read(buffer, timeout);
            if (read <= 0)
            {
                return;
            }

            bool fits = framer.Feed(buffer, read, lines);
            foreach (string line in lines)
            {
                if (_session == null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        // Blank lines are ignored, and do not stop the first-line timeout.
                        continue;
                    }

                    _session = _host.SessionFor(this);
                    framer.MaximumLineBytes = _session.MaximumLineBytes;
                }

                _session.OnLine(line);
                if (_stopping || _ending)
                {
                    return;
                }
            }

            lines.Clear();
            if (!fits || framer.Overflowed)
            {
                _session?.OnOverflow();
                return;
            }
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
        long lastWrite = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                Session? session = _session;
                bool pings = session != null && session.Pings;
                int wait = pings
                    ? Math.Max(0, _host.Settings.PingAfterMilliseconds - (int)QueuedCall.MillisecondsSince(lastWrite))
                    : Timeout.Infinite;
                bool signalled = _outboundReady.Wait(wait);
                if (_stopping)
                {
                    return;
                }

                if (!_outbound.TryDequeue(out Outgoing message))
                {
                    if (_ending)
                    {
                        return;
                    }

                    if (signalled || !pings)
                    {
                        continue;
                    }

                    message = new Outgoing(Wire.Line(new EventView("ping")), null);
                }

                if (message.Deferred != null)
                {
                    if (!message.Deferred.TryTake(out string taken))
                    {
                        continue;
                    }

                    message = new Outgoing(taken, null);
                }

                int count = utf8.GetMaxByteCount(message.Line.Length) + 1;
                if (bytes.Length < count)
                {
                    bytes = new byte[Math.Max(count, bytes.Length * 2)];
                }

                int length = utf8.GetBytes(message.Line, 0, message.Line.Length, bytes, 0);
                bytes[length++] = (byte)'\n';
                WriteResult written = _transport.Write(bytes, length, session?.WriteTimeoutMilliseconds ?? -1);
                if (written == WriteResult.TimedOut)
                {
                    SlowClient(session!, utf8);
                    return;
                }

                if (written != WriteResult.Written)
                {
                    return;
                }

                lastWrite = Stopwatch.GetTimestamp();
                Interlocked.Add(ref _bytesSent, length);
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

    // A client that has not taken a line for the write timeout: its unstarted calls are dropped, it is told why if it
    // can still take a line, and it is closed.
    private void SlowClient(Session session, UTF8Encoding utf8)
    {
        session.OnSlowClient();
        byte[] goodbye = utf8.GetBytes(Wire.Line(new GoodbyeView("slow_client")) + "\n");
        _transport.Write(goodbye, goodbye.Length, GoodbyeWriteMilliseconds);
        ProtocolLog.Info($"Connection {ClientId} closed: it took nothing for {session.WriteTimeoutMilliseconds / 1000} s.");
    }

    private readonly struct Outgoing
    {
        internal Outgoing(string line, string? method)
        {
            Line = line;
            Method = method;
            Deferred = null;
        }

        internal Outgoing(IOutboundLine deferred)
        {
            Line = string.Empty;
            Method = null;
            Deferred = deferred;
        }

        internal string Line { get; }

        internal string? Method { get; }

        internal IOutboundLine? Deferred { get; }
    }
}

/// <summary>What a connection does with each line it reads.</summary>
internal abstract class Session
{
    /// <summary>The protocol version spoken (mod_info).</summary>
    internal int Protocol => 2;

    /// <summary>The longest line accepted; null for no limit.</summary>
    internal virtual int? MaximumLineBytes => null;

    /// <summary>Whether the writer sends ping after 30 s of silence.</summary>
    internal virtual bool Pings => false;

    /// <summary>How long one line may take to be written before the client is judged too slow; negative for no limit.</summary>
    internal virtual int WriteTimeoutMilliseconds => -1;

    /// <summary>Stopwatch time by which the next line must arrive (a sign-in in progress); null for no limit.</summary>
    internal virtual long? SignInBy => null;

    /// <summary>The key's name, anonymous, or null where the protocol has none.</summary>
    internal virtual string? Client => null;

    /// <summary>The name the client gave itself (hello.client.name).</summary>
    internal virtual string? Label => null;

    internal virtual int InFlight => 0;

    internal abstract void OnLine(string line);

    /// <summary>SignInBy passed: the connection ends after this.</summary>
    internal virtual void OnReadTimeout()
    {
    }

    /// <summary>A line passed MaximumLineBytes: the connection ends after this.</summary>
    internal virtual void OnOverflow()
    {
    }

    /// <summary>The server is stopping or leaving the world: tell the client why, then end.</summary>
    internal virtual void ShutDown(string reason)
    {
    }

    /// <summary>The client took nothing for WriteTimeoutMilliseconds: drop what has not started.</summary>
    internal virtual void OnSlowClient()
    {
    }

    /// <summary>A world finished loading.</summary>
    internal virtual void OnWorldChanged(WorldFacts world)
    {
    }
}

/// <summary>
/// TCP: the first line must be the shared-secret sign-in {"type": "auth", "secret": ...}, compared in fixed time with
/// [Remote MCP] Secret. After it the connection talks exactly as a pipe connection, hello first. Each sign-in is logged
/// with the client's address.
/// </summary>
internal sealed class SecretGateSession : Session
{
    private readonly Connection _connection;
    private readonly ProtocolHost _host;
    private bool _signedIn;
    private Session? _inner;

    internal SecretGateSession(Connection connection, ProtocolHost host)
    {
        _connection = connection;
        _host = host;
    }

    internal override int? MaximumLineBytes => _inner?.MaximumLineBytes;

    internal override bool Pings => _inner?.Pings ?? false;

    internal override int WriteTimeoutMilliseconds => _inner?.WriteTimeoutMilliseconds ?? -1;

    internal override long? SignInBy => _inner?.SignInBy;

    internal override string? Client => _inner?.Client;

    internal override string? Label => _inner?.Label;

    internal override int InFlight => _inner?.InFlight ?? 0;

    internal override void OnLine(string line)
    {
        if (_inner != null)
        {
            _inner.OnLine(line);
            return;
        }

        if (_signedIn)
        {
            _inner = _host.AfterSignIn(_connection);
            _inner.OnLine(line);
            return;
        }

        if (_host.LegacySecret == null || !LegacySecret.Matches(line, _host.LegacySecret))
        {
            _connection.Send(ApiJson.WriteFresh(new AuthRefusedView(new ErrorView("unauthorized", "Authentication failed."))));
            _connection.EndGracefully();
            return;
        }

        ProtocolLog.Info($"TCP sign-in from {_connection.Peer} ({_connection.ClientId}).");
        _signedIn = true;
        _connection.Send(ApiJson.WriteFresh(new AuthAcceptedView()));
    }

    internal override void OnReadTimeout() => _inner?.OnReadTimeout();

    internal override void OnOverflow() => _inner?.OnOverflow();

    internal override void ShutDown(string reason)
    {
        if (_inner != null)
        {
            _inner.ShutDown(reason);
            return;
        }

        _connection.Close();
    }

    internal override void OnSlowClient() => _inner?.OnSlowClient();

    internal override void OnWorldChanged(WorldFacts world) => _inner?.OnWorldChanged(world);
}

/// <summary>The old TCP sign-in line, checked against the shared secret in fixed time.</summary>
internal static class LegacySecret
{
    internal static bool Matches(string line, string secret)
    {
        try
        {
            JObject message = JObject.Parse(line);
            return string.Equals(message.Value<string>("type"), "auth", StringComparison.Ordinal) &&
                   FixedTimeEquals(message.Value<string>("secret"), secret);
        }
        catch (Exception)
        {
            // JObject.Parse on a first line that is not JSON, or a secret that is not a string: not a sign-in.
            return false;
        }
    }

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        int difference = suppliedBytes.Length ^ expectedBytes.Length;
        int length = Math.Max(suppliedBytes.Length, expectedBytes.Length);
        for (int index = 0; index < length; index++)
        {
            byte left = index < suppliedBytes.Length ? suppliedBytes[index] : (byte)0;
            byte right = index < expectedBytes.Length ? expectedBytes[index] : (byte)0;
            difference |= left ^ right;
        }

        return difference == 0;
    }
}
