#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>place_structure's relative at and named facings (1.4.3): the pure math and the parsing.</summary>
public sealed class RelativeAtTests
{
    private static GridStep S(string name) => RunModels.Step(name);

    [Fact]
    public void OffsetsRunAlongTheFramesAxes()
    {
        // The player looks along -z: right is -x.
        Frame3 player = Frame3.Of(S("-x"), S("+y"), S("-z"), "player");
        Vec3 point = player.Offset(new Vec3(719, 200, 671), 1.0, 0.5, 2.0);
        Assert.Equal(new Vec3(718, 200.5, 669), point);
        Assert.Equal(new Vec3(720, 201, 672), Frame3.World.Offset(new Vec3(719, 200, 671), 1, 1, 1));
    }

    [Fact]
    public void AnchorsAreTheMiddlesOfTheBoxSidesInTheFrame()
    {
        Box3 console = new Box3(new Vec3(718.25, 200.25, 667.75), new Vec3(719.25, 201.25, 668.25));
        Frame3 facingPlusZ = Frame3.Of(S("+x"), S("+y"), S("+z"), "target");
        Vec3 origin = new Vec3(719, 200.5, 668);
        Assert.Equal(new Vec3(718.75, 201.25, 668), RelativeMath.Anchor(origin, console, facingPlusZ, BodyAnchor.Top));
        Assert.Equal(new Vec3(718.25, 200.75, 668), RelativeMath.Anchor(origin, console, facingPlusZ, BodyAnchor.Left));
        Assert.Equal(new Vec3(718.75, 200.75, 668.25),
            RelativeMath.Anchor(origin, console, facingPlusZ, BodyAnchor.Front));
        Assert.Equal(origin, RelativeMath.Anchor(origin, console, facingPlusZ, BodyAnchor.Origin));
        // Turned to face -z, its left is +x.
        Frame3 facingMinusZ = Frame3.Of(S("-x"), S("+y"), S("-z"), "target");
        Assert.Equal(new Vec3(719.25, 200.75, 668),
            RelativeMath.Anchor(origin, console, facingMinusZ, BodyAnchor.Left));
    }

    [Fact]
    public void AnchorsReadByName()
    {
        Assert.Equal(BodyAnchor.Top, RelativeMath.AnchorOf("top"));
        Assert.Equal(BodyAnchor.Origin, RelativeMath.AnchorOf(null));
        Assert.Equal(BodyAnchor.Back, RelativeMath.AnchorOf(" Back "));
        Assert.Null(RelativeMath.AnchorOf("middle"));
    }

    [Fact]
    public void ALevelDirectionNearADiagonalIsAmbiguous()
    {
        Assert.Equal("+z", RelativeMath.LevelAxis(new Vec3(0.2, -3, 1), out _)?.Name);
        Assert.Null(RelativeMath.LevelAxis(new Vec3(1, 0, 1.05), out string? why));
        Assert.Contains("diagonal", why);
        Assert.Null(RelativeMath.LevelAxis(new Vec3(0, -1, 0), out _));
    }

    [Fact]
    public void AFaceSeenFromTheFrontHasTheViewersRightAndUp()
    {
        // A wall facing +z seen by a player looking -z: right is -x, up +y.
        (GridStep right, GridStep up) = RelativeMath.FaceAxes(S("+z"), S("-z"), S("-x"));
        Assert.Equal("-x", right.Name);
        Assert.Equal("+y", up.Name);
        // A floor seen by a player looking +z: up is +z (away), right +x.
        (GridStep floorRight, GridStep floorUp) = RelativeMath.FaceAxes(S("+y"), S("+z"), S("+x"));
        Assert.Equal("+x", floorRight.Name);
        Assert.Equal("+z", floorUp.Name);
        // A ceiling looked up at by a player facing +z: right is still +x.
        (GridStep ceilingRight, GridStep ceilingUp) = RelativeMath.FaceAxes(S("-y"), S("+z"), S("+x"));
        Assert.Equal("+x", ceilingRight.Name);
        Assert.Equal("+z", ceilingUp.Name);
    }

    [Fact]
    public void AtIsAPointOrARelativeSpec()
    {
        Assert.IsType<AtArg.Absolute>(BuildArgs.AtOf(new JArray(719, 200.5, 668), "at"));
        Assert.IsType<AtArg.Absolute>(BuildArgs.AtOf(JObject.Parse("{\"x\":1,\"y\":2,\"z\":3}"), "at"));
        Assert.IsType<AtArg.Relative>(BuildArgs.AtOf(JObject.Parse("{\"crosshair\":true}"), "at"));
        Assert.IsType<AtArg.Relative>(BuildArgs.AtOf(
            JObject.Parse("{\"relative_to\":\"player\",\"forward_m\":2}"), "at"));
        Assert.IsType<AtArg.Relative>(BuildArgs.AtOf(JObject.Parse("{\"on_face_i_look_at\":true}"), "at"));
    }

    [Fact]
    public void NamedFacingsAreKeptForTheWorldToResolve()
    {
        BuildForm<PlaceArguments> form = BuildArgs.ParsePlace(new Args(JObject.Parse(
            "{\"prefab\":\"StructureConsole3x3\",\"at\":{\"crosshair\":true},\"facing\":\"toward_player\"," +
            "\"above_floor_m\":1.0}")));
        PlacementArgs placement = Assert.IsType<BuildForm<PlaceArguments>.Run>(form).Arguments.Placements[0];
        Assert.Equal("toward_player", placement.NamedFacing?.Word);
        Assert.Equal(1.0, placement.AboveFloorM);
        Assert.IsType<RotationSpec.None>(placement.Rotation);
    }

    [Fact]
    public void OrientExcludesAGivenTurn()
    {
        Assert.Throws<ApiException>(() => BuildArgs.ParsePlace(new Args(JObject.Parse(
            "{\"prefab\":\"StructureConsole3x3\",\"at\":[1,2,3],\"facing\":\"+z\",\"orient\":{\"mount\":\"wall\"}}"))));
    }
}
