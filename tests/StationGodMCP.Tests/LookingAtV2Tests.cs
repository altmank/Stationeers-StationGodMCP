#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>looking_at v2 (1.4.3): the view basis, snapped axes, the hit and the body box.</summary>
public sealed class LookingAtV2Tests
{
    private static readonly Vec3 Up = new Vec3(0, 1, 0);

    [Theory]
    [InlineData(0, 0, 1, 0.0, "+z", "+x")]
    [InlineData(1, 0, 0, 90.0, "+x", "-z")]
    [InlineData(0, 0, -1, 180.0, "-z", "-x")]
    [InlineData(-1, 0, 0, 270.0, "-x", "+z")]
    public void LevelLooksSnapToTheAxisAheadAndItsRight(double x, double y, double z, double yaw, string forward,
        string right)
    {
        ViewBasis basis = ViewBasis.Of(new Vec3(x, y, z), Up);
        Assert.Equal(yaw, basis.YawDegrees, 3);
        Assert.Equal(forward, basis.LevelForward.Name);
        Assert.Equal(right, basis.LevelRight.Name);
        Assert.False(basis.Ambiguous);
        Assert.Equal(0.0, basis.PitchDegrees, 3);
    }

    [Fact]
    public void RightIsUpCrossForwardAsUnityHasIt()
    {
        ViewBasis basis = ViewBasis.Of(new Vec3(0, 0, 1), Up);
        Assert.Equal(1.0, basis.Right.X, 6);
        Assert.Equal(0.0, basis.Right.Z, 6);
    }

    [Fact]
    public void ALookDownKeepsTheLevelForwardButItsLookAxisIsDown()
    {
        ViewBasis basis = ViewBasis.Of(new Vec3(0, -0.9, 0.3), new Vec3(0, 0.3, 0.9));
        Assert.Equal("+z", basis.LevelForward.Name);
        Assert.Equal("-y", basis.LookAxis.Name);
        Assert.True(basis.PitchDegrees < -60);
    }

    [Theory]
    [InlineData(1, 0, 1, true)]
    [InlineData(1, 0, 0.9, true)]
    [InlineData(1, 0, 0.5, false)]
    public void HeadingsNearADiagonalAreAmbiguous(double x, double y, double z, bool ambiguous)
    {
        Assert.Equal(ambiguous, ViewBasis.Of(new Vec3(x, y, z), Up).Ambiguous);
    }

    [Fact]
    public void ANormalNamesAFaceOnlyNearAnAxis()
    {
        Assert.Equal("+z", ViewBasis.Along(new Vec3(0.05, 0, 1), 10)?.Name);
        Assert.Null(ViewBasis.Along(new Vec3(1, 0, 1), 10));
    }

    [Fact]
    public void BoxesOfSmallCellsHaveHalfMetreCells()
    {
        // A wall console's four cells on the wall z = 668.
        Box3 box = Box3.OfSmallCells(new List<GridCell>
        {
            new GridCell(7185, 2005, 6680), new GridCell(7190, 2005, 6680), new GridCell(7185, 2010, 6680),
            new GridCell(7190, 2010, 6680)
        });
        Assert.Equal(718.25, box.Min.X, 6);
        Assert.Equal(719.25, box.Max.X, 6);
        Assert.Equal(200.25, box.Min.Y, 6);
        Assert.Equal(201.25, box.Max.Y, 6);
    }

    [Fact]
    public void BoxesThatOnlyTouchDoNotOverlap()
    {
        Box3 console = new Box3(new Vec3(718.25, 200.25, 667.75), new Vec3(719.25, 201.25, 668.25));
        Box3 sensor = new Box3(new Vec3(719.25, 200.25, 667.75), new Vec3(719.75, 200.75, 668.25));
        Box3 slightly = new Box3(new Vec3(719.2, 200.25, 667.75), new Vec3(719.75, 200.75, 668.25));
        Assert.False(console.Overlaps(sensor, 0.0));
        Assert.False(console.Overlaps(slightly, 0.1));
        Assert.True(console.Overlaps(slightly, 0.0));
    }

    [Fact]
    public void TheViewAndHitWireShapes()
    {
        LookView view = new LookView(new Vec3(719, 202.1, 671), ViewBasis.Of(new Vec3(0, 0, -1), Up), false, false);
        string json = WireCheck.New(view);
        Assert.Contains("\"axes\":{\"forward\":\"-z\",\"right\":\"-x\",\"up\":\"+y\",\"back\":\"+z\",\"left\":\"+x\"," +
                        "\"down\":\"-y\",\"look\":\"-z\"}", json);
        Assert.Contains("\"yaw_deg\":180.0", json);
        LookHitView hit = new LookHitDetailView(new Vec3(719, 201, 668.05), 3.0, new Vec3(0, 0, 1), "+z", "z=668",
            new PositionView(719, 201, 669), new PointView(719, 201, 668), "w", null, null);
        string hitJson = WireCheck.New(hit);
        Assert.Contains("\"cell_2m\":{\"x\":719.0,\"y\":201.0,\"z\":669.0}", hitJson);
        Assert.Contains("\"face_plane\":\"z=668\"", hitJson);
        Assert.Contains("\"distance_m\":3.0", hitJson);
    }

    [Fact]
    public void TheBriefHitHoldsThePointDistanceFaceAndThing()
    {
        LookHitView hit = new LookHitView(new Vec3(719, 201, 668.05), 3.0, "+z",
            new ThingView(new ThingId(7), "StructureWall", "Wall"));
        JObject json = JObject.Parse(WireCheck.New(hit));
        Assert.Equal(new[] { "point", "distance_m", "face", "thing" }, json.Properties().Select(p => p.Name));
    }

    [Fact]
    public void TheDefaultReplyLeavesOutThePartsNotAskedFor()
    {
        LookingAtView view = new LookingAtView(null,
            new LookingAtTargetView(new ThingView(new ThingId(7), "StructureWall", "Wall"), null, "structure",
                "Wall", new PositionView(1, 2, 3), 1.5, false, false, null),
            null, null, new LookHitView(new Vec3(1, 2, 3), 1.5, null, null));
        JObject json = JObject.Parse(WireCheck.New(view));
        Assert.Equal(new[] { "target", "hit" }, json.Properties().Select(p => p.Name));
    }
}
