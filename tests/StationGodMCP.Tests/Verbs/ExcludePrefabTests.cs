#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>find thing's exclude_prefab reads as prefab does, its problems at its own path.</summary>
public sealed class ExcludePrefabTests
{
    [Fact]
    public void PatternsReadAsPrefabDoes()
    {
        List<string> problems = new();

        PrefabFilter filter = PrefabFilter.Of(JToken.Parse("""["*Frame*", "*Wall*", 12345]"""), "exclude_prefab",
            (path, problem) => problems.Add(path))!;

        Assert.Empty(problems);
        Assert.True(filter.Keeps("StructureFrame", 1));
        Assert.True(filter.Keeps("StructureCompositeWall", 2));
        Assert.True(filter.Keeps("StructureTankSmall", 12345));
        Assert.False(filter.Keeps("StructureFurnace", 3));
    }

    [Fact]
    public void AnEntryThatDoesNotReadIsAProblemAtItsPath()
    {
        List<string> problems = new();

        PrefabFilter.Of(JToken.Parse("""["*Frame*", {"x": 1}]"""), "exclude_prefab", (path, problem) => problems.Add(path));
        PrefabFilter.Of(JToken.Parse("""true"""), "exclude_prefab", (path, problem) => problems.Add(path));

        Assert.Equal(new[] { "exclude_prefab[1]", "exclude_prefab" }, problems);
        Assert.Null(PrefabFilter.Of(null, "exclude_prefab", (path, problem) => problems.Add(path)));
    }
}
