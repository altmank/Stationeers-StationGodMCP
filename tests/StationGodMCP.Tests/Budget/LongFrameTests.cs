#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// Review D, R-D-28: a long frame's log line says how long the whole frame ran, StationGod's own Update by part, the
/// calls it ran and the GC collections, so the frame can be told StationGod's, the game's or a collection's.
/// </summary>
public sealed class LongFrameTests
{
    [Fact]
    public void AWholeFrameOverFiftyMsIsNamedWithStationGodsPartsCallsAndCollections()
    {
        LongFrames notes = new LongFrames("world model", "requests");
        Assert.Null(notes.Check(16.0, 16, 0.0)); // the first frame only sets the typical frame
        notes.Begin(81250, gcCollections: 16);
        notes.Section(0, 0.3);
        notes.Section(1, 2.2);
        notes.Call("find.thing", 1.9);
        notes.End(2.6, gcCollections: 16);

        string? line = notes.Check(wholeMs: 71.0, gcNow: 17, nowS: 100.0);

        Assert.Equal("Long frame 81250: 71.0 ms in all, StationGod 2.6 ms (world model 0.3, requests 2.2); calls: " +
                     "find.thing 1.9; GC collections in or after it: 1 (after StationGod's Update).", line);
    }

    [Fact]
    public void AShortFrameSaysNothingAndALongOneSaysItOnceAGap()
    {
        LongFrames notes = new LongFrames("requests");
        notes.Begin(1, 0);
        notes.End(1.0, 0);
        Assert.Null(notes.Check(20.0, 0, 10.0));

        notes.Begin(2, 0);
        notes.Section(0, 20.0);
        notes.End(20.0, 0);
        Assert.NotNull(notes.Check(21.0, 0, 20.0));
        Assert.Null(notes.Check(90.0, 0, 20.0 + LongFrames.MinimumGapS / 2));
        Assert.NotNull(notes.Check(90.0, 0, 20.0 + LongFrames.MinimumGapS));
    }

    [Fact]
    public void AServersOrdinaryFiftyMsFramesAreNotLongButAStallIs()
    {
        LongFrames notes = new LongFrames("requests");
        for (int frame = 0; frame < 200; frame++)
        {
            notes.Begin(frame, 0);
            notes.End(1.0, 0);
            Assert.Null(notes.Check(50.0 + frame % 5, 0, frame * 20.0));
        }

        notes.Begin(200, 0);
        notes.End(1.0, 0);
        Assert.NotNull(notes.Check(140.0, 0, 5000.0));
    }

    [Fact]
    public void CallsPastTheCapacityAreCounted()
    {
        LongFrames notes = new LongFrames("requests");
        notes.Begin(3, 0);
        for (int index = 0; index < LongFrames.CallCapacity + 2; index++)
        {
            notes.Call("read.logic", 0.1);
        }

        notes.End(30.0, 0);

        Assert.EndsWith("and 2 more; GC collections in or after it: 0.", notes.Check(30.0, 0, 0.0));
    }
}
