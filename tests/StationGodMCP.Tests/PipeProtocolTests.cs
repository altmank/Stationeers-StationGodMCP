#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Protocol;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The overlapped pipe on a real Windows named pipe in this process: version 1 unchanged byte for byte, reading and
/// writing at once at both ends, the first-line and request timeouts, and the instance limit.
/// </summary>
public sealed class PipeProtocolTests
{
    [Fact]
    public void LinesSplitAtLineFeedsAcrossReads()
    {
        LineFramer framer = new LineFramer();
        List<string> lines = new List<string>();

        Feed(framer, "﻿first\r\nsec", lines);
        Feed(framer, "ond\n\n  \nthird café", lines);
        Feed(framer, "\r\n", lines);

        Assert.Equal(new[] { "first", "second", "", "  ", "third café" }, lines);
    }

    [Fact]
    public void AByteOrderMarkSplitAcrossReadsIsDroppedOnlyAtTheStart()
    {
        LineFramer framer = new LineFramer();
        List<string> lines = new List<string>();
        byte[] mark = { 0xEF, 0xBB, 0xBF };

        framer.Feed(new[] { mark[0] }, 1, lines);
        framer.Feed(new[] { mark[1], mark[2], (byte)'a', (byte)'\n', mark[0], mark[1], mark[2], (byte)'\n' }, 8, lines);

        Assert.Equal(new[] { "a", "﻿" }, lines);
    }

    [Fact]
    public void ALineOverTheLimitIsReportedOnce()
    {
        LineFramer framer = new LineFramer { MaximumLineBytes = 4 };
        List<string> lines = new List<string>();

        Assert.True(Feed(framer, "abcd\r\nab", lines));
        Assert.False(Feed(framer, "cdef", lines));
        Assert.True(framer.Overflowed);
        Assert.Equal(new[] { "abcd" }, lines);
    }

    [Fact]
    public void ACallIsAnsweredOnce()
    {
        CallState timedOut = new CallState();
        Assert.True(timedOut.TryDrop());
        Assert.False(timedOut.TryStart());

        CallState started = new CallState();
        Assert.True(started.TryStart());
        Assert.False(started.TryDrop());
        Assert.True(started.TryFinish());
        Assert.False(started.TryFinish());
        Assert.True(started.IsAnswered);
    }

    [Fact]
    public void TheShippedLimitsAreTodays()
    {
        Assert.Equal(10000, ProtocolSettings.DefaultFirstLineTimeoutMilliseconds);
        Assert.Equal(30000, ProtocolSettings.DefaultLineTimeoutMilliseconds);
        Assert.Equal(32, ProtocolSettings.DefaultMaxPipeConnections);
    }

    [Fact]
    public async Task VersionOneTranscriptsReplayByteForByte()
    {
        using PipeRig rig = new PipeRig();
        string[] requests =
        {
            """{"id":"1","method":"game_clock","params":{}}""",
            "",
            """{"id":"2","method":"read_logic","params":{"reference_id":"501","logic_type":"Setting"}}""",
            "   ",
            """{"id":"café","method":"find_things","params":{"prefab_contains":"☃"}}""",
            "not json at all"
        };
        using NamedPipeClientStream client = await rig.ConnectAsync();
        StringBuilder sent = new StringBuilder();
        foreach (string request in requests)
        {
            sent.Append(request).Append(request.Length % 2 == 0 ? "\r\n" : "\n");
        }

        byte[] bytes = new UTF8Encoding(false).GetBytes(sent.ToString());
        await client.WriteAsync(bytes, 0, bytes.Length);

        StringBuilder expected = new StringBuilder();
        foreach (string request in requests)
        {
            if (!string.IsNullOrWhiteSpace(request))
            {
                expected.Append(FakeMod.ReplyTo(request)).Append('\n');
            }
        }

        Assert.Equal(expected.ToString(), await ReadExactly(client, Encoding.UTF8.GetByteCount(expected.ToString())));
        Assert.Equal(4, rig.Mod.Ran);
    }

