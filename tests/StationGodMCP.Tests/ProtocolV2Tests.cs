#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Protocol version 2 on a real named pipe with a fake main thread: hello and welcome, calls by id several at a time,
/// the order rule, ids, cancel and deadlines, framing limits, protocol errors, events, shutdown and slow clients.
/// </summary>
public sealed class ProtocolV2Tests
{
    [Theory]
    [InlineData("""{"type":"hello","protocol":[2],"client":{"name":"x"}}""", true)]
    [InlineData("""  {"client":{"name":"x"},"type":"hello"}  """, true)]
    [InlineData("""{"id":"1","method":"hello","params":{}}""", false)]
    [InlineData("""{"id":"1","method":"game_clock"}""", false)]
    [InlineData("{}", false)]
    [InlineData("hello there", false)]
    [InlineData("""{"type":"helloo"}""", false)]
    public void TheFirstLineChoosesTheProtocol(string line, bool version2)
    {
        Assert.Equal(version2, FirstLine.StartsVersion2(line));
    }

    [Fact]
    public async Task WelcomeSaysWhoTheClientIsWhichServerAndTheLimits()
    {
        ServerFacts.Current = new ServerFacts("9.9.9", "TestPipe", true, new WorldFacts("0123456789abcdef", "save1", 3), "Running");
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig, "probe");

