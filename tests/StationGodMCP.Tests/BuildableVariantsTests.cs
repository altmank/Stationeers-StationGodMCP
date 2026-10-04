#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>A prefab no kit builds names the buildable prefabs whose name holds its own.</summary>
public sealed class BuildableVariantsTests
{
    [Fact]
    public void TheLeverValveIsNamedForTheOneWayValve()
    {
        string[] buildable = { "StructurePipeValve", "StructurePipeOneWayValveLever", "StructurePipeStraight" };

        Assert.Equal(new[] { "StructurePipeOneWayValveLever" },
            BuildableVariants.Of("StructurePipeOneWayValve", buildable));
        Assert.Equal(" Buildable variants: StructurePipeOneWayValveLever.",
            BuildableVariants.Hint(BuildableVariants.Of("StructurePipeOneWayValve", buildable)));
    }

    [Fact]
    public void ShortestFirstAtMostFiveAndNothingWithoutAMatch()
    {
        string[] buildable = { "AbcLongest", "Abc2", "Abc1", "AbcX3", "Abc", "AbcY4", "AbcZ5", "Other" };

        Assert.Equal(new[] { "Abc1", "Abc2", "AbcX3", "AbcY4", "AbcZ5" }, BuildableVariants.Of("abc", buildable));
        Assert.Equal(string.Empty, BuildableVariants.Hint(BuildableVariants.Of("Nothing", buildable)));
    }
}
