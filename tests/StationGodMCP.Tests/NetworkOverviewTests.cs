#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>connections: the summarize form's counts by prefab and colour, and colour on members.</summary>
public sealed class NetworkOverviewTests
{
    [Fact]
    public void TheTallyCountsByPrefabMostFirstWithColoursAndAsksEachNameOnce()
    {
        int asked = 0;
        MemberTally tally = new MemberTally();
        tally.Add("StructurePipeStraight", () => { asked++; return "Pipe (Straight)"; }, "pipe", "Blue");
        tally.Add("StructurePipeStraight", () => { asked++; return "Pipe (Straight)"; }, "pipe", "Blue");
        tally.Add("StructurePipeStraight", () => { asked++; return "Pipe (Straight)"; }, "pipe", null);
        tally.Add("StructurePipeCorner", () => { asked++; return "Pipe (Corner)"; }, "pipe", "Green");
        tally.Add("StructureActiveVent", () => { asked++; return "Active Vent"; }, "device", null);

        List<MemberPrefabCount> counts = tally.MostFirst();

        Assert.Equal(new[] { "StructurePipeStraight", "StructureActiveVent", "StructurePipeCorner" },
            counts.ConvertAll(count => count.Prefab));
        Assert.Equal(3, counts[0].Count);
        Assert.Equal(2, counts[0].Colors!["Blue"]);
        Assert.Null(counts[1].Colors);
        Assert.Equal(3, asked);
    }

    [Fact]
    public void TheOverviewWiresCountsDevicesAndOpenEnds()
    {
        MemberTally tally = new MemberTally();
        tally.Add("StructurePipeStraight", () => "Pipe (Straight)", "pipe", "Blue");
        NetworkMemberView vent = new NetworkMemberView(new ThingView(new ThingId(7), "StructureActiveVent",
            "Active Vent"), "device", new PositionView(1, 2, 3), null, null, ThingColorView.Of(3, "Blue", false));
        NetworkMemberView loose = new NetworkMemberView(new ThingView(new ThingId(8), "StructurePipeStraight",
            "Pipe (Straight)"), "pipe", new PositionView(1, 2, 4), new List<int> { 1 });

        JObject wire = JObject.Parse(WireCheck.New(new NetworkOverviewView(new NetworkRefView("pipe", new ThingId(5)),
            null, 1, 1, tally.MostFirst().ConvertAll(count => new PrefabCountView(count)),
            new List<NetworkMemberView> { vent }, new List<NetworkMemberView> { loose }, 1, BrokenNeighbourReport.None)));

        Assert.Equal("{\"prefab_name\":\"StructurePipeStraight\",\"display_name\":\"Pipe (Straight)\",\"member\":\"pipe\"," +
                     "\"count\":1,\"colors\":{\"Blue\":1}}", wire["by_prefab"]![0]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("Blue", (string)wire["devices"]![0]!["color"]!["name"]!);
        Assert.Null(wire["open_ends"]![0]!["color"]);
        Assert.Equal(1, (int)wire["open_ends"]![0]!["open_ends"]![0]!);
        Assert.Equal(1, (int)wire["open_end_count"]!);
    }
}
