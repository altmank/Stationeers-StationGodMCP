#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// An IHubLink in memory: streams numbered from 1, calls answered by Answer ({} by default) and kept, the welcome and
/// reachability set by the test, events raised with Raise, and every stream end, cursor close and release kept.
/// </summary>
internal sealed class FakeHubLink : IHubLink
{
    public const string ModWelcome =
        """{"type":"welcome","protocol":2,"client_id":"c7","client":"anonymous","server":{"mod_version":"1.33.2","world":{"id":"world-1","save":"fake","epoch":0},"game_state":"Running"},"limits":{"max_in_flight":16},"features":["shape","subscriptions","cancel","streams","cursors"],"resume":{"token":"hub-0123456789abcdef","resumed":false,"window_s":60}}""";

    private long _lastStream;

    public ConcurrentQueue<GameCall> Calls { get; } = new();

    public Func<GameCall, CancellationToken, Task<GameReply>> Answer { get; set; } =
        (_, _) => Task.FromResult(FakeGameLink.Ok("{}"));

    public bool Reachable { get; set; } = true;

    public JsonElement? Welcome { get; set; } = Element(ModWelcome);

    public ConcurrentQueue<long> EndedStreams { get; } = new();

    public ConcurrentQueue<string> ClosedCursors { get; } = new();

    public ConcurrentQueue<long> Released { get; } = new();

    public bool SessionEnded { get; private set; }

    public string? WorldId => Reachable ? "world-1" : null;

    public event Action<string?>? WorldChanged
    {
        add { }
        remove { }
    }

    public event Action<JsonElement>? EventReceived;

    public static JsonElement Element(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public void Raise(string eventJson) => EventReceived?.Invoke(Element(eventJson));

    public IReadOnlyList<GameCall> CallsTo(string method) => Calls.Where(call => call.Method == method).ToList();

    public Task<bool> ConnectAsync(CancellationToken cancellation) => Task.FromResult(Reachable);

    public long NewStream() => Interlocked.Increment(ref _lastStream);

    public Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        Calls.Enqueue(call);
        return Reachable
            ? Answer(call, cancellation)
            : Task.FromResult<GameReply>(new GameReply.Unreachable("The game could not be reached.", maybeRan: false));
    }

    public Task EndStreamAsync(long stream)
    {
        EndedStreams.Enqueue(stream);
        return Task.CompletedTask;
    }

    public Task SessionEndAsync()
    {
        SessionEnded = true;
        return Task.CompletedTask;
    }

    public Task CursorCloseAsync(string cursorId)
    {
        ClosedCursors.Enqueue(cursorId);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(long stream)
    {
        Released.Enqueue(stream);
        return Task.CompletedTask;
    }

    public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation) =>
        throw new NotSupportedException();

    public Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation) =>
        throw new NotSupportedException();

    public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation) =>
        throw new NotSupportedException();
}

/// <summary>A caller of one HubSession over in-memory pipes: lines sent, lines read back, the input closed.</summary>
internal sealed class HubCaller : IAsyncDisposable
{
    public const string Hello = """{"type":"hello","protocol":[2],"client":{"name":"test","version":"1"},"features":["pipeline"]}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly AnonymousPipeServerStream _toSession = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _sessionInput;
    private readonly AnonymousPipeServerStream _sessionOutput = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _fromSession;
    private readonly StreamReader _reader;
    private bool _inputClosed;

    private HubCaller(HubSession session)
    {
        Session = session;
        _sessionInput = new AnonymousPipeClientStream(PipeDirection.In, _toSession.ClientSafePipeHandle);
        _fromSession = new AnonymousPipeClientStream(PipeDirection.In, _sessionOutput.ClientSafePipeHandle);
        _reader = new StreamReader(_fromSession, new UTF8Encoding(false));
        Running = Task.Run(() => session.RunAsync(_sessionInput, _sessionOutput, CancellationToken.None));
    }

    public HubSession Session { get; }

    public Task<HubSessionEnd> Running { get; }

    public static HubCaller Start(FakeHubLink link, HubSessionSettings? settings = null, IWriteGate? gate = null) =>
        new(new HubSession(link, new VerbPipeline(link, gate) { PollInterval = TimeSpan.Zero }, settings ?? new HubSessionSettings(Face.Program) { PassThrough = true }));

    public void Send(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        _toSession.Write(bytes, 0, bytes.Length);
        _toSession.Flush();
    }

    public async Task<JsonElement> NextAsync()
    {
        string? line = await _reader.ReadLineAsync().WaitAsync(Patience);
        Xunit.Assert.NotNull(line);
        return FakeHubLink.Element(line!);
    }

    /// <summary>The next line, or null when the session closed its output.</summary>
    public async Task<string?> NextLineOrEndAsync() => await _reader.ReadLineAsync().WaitAsync(Patience);

    public async Task<JsonElement> HelloAsync()
    {
        Send(Hello);
        return await NextAsync();
    }

    public async Task<JsonElement> CallAsync(string id, string method, string parameters = "{}", long? stream = null)
    {
        Send(Call(id, method, parameters, stream));
        return await NextAsync();
    }

    public static string Call(string id, string method, string parameters = "{}", long? stream = null) =>
        "{\"type\":\"call\",\"id\":\"" + id + "\",\"method\":\"" + method + "\",\"params\":" + parameters +
        (stream is { } given ? ",\"stream\":" + given : "") + "}";

    /// <summary>Closes the caller's side, waits for the session to stop reading, and closes the session.</summary>
    public async Task<HubSessionEnd> EndAsync()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _toSession.Dispose();
        }

        HubSessionEnd end = await Running.WaitAsync(Patience);
        await Session.CloseAsync().WaitAsync(Patience);
        return end;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_inputClosed)
        {
            await EndAsync();
        }

        _sessionOutput.Dispose();
        _reader.Dispose();
        _sessionInput.Dispose();
    }
}
