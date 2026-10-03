#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>One call the fake mod received, with builders for its reply.</summary>
internal sealed record FakeCall(int Connection, JsonElement Id, string Method, JsonElement Params, JsonElement? Shape,
    JsonElement Message)
{
    public string Ok(string resultJson, bool shaped = false) =>
        "{\"type\":\"reply\",\"id\":" + Id.GetRawText() + ",\"ok\":true" + (shaped ? ",\"shaped\":true" : "") +
        ",\"result\":" + resultJson + ",\"elapsed_ms\":0.1,\"queue_ms\":0.2,\"frame\":7}";

    public string Error(string code, string message) =>
        "{\"type\":\"reply\",\"id\":" + Id.GetRawText() + ",\"ok\":false,\"error\":" +
        JsonSerializer.Serialize(new { code, message }) + "}";
}

/// <summary>
/// An in-process fake of the mod's listener, on a real overlapped named pipe or loopback TCP: hello, welcome, calls
/// answered out of order, events pushed; a first line that is not hello closes the connection; over TCP the shared
/// secret comes first. Every line it receives is kept; Answer decides each
/// call's reply, null for none.
/// </summary>
internal sealed class FakeGame : IAsyncDisposable
{
    public const string TestSecret = "s3cret";

    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, Peer> _peers = new();
    private readonly TcpListener? _listener;
    private readonly string? _pipeName;
    private int _connections;

    private FakeGame(string? pipeName, TcpListener? listener)
    {
        _pipeName = pipeName;
        _listener = listener;
        Target = pipeName != null
            ? new GameTarget.Pipe(pipeName)
            : new GameTarget.Tcp("127.0.0.1", ((IPEndPoint)listener!.LocalEndpoint).Port);
        _ = Task.Run(AcceptAsync);
    }

    public GameTarget Target { get; }

    /// <summary>The reply line for a call, or null to leave it unanswered. The default answers {} to everything.</summary>
    public Func<FakeCall, Task<string?>> Answer { get; set; } = call => Task.FromResult<string?>(call.Ok("{}"));

    public string WorldId { get; set; } = "world-1";

    public string CatalogueHash { get; set; } = GameCatalogue.BuiltIn.Hash;

    /// <summary>What the catalogue method answers.</summary>
    public string CatalogueJson { get; set; } = GameCatalogue.BuiltIn.Document.GetRawText();

    public List<string> Features { get; set; } = ["shape", "shape.paths", "subscriptions", "cancel"];

    public int MaxInFlight { get; set; } = 16;

    public ConcurrentQueue<JsonElement> Received { get; } = new();

    public ConcurrentQueue<FakeCall> Calls { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    public int OpenConnections => _peers.Count;

    public static FakeGame OnPipe() => new("StationGodMCP-fake-" + Guid.NewGuid().ToString("N"), null);

    public static FakeGame OnTcp()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeGame(null, listener);
    }

    public IEnumerable<FakeCall> CallsTo(string method) => Calls.Where(call => call.Method == method);

    /// <summary>Closes every connection, as a game restart or save reload does.</summary>
    public void DropAll()
    {
        foreach (Peer peer in _peers.Values)
        {
            peer.Close();
        }
    }

    /// <summary>Sends an event line to every welcomed connection.</summary>
    public async Task PushAsync(string eventJson)
    {
        foreach (Peer peer in _peers.Values.Where(peer => peer.Welcomed))
        {
            await peer.WriteAsync(eventJson);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener?.Stop();
        DropAll();
        await Task.Yield();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                if (_listener != null)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    client.NoDelay = true;
                    stream = client.GetStream();
                }
                else
                {
                    NamedPipeServerStream pipe = new(_pipeName!, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.WaitForConnectionAsync(_stopping.Token);
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        throw;
                    }

                    stream = pipe;
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or
                                                  SocketException or IOException)
            {
                return;
            }

            int number = Interlocked.Increment(ref _connections);
            Peer peer = new(number, stream);
            _peers[number] = peer;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(peer);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or
                                                      OperationCanceledException or InvalidOperationException)
                {
                    // The client went away.
                }
                finally
                {
                    _peers.TryRemove(number, out _);
                    peer.Close();
                }
            });
        }
    }

    private async Task ServeAsync(Peer peer)
    {
        JsonElement? first = await ReadAsync(peer);
        if (first is not { } hello)
        {
            return;
        }

        if (_listener != null)
        {
            // TCP: the first line must be the shared-secret sign-in; anything else is refused and closed.
            bool signedIn = hello.TryGetProperty("secret", out JsonElement secret) && secret.ValueEquals(TestSecret);
            if (!signedIn)
            {
                await peer.WriteAsync("""{"ok":false,"error":{"code":"unauthorized","message":"Authentication failed."}}""");
                return;
            }

            await peer.WriteAsync("""{"ok":true}""");
            if (await ReadAsync(peer) is not { } next)
            {
                return;
            }

            hello = next;
        }

        bool isHello = hello.TryGetProperty("type", out JsonElement type) && type.ValueEquals("hello");
        if (!isHello)
        {
            return;
        }

        peer.Welcomed = true;
        string features = string.Join(",", Features.Select(feature => $"\"{feature}\""));
        await peer.WriteAsync(
            $$"""{"type":"welcome","protocol":2,"client_id":"c{{peer.Number}}","client":"{{peer.Client}}","server":{"mod_version":"1.10.0","instance_id":"fake","pipe_name":"fake","transport":"pipe","role":"host","dedicated":false,"world":{"id":"{{WorldId}}","save":"fake","epoch":0},"game_state":"Running"},"catalogue":{"hash":"{{CatalogueHash}}","methods":91,"protocol_methods":3},"limits":{"max_in_flight":{{MaxInFlight}}},"features":[{{features}}]}""");
        while (await ReadAsync(peer) is { } message)
        {
            string? kind = message.TryGetProperty("type", out JsonElement value) ? value.GetString() : null;
            if (kind != "call")
            {
                continue;
            }

            FakeCall call = CallOf(peer, message);
            _ = Task.Run(async () =>
            {
                string? reply = call.Method == "catalogue"
                    ? call.Ok(CatalogueJson)
                    : await Answer(call);
                if (reply != null)
                {
                    await peer.WriteAsync(reply);
                }
            });
        }
    }

    private FakeCall CallOf(Peer peer, JsonElement message)
    {
        FakeCall call = new(peer.Number, message.GetProperty("id"), message.GetProperty("method").GetString()!,
            message.TryGetProperty("params", out JsonElement parameters) ? parameters : JsonDocument.Parse("{}").RootElement,
            message.TryGetProperty("shape", out JsonElement shape) ? shape : null, message);
        Calls.Enqueue(call);
        return call;
    }

    private async Task<JsonElement?> ReadAsync(Peer peer)
    {
        string? line = await peer.Reader.ReadLineAsync(_stopping.Token);
        if (line == null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement message = document.RootElement.Clone();
        Received.Enqueue(message);
        return message;
    }

    private sealed class Peer(int number, Stream stream)
    {
        private readonly SemaphoreSlim _writing = new(1, 1);

        public int Number { get; } = number;

        public StreamReader Reader { get; } = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        public bool Welcomed { get; set; }

        public string Client { get; set; } = "anonymous";

        public async Task WriteAsync(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _writing.WaitAsync();
            try
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Closed under the write.
            }
            finally
            {
                _writing.Release();
            }
        }

        public void Close()
        {
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
                // Already broken.
            }
        }
    }
}