    [Fact]
    public async Task TheServerWritesWhileItsReadWaits()
    {
        using PipeRig rig = new PipeRig();
        using NamedPipeClientStream client = await rig.ConnectAsync();
        Connection connection = rig.WaitConnected();
        Thread.Sleep(100);

        Stopwatch watch = Stopwatch.StartNew();
        connection.Send("""{"pushed":true}""");
        string line = await ReadLineAsync(client);

        Assert.Equal("""{"pushed":true}""", line);
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheClientWritesWhileItsReadWaits()
    {
        using PipeRig rig = new PipeRig();
        using NamedPipeClientStream client = await rig.ConnectAsync();
        byte[] buffer = new byte[4096];
        Task<int> pendingRead = client.ReadAsync(buffer, 0, buffer.Length);
        await Task.Delay(100);
        Assert.False(pendingRead.IsCompleted);

        Stopwatch watch = Stopwatch.StartNew();
        byte[] request = Encoding.UTF8.GetBytes("""{"id":"d","method":"game_clock"}""" + "\n");
        await client.WriteAsync(request, 0, request.Length);
        int read = await pendingRead;

        Assert.Equal(FakeMod.ReplyTo("""{"id":"d","method":"game_clock"}""") + "\n", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AClientThatSendsNothingIsDroppedAndItsInstanceFreed()
    {
        using PipeRig rig = new PipeRig(maxConnections: 1, firstLineMs: 300);
        using NamedPipeClientStream silent = await rig.ConnectAsync();
        byte[] buffer = new byte[16];

        Stopwatch watch = Stopwatch.StartNew();
        Assert.Equal(0, await silent.ReadAsync(buffer, 0, buffer.Length));
        Assert.InRange(watch.ElapsedMilliseconds, 200, 3000);

        using NamedPipeClientStream next = await rig.ConnectAsync();
        await Send(next, """{"id":"n","method":"game_clock"}""");
        Assert.Equal(FakeMod.ReplyTo("""{"id":"n","method":"game_clock"}"""), await ReadLineAsync(next));
    }

    [Fact]
    public async Task ACallNotStartedByItsDeadlineIsAnsweredGameTimeoutAndNeverRuns()
    {
        using PipeRig rig = new PipeRig(lineMs: 300, mainThread: false);
        using NamedPipeClientStream client = await rig.ConnectAsync();

        Stopwatch watch = Stopwatch.StartNew();
        await Send(client, """{"id":"t1","method":"game_clock"}""");
        string reply = await ReadLineAsync(client);

        Assert.Equal("""{"id":"t1","ok":false,"error":{"code":"game_timeout","message":"The Stationeers main thread did not process the request within 0 seconds."}}""", reply);
        Assert.InRange(watch.ElapsedMilliseconds, 250, 2000);
        rig.Mod.RunPending();
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task ACallStartedJustBeforeItsDeadlineIsAnsweredWithItsResultOnly()
    {
        using PipeRig rig = new PipeRig(lineMs: 400, mainThread: false);
        using NamedPipeClientStream client = await rig.ConnectAsync();
        await Send(client, """{"id":"late","method":"game_clock"}""");
        await Task.Delay(300);

        rig.Mod.RunPending(holdMs: 400);
        string reply = await ReadLineAsync(client);
        byte[] more = new byte[64];
        Task<int> extra = client.ReadAsync(more, 0, more.Length);

        Assert.Equal(FakeMod.ReplyTo("""{"id":"late","method":"game_clock"}"""), reply);
        Assert.False(extra.Wait(500), "a second answer arrived");
        Assert.Equal(1, rig.Mod.Ran);
    }

    [Fact]
    public async Task PastTheLimitAClientWaitsUntilAConnectionCloses()
    {
        using PipeRig rig = new PipeRig(maxConnections: 3);
        List<NamedPipeClientStream> held = new List<NamedPipeClientStream>();
        for (int index = 0; index < 3; index++)
        {
            NamedPipeClientStream client = await rig.ConnectAsync();
            await Send(client, $$"""{"id":"h{{index}}","method":"game_clock"}""");
            await ReadLineAsync(client);
            held.Add(client);
        }

        NamedPipeClientStream waiting = new NamedPipeClientStream(".", rig.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Assert.Throws<TimeoutException>(() => waiting.Connect(500));

        held[0].Dispose();
        await waiting.ConnectAsync(5000);
        await Send(waiting, """{"id":"w","method":"game_clock"}""");
        Assert.Equal(FakeMod.ReplyTo("""{"id":"w","method":"game_clock"}"""), await ReadLineAsync(waiting));

        waiting.Dispose();
        foreach (NamedPipeClientStream client in held)
        {
            client.Dispose();
        }
    }

    private static bool Feed(LineFramer framer, string text, List<string> lines)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return framer.Feed(bytes, bytes.Length, lines);
    }

    private static async Task Send(Stream stream, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        List<byte> line = new List<byte>();
        byte[] one = new byte[1];
        using CancellationTokenSource timeout = new CancellationTokenSource(5000);
        while (true)
        {
            int read = await stream.ReadAsync(one, 0, 1, timeout.Token);
            if (read == 0 || one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(line.ToArray());
            }

            line.Add(one[0]);
        }
    }

    private static async Task<string> ReadExactly(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        using CancellationTokenSource timeout = new CancellationTokenSource(5000);
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset, timeout.Token);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, offset);
    }
}

/// <summary>A pipe listener with a fake mod behind it, on a pipe name of its own.</summary>
internal sealed class PipeRig : IDisposable
{
    private readonly DeadlineWatch _deadlines = new DeadlineWatch();
    private readonly PipeListener _listener;
    private readonly BlockingCollection<Connection> _connected = new BlockingCollection<Connection>();

    internal PipeRig(int maxConnections = 32, int firstLineMs = 10000, int lineMs = 30000, bool mainThread = true,
        ProtocolSettings? settings = null, AccessControl? access = null)
    {
        Name = "StationGodMCP-test-" + Guid.NewGuid().ToString("N");
        Mod = new FakeMod(mainThread, _deadlines);
        Access = access ?? new AccessControl(AccessSettings.Defaults, null);
        Host = new ProtocolHost(settings ?? new ProtocolSettings(maxConnections, firstLineMs, lineMs), Mod, _deadlines,
            TestCatalogue.File.Value, Access);
        _listener = new PipeListener(Name, Host);
        _listener.Connected += connection => _connected.Add(connection);
        _listener.Start();
    }

    internal string Name { get; }

    internal FakeMod Mod { get; }

    internal ProtocolHost Host { get; }

    internal AccessControl Access { get; }

    internal async Task<NamedPipeClientStream> ConnectAsync()
    {
        NamedPipeClientStream client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return client;
    }

    internal Connection WaitConnected() =>
        _connected.TryTake(out Connection? connection, 5000) ? connection : throw new TimeoutException();

    public void Dispose()
    {
        _listener.Dispose();
        Mod.Dispose();
        _deadlines.Dispose();
    }
}

/// <summary>
/// Stands in for the dispatcher: a "main thread" that takes calls in order and answers each with a reply made from
/// its line, or (mainThread false) never takes them until a test says so.
/// </summary>
internal sealed class FakeMod : ICallQueue, ICallRunner, IDisposable
{
    private readonly RoundRobinScheduler _calls = new RoundRobinScheduler();
    private readonly SemaphoreSlim _added = new SemaphoreSlim(0);
    private readonly Thread? _thread;
    private readonly DeadlineWatch _deadlines;
    private volatile bool _stopped;
    private int _ran;

    internal FakeMod(bool mainThread, DeadlineWatch deadlines)
    {
        _deadlines = deadlines;
        if (mainThread)
        {
            _thread = new Thread(() =>
            {
                while (!_stopped)
                {
                    _added.Wait(50);
                    RunPending();
                }
            }) { IsBackground = true };
            _thread.Start();
        }
    }

    internal int Ran => Volatile.Read(ref _ran);

    internal static string ReplyTo(string line) =>
        "{\"echo\":" + Newtonsoft.Json.JsonConvert.ToString(line) + ",\"ok\":true}";

    public void Submit(QueuedCall call)
    {
        _deadlines.Watch(call);
        _calls.Add(call);
        _added.Release();
    }

    public CallOutcome RunLine(string requestJson, double queueWaitMs)
    {
        Interlocked.Increment(ref _ran);
        return new CallOutcome(ReplyTo(requestJson), null);
    }

    /// <summary>The ids of version-2 calls in the order the fake main thread ran them.</summary>
    internal ConcurrentQueue<string> RunOrder { get; } = new ConcurrentQueue<string>();

    /// <summary>A version-2 call is answered with its method and params as result, after params.hold_ms if given.</summary>
    public CallOutcome RunCall(CallRequest call, double queueWaitMs)
    {
        Interlocked.Increment(ref _ran);
        RunOrder.Enqueue(call.Id);
        int hold = call.Params?.Value<int?>("hold_ms") ?? 0;
        if (hold > 0)
        {
            Thread.Sleep(hold);
        }

        Newtonsoft.Json.Linq.JObject result = new Newtonsoft.Json.Linq.JObject
        {
            ["method"] = call.Method,
            ["params"] = call.Params ?? new Newtonsoft.Json.Linq.JObject()
        };
        int filler = call.Params?.Value<int?>("reply_bytes") ?? 0;
        if (filler > 0)
        {
            result["filler"] = new string('f', filler);
        }
        string reply = Newtonsoft.Json.JsonConvert.SerializeObject(new Newtonsoft.Json.Linq.JObject
        {
            ["type"] = "reply", ["id"] = call.Id, ["ok"] = true, ["shaped"] = call.Shape != null, ["result"] = result,
            ["elapsed_ms"] = 0.01, ["queue_ms"] = Math.Round(queueWaitMs, 2), ["frame"] = 1
        }, Newtonsoft.Json.Formatting.None);
        return new CallOutcome(reply, call.Method);
    }

    /// <summary>Takes every queued call now, holding each started one for holdMs before answering.</summary>
    internal void RunPending(int holdMs = 0)
    {
        while (_calls.TryTake(out QueuedCall? call) && call != null)
        {
            RunOne(call, holdMs);
        }
    }

    public void Dispose() => _stopped = true;

    private void RunOne(QueuedCall call, int holdMs)
    {
        if (!call.State.TryStart())
        {
            return;
        }

        Thread.Sleep(holdMs);
        CallOutcome outcome = call.Run(this, 0);
        if (call.State.TryFinish())
        {
            call.Deliver(outcome.Reply, outcome.Method);
        }
    }
}

/// <summary>The repository's catalogue.json, loaded as the mod loads it.</summary>
internal static class TestCatalogue
{
    internal static readonly Lazy<StationGodMCP.Pure.Catalogue.CatalogueFile> File = new Lazy<StationGodMCP.Pure.Catalogue.CatalogueFile>(() =>
    {
        byte[] bytes = System.IO.File.ReadAllBytes(CatalogueChecks.CatalogueFiles.AssembledPath);
        return new StationGodMCP.Pure.Catalogue.CatalogueFile(bytes,
            StationGodMCP.Pure.Catalogue.Catalogue.Load(new UTF8Encoding(false).GetString(bytes)));
    });
}
