#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>build structure's relative at and named facings (1.4.3): the pure math and the parsing.</summary>
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
        Assert.IsType<AtForm.Absolute>(PlacementReader.AtOf(new JArray(719, 200.5, 668), "at"));
        Assert.IsType<AtForm.Absolute>(PlacementReader.AtOf(JObject.Parse("{\"x\":1,\"y\":2,\"z\":3}"), "at"));
        Assert.IsType<AtForm.Relative>(PlacementReader.AtOf(new JValue("looking_at"), "at"));
        Assert.IsType<AtForm.Relative>(PlacementReader.AtOf(JObject.Parse("{\"looking_at\":true,\"up\":1}"), "at"));
        Assert.IsType<AtForm.Relative>(PlacementReader.AtOf(
            JObject.Parse("{\"relative_to\":\"player\",\"forward\":2,\"anchor\":\"top\"}"), "at"));
        Assert.IsType<AtForm.Relative>(PlacementReader.AtOf(
            JObject.Parse("{\"on_face\":true,\"along_right\":1,\"along_up\":0.5}"), "at"));
    }

    // R-D-4: the help's words are the only ones; an old or unknown key in a relative at is refused with the nearest
    // names, never dropped (an offset read under the wrong name would build at the anchor).
    [Theory]
    [InlineData("{\"relative_to\":\"4820585\",\"right_m\":2}", "at.right_m")]
    [InlineData("{\"relative_to\":\"4820585\",\"from\":\"top\"}", "at.from")]
    [InlineData("{\"looking_at\":true,\"anchor\":\"top\"}", "at.anchor")]
    [InlineData("{\"on_face\":true,\"along_right_m\":1}", "at.along_right_m")]
    public void AnUnknownKeyInARelativeAtIsRefused(string at, string named)
    {
        ApiException error = Assert.Throws<ApiException>(() => PlacementReader.AtOf(JObject.Parse(at), "at"));
        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
        Assert.StartsWith(named + " is not a key here", error.Message);
    }

    [Fact]
    public void ARelativeAtTakesOneFormAndItsFlagsAreTrue()
    {
        Assert.Throws<ApiException>(() => PlacementReader.AtOf(
            JObject.Parse("{\"looking_at\":true,\"relative_to\":\"player\"}"), "at"));
        Assert.Throws<ApiException>(() => PlacementReader.AtOf(JObject.Parse("{\"on_face\":false}"), "at"));
        Assert.Throws<ApiException>(() => PlacementReader.AtOf(new JValue("crosshair"), "at"));
    }

    [Fact]
    public void NamedFacingsAreKeptForTheWorldToResolve()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        StructureBuildOrder order = StructureBuildOrder.Of(BuildStructureOptions.Bind(new OptionReader(
            BuildStructureOptions.Spec, JObject.Parse("{\"prefab\":\"StructureConsole3x3\"," +
                "\"at\":\"looking_at\",\"facing\":\"toward_player\",\"above_floor\":1.0}"))), problems,
            out VerbError? missing)!;

        Assert.Empty(problems);
        Assert.Null(missing);
        PlacementRequest placement = Assert.Single(order.Placements);
        Assert.Equal("toward_player", placement.NamedFacing?.Word);
        Assert.Equal(1.0, placement.AboveFloorM);
        Assert.IsType<RotationSpec.None>(placement.Rotation);
    }

    [Fact]
    public void OrientExcludesAGivenTurn()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Null(StructureBuildOrder.Of(BuildStructureOptions.Bind(new OptionReader(BuildStructureOptions.Spec,
            JObject.Parse("{\"prefab\":\"StructureConsole3x3\",\"at\":[1,2,3],\"facing\":\"+z\"," +
                "\"orient\":{\"mount\":\"wall\"}}"))), problems, out _));
        Assert.Equal("orient", Assert.Single(problems).Path);
    }
}
