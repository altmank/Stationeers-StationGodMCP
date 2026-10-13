#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// survey network's area filter (live: a 38 KB page of 200 of a cable network's 1,257 members): box keeps the members
/// inside it, near with radius those within a radius, and the options that do not fit are problems.
/// </summary>
public sealed class MemberAreaTests
{
    [Theory]
    [InlineData(717.0, 197.0, 678.0, true)]
    [InlineData(738.0, 201.0, 683.0, true)]
    [InlineData(725.5, 199.0, 680.0, true)]
    [InlineData(716.9, 199.0, 680.0, false)]
    [InlineData(725.0, 201.1, 680.0, false)]
    public void ABoxKeepsWhatLiesInsideCornersIncluded(double x, double y, double z, bool inside)
    {
        PointArea box = PointArea.Box(new Vec3(738, 201, 683), new Vec3(717, 197, 678));

        Assert.Equal(inside, box.Contains(new Vec3(x, y, z)));
    }

    [Fact]
    public void ARadiusKeepsWhatLiesWithinItBoundaryIncluded()
    {
        PointArea near = PointArea.Near(new Vec3(10, 0, 10), 5);

        Assert.True(near.Contains(new Vec3(13, 0, 14)));
        Assert.True(near.Contains(new Vec3(10, 5, 10)));
        Assert.False(near.Contains(new Vec3(13.1, 0, 14)));
    }

    [Fact]
    public void NoAreaOptionsKeepEverything()
    {
        Assert.True(MembersOf("{}").KeepsPosition(new Vec3(99999, -500, 3)));
    }

    [Fact]
    public void TheOptionsReachTheFilter()
    {
        NetworkMemberFilter box = MembersOf("""{"box":[[0,0,0],[2,2,2]]}""");
        NetworkMemberFilter near = MembersOf("""{"near":[0,0,0],"radius":1}""");

        Assert.True(box.KeepsPosition(new Vec3(1, 1, 1)));
        Assert.False(box.KeepsPosition(new Vec3(3, 1, 1)));
        Assert.True(near.KeepsPosition(new Vec3(0, 1, 0)));
        Assert.False(near.KeepsPosition(new Vec3(1, 1, 0)));
    }

    [Theory]
    [InlineData("""{"near":[0,0,0]}""", "near", "needs radius")]
    [InlineData("""{"radius":3}""", "radius", "needs near")]
    [InlineData("""{"box":[[0,0,0],[1,1,1]],"near":[0,0,0],"radius":1}""", "box", "without near")]
    public void AnAreaThatDoesNotFitIsAProblem(string options, string path, string problem)
    {
        OptionProblem found = Assert.Single(Options(options).Selector.AreaProblems(1000));

        Assert.Equal(path, found.Path);
        Assert.Contains(problem, found.Problem);
    }

    [Theory]
    [InlineData("""{"what":"network","network":"5","near":[0,0,0],"radius":1001}""")]
    [InlineData("""{"what":"network","network":"5","box":[[0,0,0]]}""")]
    public void AnAreaOutsideTheCataloguesBoundsIsRefused(string options)
    {
        Assert.Equal("invalid_argument",
            OptionCheck.Check(VerbSurface.Verb("survey")!, JObject.Parse(options), out _)?.Code);
    }

    private static SurveyNetworkOptions Options(string json) =>
        SurveyNetworkOptions.Bind(new OptionReader(SurveyNetworkOptions.Spec, JObject.Parse(json)));

    // survey network's member area as its handler reads it (a point or box; no player needed).
    private static NetworkMemberFilter MembersOf(string json) =>
        NetworkMemberFilter.Of(null, false, Options(json).Selector.AreaAround(null));
}