        JObject welcome = client.Welcome;
        Assert.Equal("welcome", (string?)welcome["type"]);
        Assert.Equal(2, (int)welcome["protocol"]!);
        Assert.StartsWith("c", (string?)welcome["client_id"]);
        Assert.Equal("anonymous", (string?)welcome["client"]);
        Assert.Equal("write", (string?)welcome["level"]);
        Assert.Equal(JTokenType.Null, welcome["cheat"]!["until_utc"]!.Type);
        Assert.False((bool)welcome["cheat"]!["standing"]!);
        Assert.Equal("TestPipe", (string?)welcome["server"]!["pipe_name"]);
        Assert.Equal("pipe", (string?)welcome["server"]!["transport"]);
        Assert.Equal("host", (string?)welcome["server"]!["role"]);
        Assert.Equal(ServerFacts.InstanceId, (string?)welcome["server"]!["instance_id"]);
        Assert.Equal("0123456789abcdef", (string?)welcome["server"]!["world"]!["id"]);
        Assert.Equal("save1", (string?)welcome["server"]!["world"]!["save"]);
        Assert.Equal(16, (int)welcome["limits"]!["max_in_flight"]!);
        Assert.Equal(4194304, (int)welcome["limits"]!["max_request_bytes"]!);
        Assert.Equal(16777216, (int)welcome["limits"]!["max_reply_bytes"]!);
        Assert.Contains("shape", welcome["features"]!.Values<string>());
        Assert.Contains("cancel", welcome["features"]!.Values<string>());
        byte[] file = File.ReadAllBytes(CatalogueChecks.CatalogueFiles.AssembledPath);
        Assert.Equal(CatalogueFile.HashOf(file), (string?)welcome["catalogue"]!["hash"]);
        Assert.Matches("^sha256:[0-9a-f]{64}$", (string?)welcome["catalogue"]!["hash"]);
        Assert.Equal(1, (int)welcome["catalogue"]!["protocol_methods"]!);
    }

    [Fact]
    public async Task ACallIsAnsweredByIdWithTheReplyEnvelope()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("a1", "game_clock", new JObject { ["x"] = 1 });

        Assert.Equal("reply", (string?)reply["type"]);
        Assert.Equal("a1", (string?)reply["id"]);
        Assert.True((bool)reply["ok"]!);
        Assert.Equal("game_clock", (string?)reply["result"]!["method"]);
        Assert.NotNull(reply["queue_ms"]);
    }

    [Fact]
    public async Task SixteenCallsMayBeInFlightAndTheSeventeenthIsRefusedAtOnce()
    {
        using PipeRig rig = new PipeRig(mainThread: false);
        using V2Client client = await V2Client.Connect(rig);
        for (int index = 0; index < 17; index++)
        {
            client.Send(Call($"b{index}", "game_clock"));
        }

        JObject refused = client.Next();
        Assert.Equal("b16", (string?)refused["id"]);
        Assert.Equal("too_many_in_flight", (string?)refused["error"]!["code"]);
        Assert.Equal(16, (int)refused["error"]!["data"]!["limit"]!);

        rig.Mod.RunPending();
        HashSet<string> answered = new HashSet<string>();
        for (int index = 0; index < 16; index++)
        {
            Assert.True(answered.Add((string)client.Next()["id"]!));
        }

        Assert.Equal(16, answered.Count);
        Assert.Null(client.NextOrNull(300));
    }

    [Fact]
    public async Task AnIdInFlightCannotBeUsedAgainButOneAnsweredCan()
    {
        using PipeRig rig = new PipeRig(mainThread: false);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("d", "game_clock"));
        client.Send(Call("d", "game_clock"));

        JObject duplicate = client.Next();
        Assert.Equal("duplicate_id", (string?)duplicate["error"]!["code"]);
        Assert.Equal("d", (string?)duplicate["id"]);

        rig.Mod.RunPending();
        Assert.True((bool)client.Next()["ok"]!);
        client.Send(Call("d", "game_clock"));
        Thread.Sleep(100);
        rig.Mod.RunPending();
        Assert.True((bool)client.Next()["ok"]!);
    }

    [Fact]
    public void EachConnectionGetsATurnPerRound()
    {
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default);
        List<string> order = new List<string>();
        object a = new object();
        object b = new object();
        for (int index = 0; index < 16; index++)
        {
            scheduler.Add(new TestCall(a, $"a{index}", isWrite: false, order));
        }

        scheduler.Add(new TestCall(b, "b0", isWrite: false, order));

        FrameOutcome outcome = scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "a0", "b0", "a1" }, order.Take(3));
        Assert.Equal(17, order.Count);
        Assert.Equal(17, outcome.LightCalls);
    }

    [Fact]
    public void ACancelledWriteHoldsNothingBack()
    {
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default);
        List<string> order = new List<string>();
        object a = new object();
        TestCall write = new TestCall(a, "w", isWrite: true, order);
        scheduler.Add(write);
        scheduler.Add(new TestCall(a, "r", isWrite: false, order));
        Assert.True(write.Drop("cancelled"));
        scheduler.Withdraw(write);

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "r" }, order);
    }

    [Fact]
    public void AWriteWaitsForTheEarlierReadsOfItsConnection()
    {
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default);
        List<string> order = new List<string>();
        object a = new object();
        scheduler.Add(new TestCall(a, "r1", isWrite: false, order));
        scheduler.Add(new TestCall(a, "w", isWrite: true, order));
        scheduler.Add(new TestCall(a, "r2", isWrite: false, order));

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "r1", "w", "r2" }, order);
    }

    [Fact]
    public void ACallOverMaxInFlightIsRefusedUnrun()
    {
        LaneScheduler scheduler = new LaneScheduler(new SchedulerSettings(4, 1.5, 1, 10, 2));
        List<string> order = new List<string>();
        object a = new object();
        TestCall[] calls = Enumerable.Range(0, 3).Select(index => new TestCall(a, $"c{index}", false, order)).ToArray();
        foreach (TestCall call in calls)
        {
            scheduler.Add(call);
        }

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "c0", "c1" }, order);
        Assert.Equal("refused too_many_in_flight", calls[2].Answer);
    }

    [Fact]
    public void AClosedConnectionsCallsDoNotRun()
    {
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default);
        List<string> order = new List<string>();
        object a = new object();
        object b = new object();
        scheduler.Add(new TestCall(a, "a0", false, order));
        scheduler.Add(new TestCall(b, "b0", false, order));
        scheduler.Close(a);

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "b0" }, order);
    }

    [Fact]
    public void AHeavyCallRunsAfterTheLightOnes()
    {
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default);
        List<string> order = new List<string>();
        object a = new object();
        object b = new object();
        scheduler.Add(new TestCall(a, "survey", false, order, CostClass.World));
        scheduler.Add(new TestCall(b, "read", false, order));

        FrameOutcome outcome = scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "read", "survey" }, order);
        Assert.Equal(1, outcome.LightCalls);
        Assert.Equal(1, outcome.HeavyCalls);
    }

    [Fact]
    public void TheSampleLaneRunsFirst()
    {
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default);
        List<string> order = new List<string>();
        scheduler.Add(new TestCall(new object(), "read", false, order));

        FrameOutcome outcome = scheduler.RunFrame(false, new OneSample(order), new NoRunner());

        Assert.Equal(new[] { "sample", "read" }, order);
        Assert.Equal(1, outcome.Samples);
    }

    [Fact]
    public void AV1LineIsOrderedWhateverItsClass()
    {
        CallProfile profile = CallProfiles.OfLine(TestCatalogue.File.Value,
            """{"id":"1","method":"read_logic","params":{"reference_id":"1","logic_type":"On"}}""");

        Assert.True(profile.IsOrdered);
        Assert.Equal("read_logic", profile.Method);
        Assert.False(CallProfiles.Of(TestCatalogue.File.Value, "read_logic", new JObject()).IsOrdered);
        Assert.Equal(CostClass.World, CallProfiles.Of(TestCatalogue.File.Value, "grid_survey", new JObject()).Cost);
    }

    [Fact]
    public async Task AReadSentAfterAWriteRunsAfterIt()
    {
        using PipeRig rig = new PipeRig(mainThread: false);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("w", "write_logic", new JObject { ["reference_id"] = "1", ["logic_type"] = "Setting", ["value"] = 7 }));
        client.Send(Call("r", "read_logic", new JObject { ["reference_id"] = "1", ["logic_type"] = "Setting" }));
        Thread.Sleep(100);

        rig.Mod.RunPending();
        client.Next();
        client.Next();

        Assert.Equal(new[] { "w", "r" }, rig.Mod.RunOrder.ToArray());
    }

    [Fact]
    public async Task CancelDropsACallNotStartedAndNothingElse()
    {
        using PipeRig rig = Loose(mainThread: false);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("c1", "game_clock"));
        client.Send("""{"type":"cancel","id":"c1"}""");
        client.Send("""{"type":"cancel","id":"nobody"}""");

        JObject cancelled = client.Next();
        Assert.Equal("c1", (string?)cancelled["id"]);
        Assert.Equal("cancelled", (string?)cancelled["error"]!["code"]);
        Assert.Null(cancelled["elapsed_ms"]);
        Assert.Null(cancelled["frame"]);
        rig.Mod.RunPending();
        Assert.Equal(0, rig.Mod.Ran);
        Assert.Null(client.NextOrNull(300));

        client.Send(Call("c2", "game_clock", new JObject { ["hold_ms"] = 400 }));
        Thread.Sleep(100);
        Task running = Task.Run(() => rig.Mod.RunPending());
        Thread.Sleep(100);
        client.Send("""{"type":"cancel","id":"c2"}""");
        await running;
        JObject answered = client.Next();
        Assert.True((bool)answered["ok"]!);
        Assert.Null(client.NextOrNull(300));
    }

    [Fact]
    public async Task ACallNotStartedByItsDeadlineIsGameTimeout()
    {
        using PipeRig rig = new PipeRig(mainThread: false);
        using V2Client client = await V2Client.Connect(rig);
        Stopwatch watch = Stopwatch.StartNew();
        client.Send("""{"type":"call","id":"t","method":"game_clock","deadline_ms":200}""");

        JObject reply = client.Next();

        Assert.Equal("game_timeout", (string?)reply["error"]!["code"]);
        Assert.InRange(watch.ElapsedMilliseconds, 150, 2000);
        rig.Mod.RunPending();
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Theory]
    [InlineData("""{"type":"call","id":"x","method":"game_clock","deadline_ms":50}""")]
    [InlineData("""{"type":"call","id":"x","method":"game_clock","params":[1]}""")]
    public async Task ABadCallIsRefusedAndTheConnectionStays(string line)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(line);

        Assert.Equal("invalid_argument", (string?)client.Next()["error"]!["code"]);
        Assert.True((bool)client.Call("y", "game_clock", new JObject())["ok"]!);
    }

    [Fact]
    public async Task LinesMayEndInCarriageReturnLineFeedAndBlankLinesAreIgnored()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.SendRaw("\r\n\n" + Call("f", "game_clock") + "\r\n");

        Assert.Equal("f", (string?)client.Next()["id"]);
    }

    [Fact]
    public async Task ALineOverTheLimitIsRefusedAndClosesTheConnection()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        // The server stops reading past the limit, so the rest of this write fails once it closes.
        Task sending = Task.Run(() =>
        {
            try
            {
                client.SendRaw("{\"type\":\"call\",\"id\":\"big\",\"method\":\"game_clock\",\"params\":{\"x\":\"" +
                               new string('a', 5 * 1024 * 1024) + "\"}}\n");
            }
            catch (IOException)
            {
                // Expected: the pipe broke under the write.
            }
        });

        JObject refused = client.Next();
        Assert.Equal("request_too_large", (string?)refused["error"]!["code"]);
        Assert.Equal(JTokenType.Null, refused["id"]!.Type);
        Assert.Equal(4194304, (int)refused["error"]!["data"]!["limit"]!);
        Assert.True(client.WaitClosed(5000));
        await sending;
    }

    [Theory]
    [InlineData("""{"type":"shout"}""")]
    [InlineData("""{"type":"call","id":"1","method":"game_clock","extra":true}""")]
    [InlineData("""{"type":"call","method":"game_clock"}""")]
    [InlineData("""{"type":"hello","protocol":[2],"client":{"name":"again"}}""")]
    [InlineData("""{"type":"auth","client":"x","proof":"00"}""")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task AMessageTheProtocolDoesNotAllowClosesTheConnection(string line)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(line);

        JObject refused = client.Next();
        Assert.Equal("protocol_error", (string?)refused["error"]!["code"]);
        Assert.Equal(line.Length <= 80 ? line : line.Substring(0, 80), (string?)refused["error"]!["data"]!["line_start"]);
        JObject goodbye = client.Next();
        Assert.Equal("goodbye", (string?)goodbye["type"]);
        Assert.Equal("protocol_error", (string?)goodbye["reason"]);
        Assert.True(client.WaitClosed(5000));
    }

    [Fact]
    public async Task NoCommonVersionIsRefused()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Open(rig);
        client.Send("""{"type":"hello","protocol":[3],"client":{"name":"future"}}""");

        JObject refused = client.Next();
        Assert.Equal("unsupported_protocol", (string?)refused["error"]!["code"]);
        Assert.Equal(new[] { 2 }, refused["error"]!["data"]!["supported"]!.Values<int>());
        Assert.True(client.WaitClosed(5000));
    }

    [Fact]
    public async Task TheCatalogueMethodAnswersTheCatalogue()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("cat", "catalogue", new JObject());

        JToken expected = JToken.Parse(File.ReadAllText(CatalogueChecks.CatalogueFiles.AssembledPath));
        Assert.True(JToken.DeepEquals(expected, reply["result"]), "the catalogue differs");
        Assert.False((bool)reply["shaped"]!);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task WithVersionTwoOffAHelloIsAnsweredAsVersionOne()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, protocol2: false));
        using V2Client client = await V2Client.Open(rig);
        string hello = """{"type":"hello","protocol":[2],"client":{"name":"x"}}""";
        client.Send(hello);

        Assert.Equal(JObject.Parse(FakeMod.ReplyTo(hello)), client.Next());
    }

    [Fact]
    public async Task EveryVersionTwoConnectionHearsOfANewWorld()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        rig.Host.WorldChanged(new WorldFacts("fedcba9876543210", null, 4));

        JObject changed = client.Next();
        Assert.Equal("event", (string?)changed["type"]);
        Assert.Equal("world_changed", (string?)changed["event"]);
        Assert.Equal("fedcba9876543210", (string?)changed["world"]!["id"]);
        Assert.Equal(JTokenType.Null, changed["world"]!["save"]!.Type);
    }

    [Fact]
    public async Task ShuttingDownAnswersWaitingCallsAndSaysGoodbye()
    {
        using PipeRig rig = new PipeRig(mainThread: false);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("s", "game_clock"));
        Thread.Sleep(100);

        rig.Host.CloseAll("shutting_down", 2000);

        Assert.Equal("shutting_down", (string?)client.Next()["error"]!["code"]);
        JObject goodbye = client.Next();
        Assert.Equal("shutting_down", (string?)goodbye["reason"]);
        Assert.True(client.WaitClosed(5000));
        rig.Mod.RunPending();
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task ASilentConnectionGetsAPing()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, pingAfterMilliseconds: 300));
        using V2Client client = await V2Client.Connect(rig);

        JObject ping = client.Next(3000);

        Assert.Equal("event", (string?)ping["type"]);
        Assert.Equal("ping", (string?)ping["event"]);
    }

    [Fact]
    public async Task AReplyOfSixteenMegabytesReachesAReadingClient()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("big", "game_clock", new JObject { ["reply_bytes"] = 16 * 1024 * 1024 - 4096 }, 20000);

        Assert.Equal(16 * 1024 * 1024 - 4096, ((string)reply["result"]!["filler"]!).Length);
    }

    [Fact]
    public async Task AClientThatReadsNothingIsClosedAsSlow()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, slowClientMilliseconds: 1000, strictArguments: false));
        using V2Client client = await V2Client.Connect(rig);
        client.StopReading();
        for (int index = 0; index < 4; index++)
        {
            client.Send(Call($"s{index}", "game_clock", new JObject { ["reply_bytes"] = 1024 * 1024 }));
        }

        Stopwatch watch = Stopwatch.StartNew();
        while (rig.Host.Open.Count > 0 && watch.ElapsedMilliseconds < 10000)
        {
            Thread.Sleep(50);
        }

        Assert.Empty(rig.Host.Open);
    }

    [Fact]
    public void EveryWorldThatStartsRunningGetsANewId()
    {
        WorldScope scope = new WorldScope();
        scope.Observe(true);
        string first = scope.WorldId;
        Assert.True(scope.Entered);
        scope.Observe(true);
        Assert.False(scope.Entered);
        Assert.Equal(first, scope.WorldId);
        scope.Observe(false);
        scope.Observe(true);

        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.NotEqual(first, scope.WorldId);
        Assert.NotEqual(ServerFacts.RandomHex(8), ServerFacts.RandomHex(8));
    }

    // The fake mod's own params (hold_ms, reply_bytes) are no method's arguments: these rigs check calls as version 1.
    private static PipeRig Loose(bool mainThread = true) =>
        new PipeRig(mainThread: mainThread, settings: new ProtocolSettings(32, strictArguments: false));

    private static string Call(string id, string method, JObject? parameters = null) =>
        new JObject { ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JObject() }
            .ToString(Formatting.None);

    private sealed class TestCall : QueuedCall
    {
        private readonly object _source;
        private readonly List<string>? _ran;

        internal TestCall(object source, string name, bool isWrite, List<string>? ran = null,
            CostClass cost = CostClass.Instant)
            : base(DeadlineAfter(60000), new CallProfile(name, isWrite ? MethodClass.Write : MethodClass.Read,
                new CallCost(cost, 1)))
        {
            _source = source;
            Name = name;
            _ran = ran;
        }

        internal string Name { get; }

        internal string? Answer { get; private set; }

        internal override object? Source => _source;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
        {
            _ran?.Add(Name);
            return new CallOutcome(Name, null);
        }

        internal override string TimeoutReply() => "timeout";

        internal override string RefusalReply(string code, string message) => "refused " + code;

        internal override void Deliver(string reply, string? method) => Answer = reply;
    }

    private sealed class NoRunner : ICallRunner
    {
        public CallOutcome RunLine(LineCall call, double queueWaitMs) => throw new InvalidOperationException();

        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException();
    }

    private sealed class OneSample : ISampleLane
    {
        private readonly List<string> _order;
        private bool _taken;

        internal OneSample(List<string> order) => _order = order;

        public bool RunNextDueSample()
        {
            if (_taken)
            {
                return false;
            }

            _taken = true;
            _order.Add("sample");
            return true;
        }
    }
}

