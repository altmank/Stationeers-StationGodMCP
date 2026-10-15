#nullable enable

using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>
/// Step 6 (cost design A6): subscription reads shared within a sample tick. A read kept in a frame is taken by every
/// later sample of that frame; the next frame reads again; keys tell parts, slots and types apart.
/// </summary>
public sealed class SampleReadsTests
{
    private static readonly SampleReadKey Power = new SampleReadKey(1001, SampleReadPart.Logic, 0, 12);

    [Fact]
    public void ASecondSampleInTheFrameTakesTheFirstOnesRead()
    {
        SampleReads reads = new SampleReads();
        reads.Begin(7);
        Assert.False(reads.TryTake(Power, out _));
        reads.Keep(Power, new SampleRead(42.0, null));

        Assert.True(reads.TryTake(Power, out SampleRead shared));
        Assert.Equal(42.0, shared.Number);
        Assert.Equal(1, reads.Made);
        Assert.Equal(1, reads.Shared);
    }

    [Fact]
    public void TheNextFrameReadsAgainAndTheSameFrameKeeps()
    {
        SampleReads reads = new SampleReads();
        reads.Begin(7);
        reads.Keep(Power, new SampleRead(42.0, null));
        reads.Begin(7);
        Assert.True(reads.TryTake(Power, out _));

        reads.Begin(8);

        Assert.False(reads.TryTake(Power, out _));
    }

    [Fact]
    public void PartsSlotsAndTypesAreDifferentReads()
    {
        SampleReads reads = new SampleReads();
        reads.Begin(1);
        reads.Keep(Power, new SampleRead(1.0, null));

        Assert.False(reads.TryTake(new SampleReadKey(1001, SampleReadPart.SlotLogic, 0, 12), out _));
        Assert.False(reads.TryTake(new SampleReadKey(1001, SampleReadPart.Logic, 0, 13), out _));
        Assert.False(reads.TryTake(new SampleReadKey(1002, SampleReadPart.Logic, 0, 12), out _));
        Assert.False(reads.TryTake(new SampleReadKey(1001, SampleReadPart.SlotLogic, 1, 12), out _));
    }

    [Fact]
    public void ARefusalIsKeptAsTheReadToo()
    {
        SampleReads reads = new SampleReads();
        reads.Begin(1);
        object refusal = new object();
        reads.Keep(Power, new SampleRead(0.0, refusal));

        Assert.True(reads.TryTake(Power, out SampleRead kept));
        Assert.Same(refusal, kept.Other);
    }
}
