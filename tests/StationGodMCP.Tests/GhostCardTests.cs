#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The ghost tooltip's card without the game: the standing Lattice's findings give (fails over waits over warns),
/// the title, needed materials added up and folded, ports counted by type in words.
/// </summary>
public sealed class GhostCardTests
{
    [Theory]
    [InlineData((int)GhostStanding.Planned, true, true, true, true, (int)GhostStanding.Blocked)]
    [InlineData((int)GhostStanding.Planned, false, true, true, true, (int)GhostStanding.Waiting)]
    [InlineData((int)GhostStanding.Planned, false, false, true, true, (int)GhostStanding.Warned)]
    [InlineData((int)GhostStanding.Planned, false, false, false, true, (int)GhostStanding.Ready)]
    [InlineData((int)GhostStanding.Planned, false, false, false, false, (int)GhostStanding.Planned)]
    [InlineData((int)GhostStanding.Started, false, true, true, true, (int)GhostStanding.Started)]
    [InlineData((int)GhostStanding.Started, true, false, false, true, (int)GhostStanding.Blocked)]
    [InlineData((int)GhostStanding.Stale, true, false, false, true, (int)GhostStanding.Stale)]
    public void FindingsGiveTheStanding(int state, bool fails, bool waits, bool warns, bool judged, int expected) =>
        Assert.Equal((GhostStanding)expected, GhostCard.FromFindings((GhostStanding)state, fails, waits, warns, judged));

    [Fact]
    public void TheLabelIsTheTitleElseThePiece()
    {
        Assert.Equal("Fuel mixer", new GhostCard(4, "Volume Pump", "Fuel mixer").Title);
        GhostCard plain = new GhostCard(4, "Volume Pump", "");
        Assert.Equal("Volume Pump", plain.Title);
        Assert.Null(plain.Label);
    }

    [Fact]
    public void NeededItemsAddUpAndFold()
    {
        GhostCard card = new GhostCard(1, "Wall", null);
        card.Need("Steel Sheets", 2);
        card.Need("Steel Sheets", 1);
        for (int index = 0; index < GhostCard.ShownItems + 1; index++)
        {
            card.Need("Item " + index, 1);
        }

        List<string> lines = card.NeededLines();
        Assert.Equal("3 x Steel Sheets", lines[0]);
        Assert.Equal(GhostCard.ShownItems + 1, lines.Count);
        Assert.Equal("and 2 more", lines[lines.Count - 1]);
    }

    [Fact]
    public void PortsAreCountedByTypeInWords()
    {
        GhostCard card = new GhostCard(1, "Pump", null);
        card.CountPorts(new[]
        {
            new GhostPort(0, new Vec3(0, 0, 0), "+x", "Pipe", "Input"),
            new GhostPort(1, new Vec3(0, 0, 0), "-x", "Pipe", "Output"),
            new GhostPort(2, new Vec3(0, 0, 0), "+y", "PowerAndData", null),
        });
        Assert.Equal("2 Pipe, 1 Power and data", card.PortsLine());
    }

    [Theory]
    [InlineData("PowerAndData", "Power and data")]
    [InlineData("PipeLiquid", "Pipe liquid")]
    [InlineData("HPPipe", "HPPipe")]
    [InlineData("chute_end", "Chute end")]
    public void NamesReadAsWords(string name, string words) => Assert.Equal(words, GhostCard.Words(name));

    [Fact]
    public void LongTextIsCut()
    {
        Assert.Equal("short", GhostCard.Short("short", 10));
        Assert.Equal("abcdef...", GhostCard.Short("abcdefghijklmnop", 9));
    }
}