// <summary>A version-2 test client on a real pipe: a reader task collects every line the server sends.</summary>
internal sealed class V2Client : IDisposable
{
    private readonly Stream _pipe;
    private readonly BlockingCollection<string> _lines = new BlockingCollection<string>();
    private readonly ManualResetEventSlim _closed = new ManualResetEventSlim(false);
    private readonly ManualResetEventSlim _reading = new ManualResetEventSlim(true);

    internal V2Client(Stream pipe)
    {
        _pipe = pipe;
        Task.Run(ReadAll);
    }

    internal JObject Welcome { get; set; } = new JObject();

    internal static async Task<V2Client> Open(PipeRig rig)
    {
        NamedPipeClientStream pipe = await rig.ConnectAsync();
        return new V2Client(pipe);
    }

    internal static async Task<V2Client> Connect(PipeRig rig, string name = "tester")
    {
        V2Client client = await Open(rig);
        client.Hello(name);
        client.Welcome = client.Next();
        Assert.Equal("welcome", (string?)client.Welcome["type"]);
        return client;
    }

    /// <summary>Sends hello (with auth key when key is given) and, for a key, answers the challenge; returns the answer.</summary>
    internal JObject SignIn(string name, string? key, string transport, string? authName = null, string? badProof = null)
    {
        Hello(name, key != null);
        JObject answer = Next();
        if (key != null && (string?)answer["type"] == "challenge")
        {
            string proof = badProof ?? StationGodMCP.Pure.Access.KeyProof.Compute(Convert.FromBase64String(key),
                (string)answer["nonce"]!, authName ?? name, transport);
            Send(new JObject { ["type"] = "auth", ["client"] = authName ?? name, ["proof"] = proof }.ToString(Formatting.None));
            answer = Next();
        }

        if ((string?)answer["type"] == "welcome")
        {
            Welcome = answer;
        }

        return answer;
    }

