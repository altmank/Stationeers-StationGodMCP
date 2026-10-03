#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace StationGodMCP.Protocol;

/// <summary>
/// The TCP listener: each accepted socket becomes a Connection like a pipe client's. Its first line must be the
/// shared-secret sign-in (SecretGateSession); after it the connection speaks the same protocols as the pipe. At most maxConnections at once, counted apart from the pipe's, so remote connections and
/// unfinished sign-ins never take a local client's place; a socket past it is closed at once.
/// </summary>
internal sealed class TcpAcceptor : IDisposable
{
    private const int StopWaitMilliseconds = 2000;

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly int _maxConnections;
    private readonly ProtocolHost _host;
    private TcpListener? _listener;
    private Thread? _thread;
    private volatile bool _stopping;

    internal TcpAcceptor(string bindAddress, int port, int maxConnections, ProtocolHost host)
    {
        if (!IPAddress.TryParse(bindAddress, out IPAddress? address))
        {
            throw new ArgumentException($"BindAddress '{bindAddress}' is not a valid IP address.", nameof(bindAddress));
        }

        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        _address = address;
        _port = port;
        _maxConnections = maxConnections;
        _host = host;
    }

    /// <summary>Whether TCP listens: when it is enabled and the shared secret is set.</summary>
    internal static bool ShouldListen(bool enabled, string? secret) => enabled && !string.IsNullOrEmpty(secret);

    /// <summary>The port actually bound (the one asked for, or the system's choice for 0 in tests).</summary>
    internal int BoundPort => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    internal void Start()
    {
        _listener = new TcpListener(_address, _port);
        _listener.Start();
        _thread = new Thread(Accept) { IsBackground = true, Name = "StationGodMCP TCP listener" };
        _thread.Start();
        ProtocolLog.Info($"Authenticated remote MCP bridge listening on {_address}:{BoundPort} (plain TCP).");
    }

    public void Dispose() => ShutDown("shutting_down");

    internal void ShutDown(string reason)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // TcpListener.Stop on a listener already closed.
        }

        _thread?.Join(StopWaitMilliseconds);
        _host.CloseAll(reason, StopWaitMilliseconds);
        _host.Retire();
    }

    private void Accept()
    {
        while (!_stopping)
        {
            Socket socket;
            try
            {
                socket = _listener!.AcceptSocket();
            }
            catch (Exception exception) when (_stopping && (exception is SocketException || exception is ObjectDisposedException))
            {
                return;
            }
            catch (Exception exception)
            {
                // AcceptSocket failing for one client: logged, and the listener keeps accepting.
                ProtocolLog.Warning($"TCP accept failed: {exception.Message}");
                continue;
            }

            if (CountTcp() >= _maxConnections)
            {
                socket.Close();
                continue;
            }

            socket.NoDelay = true;
            new Connection(new SocketTransport(socket), _host).Start();
        }
    }

    private int CountTcp()
    {
        int count = 0;
        foreach (Connection connection in _host.Open)
        {
            count += connection.Transport == "tcp" ? 1 : 0;
        }

        return count;
    }
}

/// <summary>A TCP socket as a connection's byte stream; a socket reads and writes at once natively.</summary>
internal sealed class SocketTransport : IByteTransport
{
    private readonly Socket _socket;
    private volatile bool _cancelled;

    internal SocketTransport(Socket socket)
    {
        _socket = socket;
        Peer = socket.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public string Kind => "tcp";

    public string Peer { get; }

    public int Read(byte[] buffer, int timeoutMilliseconds)
    {
        try
        {
            _socket.ReceiveTimeout = timeoutMilliseconds < 0 ? 0 : Math.Max(1, timeoutMilliseconds);
            return _socket.Receive(buffer);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut && !_cancelled)
        {
            throw new TimeoutException();
        }
        catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
        {
            return _cancelled ? NativePipe.Cancelled : 0;
        }
    }

    public WriteResult Write(byte[] buffer, int count, int timeoutMilliseconds)
    {
        try
        {
            _socket.SendTimeout = timeoutMilliseconds < 0 ? 0 : Math.Max(1, timeoutMilliseconds);
            int offset = 0;
            while (offset < count)
            {
                offset += _socket.Send(buffer, offset, count - offset, SocketFlags.None);
            }

            return WriteResult.Written;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut && !_cancelled)
        {
            return WriteResult.TimedOut;
        }
        catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
        {
            return WriteResult.Closed;
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
        {
            // Already shut or closed: the waits it would wake have ended.
        }

        _socket.Close();
    }

    public void Dispose() => _socket.Close();
}
