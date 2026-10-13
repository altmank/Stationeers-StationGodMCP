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
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The overlapped pipe on a real Windows named pipe in this process: line framing, reading and writing at once at both
/// ends, the first-line and call timeouts, and the instance limit.
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
        Assert.Equal(32, ProtocolSettings.DefaultMaxPipeConnections);
    }

    [Fact]
    public async Task TheServerWritesWhileItsReadWaits()
    {
        using PipeRig rig = new PipeRig();
        using NamedPipeClientStream client = await rig.ConnectAsync();
        Connection connection = rig.WaitConnected();
        Thread.Sleep(100);

        // The server's read waits for a hello that never comes: a write held behind it would never arrive.
        connection.Send("""{"pushed":true}""");
        string line = await ReadLineAsync(client);

        Assert.Equal("""{"pushed":true}""", line);
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

        // The pending read can end only with the welcome, which the hello brings: a write held behind it would hang.
        byte[] hello = Encoding.UTF8.GetBytes(Hello + "\n");
        Task write = client.WriteAsync(hello, 0, hello.Length);
        Assert.Same(pendingRead, await Task.WhenAny(pendingRead, Task.Delay(30000)));
        await write;
        int read = await pendingRead;

        Assert.StartsWith("""{"type":"welcome",""", Encoding.UTF8.GetString(buffer, 0, read));
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

        using V2Client next = await V2Client.Connect(rig);
        Assert.True((bool)next.Call("n", "read", TestVerbs.Clock())["ok"]!);
    }

    [Fact]
    public async Task BlankLinesDoNotStopTheFirstLineTimeout()
    {
        using PipeRig rig = new PipeRig(maxConnections: 1, firstLineMs: 300);
        using NamedPipeClientStream client = await rig.ConnectAsync();
        byte[] blank = Encoding.UTF8.GetBytes("\n\r\n  \n");
        byte[] buffer = new byte[16];
        Task<int> read = client.ReadAsync(buffer, 0, buffer.Length);

        // Blank lines keep coming until the connection closes: were each one a first line, it never would.
        while (!read.IsCompleted)
        {
            try
            {
                await client.WriteAsync(blank, 0, blank.Length);
            }
            catch (IOException)
            {
                break;
            }

            await Task.WhenAny(read, Task.Delay(20));
        }

        Assert.Same(read, await Task.WhenAny(read, Task.Delay(30000)));
        Assert.Equal(0, await read);
    }

    [Fact]
    public async Task ACallStartedJustBeforeItsDeadlineIsAnsweredWithItsResultOnly()
    {
        Scheduling.FakeClock clock = new Scheduling.FakeClock();
        using PipeRig rig = new PipeRig(mainThread: false, frameClock: clock, deadlineClock: clock);
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"call","id":"late","method":"read","params":{"what":"clock"},"deadline_ms":400}""");
        rig.Mod.WaitSubmitted(1);
        clock.Advance(300);
        rig.Deadlines.CheckNow();

        // It starts at 300 ms and runs to 700 ms; the deadline watch looks at it at 700 ms, past its deadline.
        rig.Mod.CallHook = (_, _) =>
        {
            clock.Advance(400);
            rig.Deadlines.CheckNow();
            return null;
        };
        rig.Mod.RunPending();
        JObject reply = client.Next();

        Assert.Equal("late", (string?)reply["id"]);
        Assert.True((bool)reply["ok"]!);
        rig.Deadlines.CheckNow();
        Assert.Null(client.NextOrNull(500));
        Assert.Equal(1, rig.Mod.Ran);
        Assert.Equal(0, rig.Deadlines.TimedOut);
    }

    [Fact]
    public async Task PastTheLimitAClientWaitsUntilAConnectionCloses()
    {
        using PipeRig rig = new PipeRig(maxConnections: 3);
        List<V2Client> held = new List<V2Client>();
        for (int index = 0; index < 3; index++)
        {
            V2Client client = await V2Client.Connect(rig);
            client.Call($"h{index}", "read", TestVerbs.Clock());
            held.Add(client);
        }

        NamedPipeClientStream waiting = new NamedPipeClientStream(".", rig.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Assert.Throws<TimeoutException>(() => waiting.Connect(500));

        held[0].Dispose();
        await waiting.ConnectAsync(5000);
        using V2Client next = new V2Client(waiting);
        next.Hello("waiting");
        Assert.Equal("welcome", (string?)next.Next()["type"]);
        Assert.True((bool)next.Call("w", "read", TestVerbs.Clock())["ok"]!);

        foreach (V2Client client in held)
        {
            client.Dispose();
        }
    }

    private const string Hello = """{"type":"hello","protocol":[2],"client":{"name":"pipe-test","version":"1"}}""";

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
}

/// <summary>
/// A pipe listener with a fake mod behind it, on a pipe name of its own; frameClock, when given, is the fake main
/// thread's clock, and the time a call holds passes on it instead of the wall clock's; deadlineClock, when given, is
/// the clock call deadlines are set and checked on.
/// </summary>
internal sealed class PipeRig : IDisposable
{
    private readonly DeadlineWatch _deadlines;
    private readonly PipeListener _listener;
    private readonly BlockingCollection<Connection> _connected = new BlockingCollection<Connection>();

    internal PipeRig(int maxConnections = 32, int firstLineMs = 10000, bool mainThread = true,
        ProtocolSettings? settings = null, SubscriptionHub? subscriptions = null,
        Func<SamplingTick>? tick = null, Func<RealTimeTick>? realTick = null, StreamStores? stores = null,
        int resumeWindowMs = 0, IMonotonicClock? resumeClock = null, HeldRestores? restores = null,
        Func<WorldFacts>? world = null, Scheduling.FakeClock? frameClock = null,
        Scheduling.FakeClock? deadlineClock = null, StationGodMCP.Pure.Verbs.VerbDispatcher? verbs = null)
    {
        _deadlines = new DeadlineWatch(deadlineClock);
        Name = "StationGodMCP-test-" + Guid.NewGuid().ToString("N");
        Mod = new FakeMod(mainThread, _deadlines, stores: stores, clock: frameClock, world: world);
        if (subscriptions != null)
        {
            Mod.Samples = subscriptions;
            Mod.BeforeFrame = () => subscriptions.BeginFrame(tick?.Invoke() ?? new SamplingTick(1, 0),
                realTick?.Invoke() ?? new RealTimeTick(1, 0, DateTimeOffset.UnixEpoch));
            Mod.CallHook = (call, queueMs) => SubscriptionHub.Handles(call.Request.Method)
                ? new CallOutcome(subscriptions.Run(call, queueMs), call.Request.Method)
                : IsSampleRun(call.Request)
                    ? subscriptions.StartSampleLogic(call, queueMs)
                    : null;
            Mod.ClosedHook = subscriptions.Closed;
            Mod.StreamEndedHook = subscriptions.StreamEnded;
            Mod.HeldHook = subscriptions.Held;
            Mod.ResumedHook = subscriptions.Resumed;
        }

        Holds = new ResumeHolds(resumeWindowMs, Mod, resumeClock);
        Mod.Holds = Holds;
        Host = new ProtocolHost(settings ?? new ProtocolSettings(maxConnections, firstLineMs), Mod, _deadlines,
            TestCatalogue.File.Value, subscriptions: subscriptions, holds: Holds, restores: restores,
            verbs: verbs ?? Verbs.TestVerbs.Every);
        _listener = new PipeListener(Name, Host);
        _listener.Connected += connection => _connected.Add(connection);
        _listener.Start();
    }

    internal string Name { get; }

    /// <summary>
    /// read {what: "sample"} sent with the sampler's own arguments (targets, duration_seconds, interval_seconds) on a
    /// rig that does not check options on the reader thread: the sampler's run over frames, before read sample has its
    /// verb handler.
    /// </summary>
    internal static bool IsSampleRun(CallRequest call) =>
        call.Method == "read" && (string?)call.Params?["what"] == "sample";

    internal FakeMod Mod { get; }

    internal ProtocolHost Host { get; }

    internal DeadlineWatch Deadlines => _deadlines;

    /// <summary>The resume tokens; off unless the rig was given a window.</summary>
    internal ResumeHolds Holds { get; }

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
/// Stands in for the dispatcher: a "main thread" that runs frames of the lane scheduler and answers each call with a
/// reply made from its method and params, or (mainThread false) runs no frame until a test says so. Hooks let a test start calls
/// that go on over later frames (Deferred) and give the lane its samples.
/// </summary>
internal sealed class FakeMod : ICallQueue, ICallRunner, IDisposable
{
    private readonly LaneScheduler _calls;
    private readonly SemaphoreSlim _added = new SemaphoreSlim(0);
    private readonly Thread? _thread;
    private readonly DeadlineWatch _deadlines;
    private readonly Scheduling.FakeClock? _clock;
    private readonly object _frame = new object();
    private volatile bool _stopped;
    private int _ran;
    private int _submitted;
    private int _holdMs;

    internal FakeMod(bool mainThread, DeadlineWatch deadlines, SchedulerSettings? settings = null,
        StreamStores? stores = null, Scheduling.FakeClock? clock = null, Func<WorldFacts>? world = null)
    {
        _deadlines = deadlines;
        _clock = clock;
        _calls = new LaneScheduler(settings ?? SchedulerSettings.Default, stores, clock, world);
        if (mainThread)
        {
            _thread = new Thread(() =>
            {
                while (!_stopped)
                {
                    _added.Wait(20);
                    RunPending();
                }
            }) { IsBackground = true };
            _thread.Start();
        }
    }

    internal int Ran => Volatile.Read(ref _ran);

    /// <summary>The sample lane each frame runs first; null for none.</summary>
    internal ISampleLane? Samples { get; set; }

    /// <summary>Before a call runs: its outcome when the hook took it (answered or deferred) instead.</summary>
    internal Func<ProtocolCall, double, CallOutcome?>? CallHook { get; set; }

    /// <summary>Connections closed, in order.</summary>
    internal ConcurrentQueue<object> ClosedSources { get; } = new ConcurrentQueue<object>();

    /// <summary>Called on the main thread at the start of each frame.</summary>
    internal Action? BeforeFrame { get; set; }

    /// <summary>The resume tokens whose windows each frame checks first, as StationGodMod.Update does.</summary>
    internal ResumeHolds? Holds { get; set; }

    /// <summary>Called (on the connection's thread) when a resume token's state is held.</summary>
    internal Action<string>? HeldHook { get; set; }

    /// <summary>Called (on the connection's thread) when a connection resumes a token.</summary>
    internal Action<string, object>? ResumedHook { get; set; }

    /// <summary>Owners held for a resume, in order.</summary>
    internal ConcurrentQueue<string> HeldOwners { get; } = new ConcurrentQueue<string>();

    /// <summary>Called on the main thread at the end of each frame.</summary>
    internal Action? AfterFrame { get; set; }

    /// <summary>Called (on the connection's reader thread) when a connection ends.</summary>
    internal Action<object>? ClosedHook { get; set; }

    /// <summary>Streams ended (stream_end, a slow client), in order, with their connection.</summary>
    internal ConcurrentQueue<(object Source, StationGodMCP.Pure.Protocol.StreamEnd End)> StreamEnds { get; } =
        new ConcurrentQueue<(object Source, StationGodMCP.Pure.Protocol.StreamEnd End)>();

    /// <summary>Called (on the thread that ended them) when streams end.</summary>
    internal Action<object, StationGodMCP.Pure.Protocol.StreamEnd>? StreamEndedHook { get; set; }

    public void Submit(QueuedCall call)
    {
        _deadlines.Watch(call);
        _calls.Add(call);
        lock (_submittedChanged)
        {
            _submitted++;
            Monitor.PulseAll(_submittedChanged);
        }

        _added.Release();
    }

    private readonly object _submittedChanged = new object();

    /// <summary>
    /// Waits until count calls in all have reached the queue, so the next frame takes them in: the reader has read
    /// every line up to the count-th call. Throws after 30 s, which only a hang takes.
    /// </summary>
    internal int Submitted
    {
        get
        {
            lock (_submittedChanged)
            {
                return _submitted;
            }
        }
    }

    internal void WaitSubmitted(int count)
    {
        lock (_submittedChanged)
        {
            while (_submitted < count)
            {
                if (!Monitor.Wait(_submittedChanged, 30000))
                {
                    throw new TimeoutException($"{_submitted} of {count} calls reached the queue");
                }
            }
        }
    }

    public void Withdraw(QueuedCall call) => _calls.Withdraw(call);

    public void StreamEnded(object source, StationGodMCP.Pure.Protocol.StreamEnd end)
    {
        _calls.EndStreams(source, end);
        StreamEndedHook?.Invoke(source, end);
        StreamEnds.Enqueue((source, end));
        _added.Release();
    }

    /// <summary>cursor_close messages, in order, with their connection's owner.</summary>
    internal ConcurrentQueue<(string Owner, string CursorId)> CursorCloses { get; } =
        new ConcurrentQueue<(string Owner, string CursorId)>();

    public void CursorClosed(string owner, string cursorId)
    {
        _calls.CloseCursor(owner, cursorId);
        CursorCloses.Enqueue((owner, cursorId));
        _added.Release();
    }

    /// <summary>release messages, in order, with their connection's owner.</summary>
    internal ConcurrentQueue<(string Owner, long Stream)> PinReleases { get; } =
        new ConcurrentQueue<(string Owner, long Stream)>();

    public void PinsReleased(string owner, long stream)
    {
        _calls.ReleasePins(owner, stream);
        PinReleases.Enqueue((owner, stream));
        _added.Release();
    }

    /// <summary>Owners whose session ended abruptly, in order.</summary>
    internal ConcurrentQueue<string> SessionEnds { get; } = new ConcurrentQueue<string>();

    public void SessionEnded(string owner)
    {
        _calls.EndSession(owner);
        SessionEnds.Enqueue(owner);
        _added.Release();
    }

    /// <summary>Runs work on the fake main thread, between frames.</summary>
    internal void OnMainThread(Action work)
    {
        lock (_frame)
        {
            work();
        }
    }

    public void OwnerHeld(string owner)
    {
        HeldHook?.Invoke(owner);
        HeldOwners.Enqueue(owner);
        _added.Release();
    }

    public void OwnerResumed(string owner, object source)
    {
        ResumedHook?.Invoke(owner, source);
        _added.Release();
    }

    public void Closed(object source)
    {
        _calls.Close(source);
        ClosedHook?.Invoke(source);
        ClosedSources.Enqueue(source);
        _added.Release();
    }

    /// <summary>The ids of calls in the order the fake main thread ran them.</summary>
    internal ConcurrentQueue<string> RunOrder { get; } = new ConcurrentQueue<string>();

    /// <summary>What each (owner, stream) has cost, as the lane scheduler counts it.</summary>
    internal StreamCosts Costs => _calls.Costs;

    /// <summary>
    /// A call is answered with its method and params as result, after params.hold_ms if given; stream_costs as the
    /// dispatcher answers it.
    /// </summary>
    public CallOutcome RunCall(ProtocolCall queued, double queueWaitMs)
    {
        Interlocked.Increment(ref _ran);
        CallRequest call = queued.Request;
        RunOrder.Enqueue(call.Id);
        Hold();
        if (CallHook?.Invoke(queued, queueWaitMs) is CallOutcome taken)
        {
            return taken;
        }

        if (StreamCostCalls.Handles(call.Method))
        {
            return new CallOutcome(new StreamCostCalls(_calls.Costs).Run(queued, queueWaitMs, 1), call.Method);
        }

        Spend(call.Params?.Value<int?>("hold_ms") ?? 0);

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

    /// <summary>
    /// Runs frames until one serves nothing, holding each started call for holdMs before answering; returns the
    /// frames' outcomes.
    /// </summary>
    internal List<FrameOutcome> RunPending(int holdMs = 0)
    {
        List<FrameOutcome> frames = new List<FrameOutcome>();
        lock (_frame)
        {
            _holdMs = holdMs;
            while (true)
            {
                FrameOutcome outcome = RunFrame();
                frames.Add(outcome);
                if (outcome.Served == 0 && outcome.ExpiredCalls == 0)
                {
                    break;
                }
            }

            _holdMs = 0;
        }

        return frames;
    }

    /// <summary>One frame, as StationGodMod.Update runs it.</summary>
    internal FrameOutcome RunFrame(bool jobHoldsTick = false)
    {
        lock (_frame)
        {
            Holds?.Tick();
            BeforeFrame?.Invoke();
            FrameOutcome outcome = _calls.RunFrame(jobHoldsTick, Samples, this);
            AfterFrame?.Invoke();
            return outcome;
        }
    }

    public void Dispose() => _stopped = true;

    private void Hold() => Spend(_holdMs);

    // ms of the main thread's time: on the fake clock when the scheduler runs on one, else asleep.
    private void Spend(int ms)
    {
        if (ms <= 0)
        {
            return;
        }

        if (_clock != null)
        {
            _clock.Advance(ms);
        }
        else
        {
            Thread.Sleep(ms);
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