    internal void Hello(string name, bool key = false)
    {
        JObject hello = new JObject
        {
            ["type"] = "hello", ["protocol"] = new JArray(2), ["client"] = new JObject { ["name"] = name, ["version"] = "1" }
        };
        if (key)
        {
            hello["auth"] = "key";
        }

        Send(hello.ToString(Formatting.None));
    }

    internal void Send(string line) => SendRaw(line + "\n");

    internal void SendRaw(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _pipe.Write(bytes, 0, bytes.Length);
    }

    internal JObject Next(int timeoutMs = 5000) =>
        NextOrNull(timeoutMs) ?? throw new TimeoutException("no line from the server");

    internal JObject? NextOrNull(int timeoutMs) =>
        _lines.TryTake(out string? line, timeoutMs) ? Parse(line!) : null;

    // As the wire has it: an ISO time stays a string.
    private static JObject Parse(string line)
    {
        using JsonTextReader reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    internal JObject Call(string id, string method, JObject parameters, int timeoutMs = 5000)
    {
        Send(new JObject { ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToString(Formatting.None));
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            JObject message = Next(Math.Max(1, timeoutMs - (int)watch.ElapsedMilliseconds));
            if ((string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }
    }

    internal void StopReading() => _reading.Reset();

    internal bool WaitClosed(int timeoutMs) => _closed.Wait(timeoutMs);

    public void Dispose() => _pipe.Dispose();

    private async Task ReadAll()
    {
        byte[] buffer = new byte[65536];
        List<byte> line = new List<byte>();
        try
        {
            while (true)
            {
                _reading.Wait();
                int read = await _pipe.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return;
                }

                for (int index = 0; index < read; index++)
                {
                    if (buffer[index] == (byte)'\n')
                    {
                        _lines.Add(Encoding.UTF8.GetString(line.ToArray()));
                        line.Clear();
                    }
                    else
                    {
                        line.Add(buffer[index]);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
        {
            // The server closed the pipe or the test disposed it.
        }
        finally
        {
            _closed.Set();
        }
    }
}
