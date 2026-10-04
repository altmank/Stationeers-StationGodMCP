#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>advance_build_state's rule and the build_state view.</summary>
public sealed class BuildStepRuleTests
{
    private const int Kit = 1;
    private const int Plate = 2;
    private const int Cable = 3;
    private const int Welder = 9;

    // A console: the kit places state 0, plates make state 1, cable and a welder make state 2.
    private static readonly List<IReadOnlyList<BuildEntry>> Console = new List<IReadOnlyList<BuildEntry>>
    {
        new List<BuildEntry> { new BuildEntry(Kit, 1, false) },
        new List<BuildEntry> { new BuildEntry(Plate, 2, false) },
        new List<BuildEntry> { new BuildEntry(Cable, 3, false), new BuildEntry(Welder, 1, true) }
    };

    [Fact]
    public void AStepCostsOnlyTheStatesItAddsAndToolsAreListedNotCharged()
    {
        List<ItemCount> next = BuildStepRule.Cost(Console, 0, 1);
        Assert.Single(next);
        Assert.Equal(Plate, next[0].Item);
        Assert.Equal(2, next[0].Quantity);

        List<ItemCount> both = BuildStepRule.Cost(Console, 0, 2);
        Assert.Equal(new[] { Plate, Cable }, both.ConvertAll(count => count.Item));
        Assert.Empty(BuildStepRule.Tools(Console, 0, 1));
        Assert.Equal(new List<int> { Welder }, BuildStepRule.Tools(Console, 1, 2));
    }

    [Theory]
    [InlineData(0, 1, 1, false, BuildStepRefusal.SingleState)]
    [InlineData(-1, 3, 0, false, BuildStepRefusal.Broken)]
    [InlineData(2, 3, 3, false, BuildStepRefusal.Complete)]
    [InlineData(0, 3, 3, false, BuildStepRefusal.TargetOutOfRange)]
    [InlineData(1, 3, 1, false, BuildStepRefusal.TargetOutOfRange)]
    [InlineData(0, 3, 1, true, BuildStepRefusal.Damaged)]
    [InlineData(0, 3, 2, false, BuildStepRefusal.None)]
    public void TheRuleRefusesAsTheGameDoes(int current, int states, int target, bool damaged, object expected) =>
        Assert.Equal((BuildStepRefusal)expected, BuildStepRule.Refusal(current, states, target, damaged));

    [Fact]
    public void TheViewSaysCompleteAndLeavesNextOutWhenThereIsNone()
    {
        JObject half = JObject.Parse(WireCheck.New(new BuildStateView(1, 2,
            new List<BuildNeedView> { new BuildNeedView("ItemCableCoil", "Cable Coil", 3, false),
                new BuildNeedView("ItemWelder", "Welding Torch", null, true) })));
        Assert.False((bool)half["complete"]!);
        Assert.Equal(3, (int)half["next"]![0]!["quantity"]!);
        Assert.Null(half["next"]![1]!["quantity"]);

        JObject done = JObject.Parse(WireCheck.New(new BuildStateView(2, 2, null)));
        Assert.True((bool)done["complete"]!);
        Assert.Null(done["next"]);
    }
}
