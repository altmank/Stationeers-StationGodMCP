#nullable enable

using StationGodMCP.Pure.Protocol;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// The streams a connection has ended (never reused), kept as ranges so a hub's increasing run ids stay a handful of
/// entries; and StreamEnd, which says which call contexts an end covers.
/// </summary>
public sealed class EndedStreamsTests
{
    [Fact]
    public void AStreamIsEndedOnceAndStaysEnded()
    {
        EndedStreams ended = new EndedStreams();

        Assert.False(ended.Contains(3));
        Assert.True(ended.End(3));
        Assert.False(ended.End(3));
        Assert.True(ended.Contains(3));
        Assert.False(ended.Contains(2));
        Assert.False(ended.Contains(4));
    }

    [Fact]
    public void StreamZeroIsNeverEnded()
    {
        EndedStreams ended = new EndedStreams();

        Assert.False(ended.End(0));
        Assert.False(ended.Contains(0));
    }

    [Fact]
    public void IncreasingIdsEndedInAnyOrderCollapseToOneRange()
    {
        EndedStreams ended = new EndedStreams();
        int[] order = { 5, 1, 2, 4, 3, 6, 9, 8, 7, 10 };
        foreach (int id in order)
        {
            Assert.True(ended.End(id));
        }

        Assert.Equal(1, ended.Ranges);
        for (int id = 1; id <= 10; id++)
        {
            Assert.True(ended.Contains(id));
        }

        Assert.False(ended.Contains(11));
    }

    [Fact]
    public void AStreamStillOpenKeepsAHoleAndCostsOneRange()
    {
        EndedStreams ended = new EndedStreams();
        for (long id = 1; id <= 1000; id++)
        {
            if (id != 7)
            {
                ended.End(id);
            }
        }

        Assert.Equal(2, ended.Ranges);
        Assert.False(ended.Contains(7));
        Assert.True(ended.End(7));
        Assert.Equal(1, ended.Ranges);
    }

    [Fact]
    public void PastTheBoundTheLowestHoleIsClosed()
    {
        EndedStreams ended = new EndedStreams();
        for (long index = 0; index <= EndedStreams.MaximumRanges; index++)
        {
            ended.End(index * 2 + 1);
        }

        Assert.Equal(EndedStreams.MaximumRanges, ended.Ranges);
        Assert.True(ended.Contains(2));
        Assert.False(ended.Contains(4));
        Assert.True(ended.Contains(EndedStreams.MaximumRanges * 2 + 1));
    }

    [Fact]
    public void TheLargestStreamIdIsAccepted()
    {
        EndedStreams ended = new EndedStreams();

        Assert.True(ended.End(StreamEnd.MaximumStream));
        Assert.True(ended.Contains(StreamEnd.MaximumStream));
        Assert.Equal(9007199254740992L, StreamEnd.MaximumStream);
    }

    [Fact]
    public void AStreamsEndCoversOnlyThatStreamOfThatConnection()
    {
        StreamEnd end = StreamEnd.Of("c4", 2);

        Assert.True(end.Covers(new CallContext("c4", 2)));
        Assert.False(end.Covers(new CallContext("c4", 0)));
        Assert.False(end.Covers(new CallContext("c5", 2)));
        Assert.False(end.Covers(CallContext.None));
    }

    [Fact]
    public void AConnectionsEndCoversEveryStreamOfIt()
    {
        StreamEnd end = StreamEnd.AllOf("c4");

        Assert.True(end.Covers(new CallContext("c4", 0)));
        Assert.True(end.Covers(new CallContext("c4", 77)));
        Assert.False(end.Covers(new CallContext("c5", 0)));
        Assert.True(end.IsWholeConnection);
    }

    [Fact]
    public void AWorldChangeCoversEveryStreamAndNothingIsTheDefault()
    {
        Assert.True(StreamEnd.World.Covers(new CallContext("c9", 3)));
        Assert.True(StreamEnd.World.IsWorld);
        Assert.False(default(StreamEnd).Covers(new CallContext("c9", 3)));
    }
}
