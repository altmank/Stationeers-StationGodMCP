#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// find thing's options as its handler reads them (the game's search needs the game): a prefab and an area narrow it to
/// one deck, near goes with radius (at most 1000 m around a point, any around the player), box is the other area, and
/// the paging defaults to the catalogue's 8 with the wire cursor carried.
/// </summary>
public sealed class FindThingTests
{
    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("find")!, JObject.Parse(options), out _);

    private static FindThingOptions Bound(string options) =>
        FindThingOptions.Bind(new OptionReader(FindThingOptions.Spec, JObject.Parse(options)));

    [Fact]
    public void ThePrefabAndAnAreaNarrowAFind()
    {
        Assert.Null(Check("""{"what": "thing", "prefab": "StructureFrame", "near": [100, 20, 50], "radius": 9}"""));
        Assert.Null(Check("""{"what": "thing", "prefab": "*Frame*", "box": [[90, 15, 40], [105, 25, 50]]}"""));
        Assert.NotNull(Check("""{"what": "thing", "near": [1, 2, 3], "radius": "far"}"""));

        Selector deck = Bound("""{"near": [100, 20, 50], "radius": 9}""").Selector;
        Assert.True(deck.AreaAround(null).Contains(new Vec3(107, 20, 50)));
        Assert.False(deck.AreaAround(null).Contains(new Vec3(110, 20, 50)));
        Assert.Empty(deck.AreaProblems(1000));
    }

    [Theory]
    [InlineData("""{"near": [1, 2, 3]}""", "near", "needs radius")]
    [InlineData("""{"radius": 5}""", "radius", "needs near")]
    [InlineData("""{"near": [1, 2, 3], "radius": 5, "box": [[0, 0, 0], [1, 1, 1]]}""", "box", "without near")]
    [InlineData("""{"near": [1, 2, 3], "radius": 5000}""", "radius", "at most 1000")]
    [InlineData("""{"near": "looking_at", "radius": 5000}""", "radius", "at most 1000")]
    public void AnIncompleteOrDoubleAreaIsAProblem(string options, string path, string problem)
    {
        List<OptionProblem> problems = Bound(options).Selector.AreaProblems(1000);

        OptionProblem found = Assert.Single(problems);
        Assert.Equal(path, found.Path);
        Assert.Contains(problem, found.Problem);
    }

    [Fact]
    public void AnyRadiusGoesAroundThePlayer()
    {
        Assert.Empty(Bound("""{"near": "player", "radius": 5000}""").Selector.AreaProblems(1000));
    }

    [Fact]
    public void ThePageIsTheCataloguesEightAndCarriesTheWireCursor()
    {
        Pure.Verbs.Paging plain = Bound("{}").Paging;
        Pure.Verbs.Paging opened = Bound("""{"cursor": "open", "limit": 500}""").Paging;
        Pure.Verbs.Paging later = Bound("""{"cursor": "k4"}""").Paging;

        Assert.Equal(8, plain.Limit);
        Assert.False(plain.OpensCursor);
        Assert.True(opened.OpensCursor);
        Assert.Null(opened.CursorId);
        Assert.Equal("k4", later.CursorId);
        Assert.Null(Check("""{"what": "thing", "cursor": "open"}"""));
        Assert.NotNull(Check("""{"what": "thing", "limit": 501}"""));
    }
}
