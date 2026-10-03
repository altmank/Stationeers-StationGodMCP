#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// connections' area filter (live: a 38 KB page of 200 of a cable network's 1,257 members): min and max keep the
/// members inside a box, near with radius_m those within a radius, and the arguments that do not fit are refused.
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
    public void NoAreaArgumentsKeepEverything()
    {
        NetworkMemberFilter filter = NetworkMemberFilter.Parse(new Args(new JObject()));

        Assert.True(filter.KeepsPosition(new Vec3(99999, -500, 3)));
    }

    [Fact]
    public void TheArgumentsReachTheFilter()
    {
        NetworkMemberFilter box = NetworkMemberFilter.Parse(Args("""{"min":[0,0,0],"max":{"x":2,"y":2,"z":2}}"""));
        NetworkMemberFilter near = NetworkMemberFilter.Parse(Args("""{"near":[0,0,0],"radius_m":1}"""));

        Assert.True(box.KeepsPosition(new Vec3(1, 1, 1)));
        Assert.False(box.KeepsPosition(new Vec3(3, 1, 1)));
        Assert.True(near.KeepsPosition(new Vec3(0, 1, 0)));
        Assert.False(near.KeepsPosition(new Vec3(1, 1, 0)));
    }

    [Theory]
    [InlineData("""{"min":[0,0,0]}""", "Give both min and max")]
    [InlineData("""{"near":[0,0,0]}""", "near needs radius_m")]
    [InlineData("""{"radius_m":3}""", "radius_m needs near")]
    [InlineData("""{"near":[0,0,0],"radius_m":0}""", "radius_m")]
    [InlineData("""{"near":[0,0,0],"radius_m":1001}""", "radius_m")]
    [InlineData("""{"min":[0,0,0],"max":[1,1,1],"near":[0,0,0],"radius_m":1}""", "not both")]
    [InlineData("""{"near":"player","radius_m":1}""", "near must be a position")]
    public void AnAreaThatDoesNotFitIsRefused(string arguments, string message)
    {
        ApiException refused = Assert.Throws<ApiException>(() => AreaArgs.Parse(Args(arguments)));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains(message, refused.Message);
    }

    private static Args Args(string json) => new Args(JObject.Parse(json));
}
