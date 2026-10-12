#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// cursor_close on the wire (protocol.md, Walks): no reply, like cancel and stream_end; posted with the connection's
/// client_id through the scheduler's inbox and told to the cursor stores at the start of the next frame.
/// </summary>
public sealed class CursorCloseWireTests
{
    [Fact]
    public async Task WelcomeListsTheCursorsFeature()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);

        Assert.Contains("cursors", client.Welcome["features"]!.Values<string>());
    }

    [Fact]
    public async Task ACursorCloseHasNoReplyAndReachesTheMainThreadWithItsConnection()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"cursor_close","cursor_id":"k12"}""");

        Stopwatch watch = Stopwatch.StartNew();
        while (rig.Mod.CursorCloses.IsEmpty && watch.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }

        Assert.True(rig.Mod.CursorCloses.TryDequeue(out (string ConnectionId, string CursorId) closed));
        Assert.Equal(((string)client.Welcome["client_id"]!, "k12"), closed);
        Assert.Null(client.NextOrNull(200));
    }

    [Theory]
    [InlineData("""{"type":"cursor_close"}""")]
    [InlineData("""{"type":"cursor_close","cursor_id":12}""")]
    [InlineData("""{"type":"cursor_close","cursor_id":"k1","stream":1}""")]
    public async Task ACursorCloseWithoutAStringIdOrWithAnotherKeyIsAProtocolError(string line)
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(line);

        Assert.Equal("protocol_error", (string?)client.Next()["error"]!["code"]);
        Assert.Equal("protocol_error", (string?)client.Next()["reason"]);
        Assert.True(rig.Mod.CursorCloses.IsEmpty);
    }

    [Fact]
    public void TheSchedulerTellsTheCursorStoresAtTheNextFrame()
    {
        StreamStores stores = new StreamStores();
        WalkCursors cursors = new WalkCursors();
        stores.Register(cursors);
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default, stores);
        string id = cursors.Open(new CallContext("c1", 2), new long[] { 1, 2 }, 1)!;

        scheduler.CloseCursor("c1", id);
        Assert.Equal(1, cursors.Count);
        scheduler.RunFrame(false, null, new IdleRunner());

        Assert.Equal(0, cursors.Count);
    }

    [Fact]
    public void ACursorCloseReachesOnlyCursorStoresAndAThrowingOneDoesNotStopTheOthers()
    {
        StreamStores stores = new StreamStores();
        RecordingCursors first = new RecordingCursors(throws: true);
        RecordingCursors second = new RecordingCursors(throws: false);
        stores.Register(new PlainStore());
        stores.Register(first);
        stores.Register(second);

        stores.CloseCursor("c3", "k7");

        Assert.Equal(new[] { ("c3", "k7") }, second.Closed);
    }

    private static PipeRig Loose() => new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));

    private sealed class IdleRunner : ICallRunner
    {
        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException("no calls");
    }

    private sealed class PlainStore : IStreamStore
    {
        public void Ended(StreamEnd end)
        {
        }
    }

    private sealed class RecordingCursors : ICursorStore
    {
        private readonly bool _throws;

        internal RecordingCursors(bool throws) => _throws = throws;

        internal List<(string, string)> Closed { get; } = new List<(string, string)>();

        public void Ended(StreamEnd end)
        {
        }

        public void Close(string connectionId, string cursorId)
        {
            if (_throws)
            {
                throw new InvalidOperationException("broken store");
            }

            Closed.Add((connectionId, cursorId));
        }
    }
}
