#nullable enable

using StationGodMCP.Api.Shared;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The line of jobs waiting for the job slot: removing one waiting job by id from the front, the middle or the back
/// hands back what starts it and moves the jobs behind it up by one; an id that is not waiting removes nothing.
/// </summary>
public sealed class JobLineTests
{
    [Fact]
    public void RemovingTheFrontJobMovesEveryOtherUpByOne()
    {
        JobLine<string> line = Line("upgrade-1", "upgrade-2", "upgrade-3");

        Assert.True(line.Remove("upgrade-1", out string start));

        Assert.Equal("start upgrade-1", start);
        Assert.Equal(2, line.Count);
        Assert.Null(line.PositionOf("upgrade-1"));
        Assert.Equal(1, line.PositionOf("upgrade-2"));
        Assert.Equal(2, line.PositionOf("upgrade-3"));
    }

    [Fact]
    public void RemovingAMiddleJobMovesOnlyTheJobsBehindItUp()
    {
        JobLine<string> line = Line("upgrade-1", "upgrade-2", "upgrade-3", "upgrade-4");

        Assert.True(line.Remove("upgrade-2", out string start));

        Assert.Equal("start upgrade-2", start);
        Assert.Equal(1, line.PositionOf("upgrade-1"));
        Assert.Null(line.PositionOf("upgrade-2"));
        Assert.Equal(2, line.PositionOf("upgrade-3"));
        Assert.Equal(3, line.PositionOf("upgrade-4"));
    }

    [Fact]
    public void RemovingTheBackJobLeavesTheOthersWhereTheyStand()
    {
        JobLine<string> line = Line("upgrade-1", "upgrade-2", "upgrade-3");

        Assert.True(line.Remove("upgrade-3", out string start));

        Assert.Equal("start upgrade-3", start);
        Assert.Equal(1, line.PositionOf("upgrade-1"));
        Assert.Equal(2, line.PositionOf("upgrade-2"));
        Assert.Null(line.PositionOf("upgrade-3"));
    }

    [Fact]
    public void AnIdThatIsNotWaitingRemovesNothing()
    {
        JobLine<string> line = Line("upgrade-1", "upgrade-2");

        Assert.False(line.Remove("upgrade-9", out string start));

        Assert.Null(start);
        Assert.Equal(2, line.Count);
        Assert.Equal(1, line.PositionOf("upgrade-1"));
        Assert.Equal(2, line.PositionOf("upgrade-2"));
        Assert.False(new JobLine<string>(4).Remove("upgrade-1", out _));
    }

    [Fact]
    public void ARemovedJobFreesItsPlaceInAFullLineAndIsNeverTaken()
    {
        JobLine<string> line = Line("upgrade-1", "upgrade-2");
        Assert.True(line.IsFull);

        Assert.True(line.Remove("upgrade-1", out _));

        Assert.False(line.IsFull);
        Assert.True(line.TryTake(out string next, out _));
        Assert.Equal("upgrade-2", next);
        Assert.False(line.TryTake(out _, out _));
    }

    private static JobLine<string> Line(params string[] ids)
    {
        JobLine<string> line = new JobLine<string>(ids.Length);
        foreach (string id in ids)
        {
            Assert.True(line.Add(id, "start " + id));
        }

        return line;
    }
}
