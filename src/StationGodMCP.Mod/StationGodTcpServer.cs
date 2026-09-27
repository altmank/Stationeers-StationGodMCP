#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP;

/// <summary>
/// The optional remote transport: plain TCP, one line per request and per reply, after a first line
/// {"type": "auth", "secret": ...} compared in fixed time. Off unless configured (StationGodMod.LoadConfiguration);
/// meant for the authoritative server only, since the secret travels unencrypted.
/// </summary>
internal sealed class StationGodTcpServer : IDisposable
{
    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;
    private const int AuthTimeoutMilliseconds = 10000;
    private const int SendTimeoutMilliseconds = 35000;
    private const int RequestTimeoutMilliseconds = 30000;
    private const int JoinMilliseconds = 1000;
    private const int BufferSize = 4096;

    private readonly IPAddress _bindAddress;
    private readonly int _port;
    private readonly string _secret;
    private readonly StationGodRequestDispatcher _dispatcher;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new ConcurrentDictionary<TcpClient, byte>();
    private Thread? _listenerThread;
    private TcpListener? _listener;
    private volatile bool _stopping;

    internal StationGodTcpServer(string bindAddress, int port, string secret, StationGodRequestDispatcher dispatcher)
    {
        if (!IPAddress.TryParse(bindAddress, out IPAddress? address))
        {
            throw new ArgumentException($"RemoteBindAddress '{bindAddress}' is not a valid IP address.",
                nameof(bindAddress));
        }

        if (port < MinimumPort || port > MaximumPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "RemotePort must be between 1 and 65535.");
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("RemoteSecret must not be empty.", nameof(secret));
        }

        _bindAddress = address;
        _port = port;
        _secret = secret;
        _dispatcher = dispatcher;
    }

    internal void Start()
    {
        if (_listenerThread != null)
        {
            return;
        }

        _listener = new TcpListener(_bindAddress, _port);
        _listener.Start();
        _listenerThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "StationGodMCP TCP listener"
        };
        _listenerThread.Start();
        StationGodMod.Log($"Authenticated remote MCP bridge listening on {_bindAddress}:{_port} (plain TCP).");
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // TcpListener.Stop on a listener already closed.
        }

        foreach (TcpClient client in _clients.Keys)
        {
            client.Close();
        }

        if (_listenerThread != null && _listenerThread.IsAlive)
        {
            _listenerThread.Join(JoinMilliseconds);
        }

        _listenerThread = null;
        _listener = null;
    }

    private void ListenLoop()
    {
        while (!_stopping)
        {
            try
            {
                TcpClient client = _listener!.AcceptTcpClient();
                _clients.TryAdd(client, 0);
                Thread clientThread = new Thread(HandleClient)
                {
                    IsBackground = true,
                    Name = "StationGodMCP TCP client"
                };
                clientThread.Start(client);
            }
            catch (SocketException) when (_stopping)
            {
                break;
            }
            catch (ObjectDisposedException) when (_stopping)
            {
                break;
            }
            catch (Exception exception)
            {
                // TcpListener.AcceptTcpClient or Thread.Start failing for one client: log it, keep listening.
                if (!_stopping)
                {
                    StationGodMod.LogWarning($"TCP accept failed: {exception.Message}");
                }
            }
        }
    }

    private void HandleClient(object? state)
    {
        TcpClient client = (TcpClient)state!;
        try
        {
            Talk(client);
        }
        catch (IOException)
        {
            // The client went away or timed out: the normal end of a connection.
        }
        catch (ObjectDisposedException)
        {
            // The server is stopping and closed the client.
        }
        catch (Exception exception)
        {
            // Anything else on this client's thread: log it, and only this client is lost.
            if (!_stopping)
            {
                StationGodMod.LogWarning($"TCP client failed: {exception.Message}");
            }
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Close();
        }
    }

    // Authentication first (timed), then a reply line for each request line until the client closes.
    private void Talk(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveTimeout = AuthTimeoutMilliseconds;
        client.SendTimeout = SendTimeoutMilliseconds;
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), true, BufferSize, true);
        using StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize, true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        if (!Authenticate(reader.ReadLine()))
        {
            writer.WriteLine(ApiHost.Serialize(
                new AuthRefusedView(new ErrorView("unauthorized", "Authentication failed."))));
            return;
        }

        writer.WriteLine(ApiHost.Serialize(new AuthAcceptedView()));
        client.ReceiveTimeout = 0;
        Serve(reader, writer);
    }

    private void Serve(StreamReader reader, StreamWriter writer)
    {
        string? requestJson;
        while (!_stopping && (requestJson = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(requestJson))
            {
                writer.WriteLine(_dispatcher.Dispatch(requestJson, RequestTimeoutMilliseconds));
            }
        }
    }

    private bool Authenticate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            JObject message = JObject.Parse(json!);
            return string.Equals(message.Value<string>("type"), "auth", StringComparison.Ordinal) &&
                   FixedTimeEquals(message.Value<string>("secret"), _secret);
        }
        catch (Exception)
        {
            // JObject.Parse on a first line that is not JSON: not an authentication.
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
