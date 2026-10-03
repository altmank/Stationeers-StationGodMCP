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

/// <summary>Which protocol the fake mod speaks: today's (an old mod), or version 2 beside it as the mod will.</summary>
internal enum FakeProtocol
{
    OldMod,
    Version2
}

/// <summary>One call the fake mod received, with builders for its reply in the version it came in.</summary>
internal sealed record FakeCall(int Connection, int Version, JsonElement Id, string Method, JsonElement Params, JsonElement? Shape,
    JsonElement Message)
{
    public string Ok(string resultJson, bool shaped = false)
    {
        string mark = shaped ? ",\"shaped\":true" : "";
        return Version == 2
            ? "{\"type\":\"reply\",\"id\":" + Id.GetRawText() + ",\"ok\":true" + mark + ",\"result\":" + resultJson +
              ",\"elapsed_ms\":0.1,\"queue_ms\":0.2,\"frame\":7}"
            : "{\"id\":" + Id.GetRawText() + ",\"ok\":true,\"result\":" + resultJson + mark + ",\"elapsed_ms\":0.1}";
    }

    public string Error(string code, string message)
    {
        string error = JsonSerializer.Serialize(new { code, message });
        return Version == 2
            ? "{\"type\":\"reply\",\"id\":" + Id.GetRawText() + ",\"ok\":false,\"error\":" + error + "}"
            : "{\"id\":" + Id.GetRawText() + ",\"ok\":false,\"error\":" + error + "}";
    }
}

/// <summary>
/// An in-process fake of the mod's listener, on a real overlapped named pipe or loopback TCP: today's protocol (hello
/// answered as a request with no method, TCP signed in with the shared secret) or version 2 (hello, challenge and key
/// proof, welcome, calls answered out of order, events pushed). Every line it receives is kept; Answer decides each
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

    private FakeGame(FakeProtocol protocol, string? pipeName, TcpListener? listener)
    {
        Protocol = protocol;
        _pipeName = pipeName;
        _listener = listener;
        Target = pipeName != null
            ? new GameTarget.Pipe(pipeName)
            : new GameTarget.Tcp("127.0.0.1", ((IPEndPoint)listener!.LocalEndpoint).Port);
        _ = Task.Run(AcceptAsync);
    }

    public FakeProtocol Protocol { get; }

    public GameTarget Target { get; }

    /// <summary>The reply line for a call, or null to leave it unanswered. The default answers {} to everything.</summary>
    public Func<FakeCall, Task<string?>> Answer { get; set; } = call => Task.FromResult<string?>(call.Ok("{}"));

    public string WorldId { get; set; } = "world-1";

    public string CatalogueHash { get; set; } = GameCatalogue.BuiltIn.Hash;

    /// <summary>What the catalogue method answers.</summary>
    public string CatalogueJson { get; set; } = GameCatalogue.BuiltIn.Document.GetRawText();

    public List<string> Features { get; set; } = ["shape", "shape.paths", "subscriptions", "cancel"];

    public int MaxInFlight { get; set; } = 16;

    /// <summary>Key name to base64 key: a hello with auth "key" must prove one of these.</summary>
    public Dictionary<string, string> Keys { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<JsonElement> Received { get; } = new();

    public ConcurrentQueue<FakeCall> Calls { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    public int OpenConnections => _peers.Count;

    public static FakeGame OnPipe(FakeProtocol protocol) =>
        new(protocol, "StationGodMCP-fake-" + Guid.NewGuid().ToString("N"), null);

    public static FakeGame OnTcp(FakeProtocol protocol)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeGame(protocol, null, listener);
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

    /// <summary>Sends an event line to every version-2 connection.</summary>
    public async Task PushAsync(string eventJson)
    {
        foreach (Peer peer in _peers.Values.Where(peer => peer.Version == 2))
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

        bool isHello = hello.TryGetProperty("type", out JsonElement type) && type.ValueEquals("hello");
        if (_listener != null && (Protocol == FakeProtocol.OldMod || !isHello))
        {
            // Today's TCP: the first line must be the shared-secret sign-in; anything else is refused and closed.
            bool signedIn = isHello is false && hello.TryGetProperty("secret", out JsonElement secret) &&
                            secret.ValueEquals(TestSecret);
            if (!signedIn)
            {
                await peer.WriteAsync("""{"ok":false,"error":{"code":"unauthorized","message":"Authentication failed."}}""");
                return;
            }

            await peer.WriteAsync("""{"ok":true}""");
            await ServeVersionOneAsync(peer, null);
            return;
        }

        if (Protocol == FakeProtocol.OldMod || !isHello)
        {
            await ServeVersionOneAsync(peer, hello);
            return;
        }

        if (hello.TryGetProperty("auth", out JsonElement auth) && auth.ValueEquals("key"))
        {
            string nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            await peer.WriteAsync($$"""{"type":"challenge","nonce":"{{nonce}}"}""");
            JsonElement? proofLine = await ReadAsync(peer);
            string name = hello.GetProperty("client").GetProperty("name").GetString()!;
            string transport = _listener != null ? "tcp" : "pipe";
            bool proven = proofLine is { } given && Keys.TryGetValue(name, out string? key) &&
                          given.GetProperty("client").ValueEquals(name) &&
                          given.GetProperty("proof").ValueEquals(KeyProof.Of(key, nonce, name, transport)!);
            if (!proven)
            {
                await peer.WriteAsync("""{"type":"reply","id":null,"ok":false,"error":{"code":"unauthorized","message":"Bad key proof."}}""");
                return;
            }

            peer.Client = name;
        }

        peer.Version = 2;
        string features = string.Join(",", Features.Select(feature => $"\"{feature}\""));
        await peer.WriteAsync(
            $$"""{"type":"welcome","protocol":2,"client_id":"c{{peer.Number}}","client":"{{peer.Client}}","level":"cheat","grants":[],"cheat":{"armed":false,"until_utc":null,"standing":true},"server":{"mod_version":"1.10.0","instance_id":"fake","pipe_name":"fake","transport":"pipe","role":"host","dedicated":false,"world":{"id":"{{WorldId}}","save":"fake","epoch":0},"game_state":"Running"},"catalogue":{"hash":"{{CatalogueHash}}","methods":91,"protocol_methods":3},"limits":{"max_in_flight":{{MaxInFlight}}},"features":[{{features}}]}""");
        while (await ReadAsync(peer) is { } message)
        {
            string? kind = message.TryGetProperty("type", out JsonElement value) ? value.GetString() : null;
            if (kind != "call")
            {
                continue;
            }

            FakeCall call = CallOf(peer, 2, message);
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

    // One request, one reply, in order; a line with no method is answered as the old mod answers hello.
    private async Task ServeVersionOneAsync(Peer peer, JsonElement? first)
    {
        peer.Version = 1;
        JsonElement? message = first ?? await ReadAsync(peer);
        while (message is { } request)
        {
            if (!request.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String)
            {
                await peer.WriteAsync("""{"id":null,"ok":false,"error":{"code":"method_not_found","message":"Unknown method ''."}}""");
            }
            else if (await Answer(CallOf(peer, 1, request)) is { } reply)
            {
                await peer.WriteAsync(reply);
            }

            message = await ReadAsync(peer);
        }
    }

    private FakeCall CallOf(Peer peer, int version, JsonElement message)
    {
        FakeCall call = new(peer.Number, version, message.GetProperty("id"), message.GetProperty("method").GetString()!,
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

        public int Version { get; set; }

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
