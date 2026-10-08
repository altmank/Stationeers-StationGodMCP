#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// clips_surface and controls_blocked's mesh check (LU 2026-10-08: Turbo Volume Pump 4755409 on bt built at y 212
/// facing -y, up +z, sunk into the floor of its frame, its controls side against the furnace beside it; lint_layout
/// reported neither). The pump's mesh box is the prefab's own (describe_prefab: -0.26..0.26, -0.22..0.31, -0.25..0.25);
/// the floor is a Composite Wall facing +y on the top of the iron frame below (render 0..0.13 along its forward).
/// </summary>
public sealed class SurfaceClipTests
{
    private static readonly Box3 PumpMesh = new Box3(new Vec3(-0.26, -0.22, -0.25), new Vec3(0.26, 0.31, 0.25));

    private static readonly Box3 FurnaceMesh = new Box3(new Vec3(-0.79, -0.6, -0.75), new Vec3(0.78, 0.77, 0.81));

    private static readonly Vec3 PumpAt = new Vec3(591, 212, 629);

    private static readonly Solid Floor = new Solid(SolidKind.Plate, "Composite Wall (StructureCompositeWall 44)", 44,
        new Box3(new Vec3(590, 212, 628), new Vec3(592, 212.13, 630)), FacePlane.Of(1, 2120));

    private static readonly Solid FrameBelow = new Solid(SolidKind.Frame, "Iron Frame (StructureFrameIron 33)", 33,
        new Box3(new Vec3(590, 210, 628), new Vec3(592, 212, 630)));

    private static readonly Solid Furnace = new Solid(SolidKind.Body, "Hub Furnace 1 (StructureAdvancedFurnace 42)", 42,
        TurnedBox.Of(FurnaceMesh, CubeRotation.Identity, new Vec3(591, 212.5, 630.5)));

    private static CubeRotation Turn(string facing, string up)
    {
        GridStep.TryParse(facing, out GridStep forward);
        GridStep.TryParse(up, out GridStep top);
        return CubeRotation.FromFacing(forward, top) ?? throw new ArgumentException($"{facing} {up}");
    }

    private static GridCell Cell(Vec3 at) =>
        new GridCell((int)Math.Round(at.X * 10), (int)Math.Round(at.Y * 10), (int)Math.Round(at.Z * 10));

    // The pump's mount as PlacementLayout reads it: the plane behind its small cell along its top (grid placement).
    private static MountRect? MountOf(CubeRotation turn, Vec3 at, Box3 render) =>
        MountRect.Of(Box3.OfSmallCells(new List<GridCell> { Cell(at) }), turn.Up, render);

    [Fact]
    public void APumpLyingFacingDownOnAFloorClipsTheFloor()
    {
        CubeRotation turn = Turn("-y", "+z");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);

        (Solid Solid, double Depth)? clip =
            SurfaceClip.First(render, MountOf(turn, PumpAt, render), new List<Solid> { FrameBelow, Floor });

        Assert.NotNull(clip);
        Assert.Same(Floor, clip!.Value.Solid);
        Assert.Equal(0.13, clip.Value.Depth, 3);
        Assert.Equal("the floor (Composite Wall (StructureCompositeWall 44)) on y=212, 0.13 m deep",
            SurfaceClip.Describe(clip.Value.Solid, clip.Value.Depth, render));
    }

    [Fact]
    public void WithoutAPlateTheFrameBodyIsTheSurfaceItClips()
    {
        CubeRotation turn = Turn("-y", "+z");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);

        (Solid Solid, double Depth)? clip =
            SurfaceClip.First(render, MountOf(turn, PumpAt, render), new List<Solid> { FrameBelow });

        Assert.Same(FrameBelow, clip!.Value.Solid);
        Assert.Equal(0.25, clip.Value.Depth, 3);
    }

    [Fact]
    public void AnUprightPumpOnTheFloorRestsOnItAndPasses()
    {
        CubeRotation turn = Turn("+z", "+y");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);
        MountRect? mount = MountOf(turn, PumpAt, render);

        Assert.NotNull(mount);
        Assert.Equal("y=212", mount!.Plane.ToString());
        Assert.Null(SurfaceClip.First(render, mount, new List<Solid> { FrameBelow, Floor }));
    }

    [Fact]
    public void AnUprightPumpOnAWallPlaneClipsTheWallItDoesNotStandOn()
    {
        // Standing on the floor at x 590, the plane of a wall facing +x: half its body is in the wall.
        Vec3 at = new Vec3(590, 212, 629);
        Solid wall = new Solid(SolidKind.Plate, "Iron Wall (StructureWallIron 55)", 55,
            new Box3(new Vec3(590, 212, 628), new Vec3(590.13, 214, 630)), FacePlane.Of(0, 5900));
        CubeRotation turn = Turn("+z", "+y");
        Box3 render = TurnedBox.Of(PumpMesh, turn, at);

        (Solid Solid, double Depth)? clip =
            SurfaceClip.First(render, MountOf(turn, at, render), new List<Solid> { Floor, wall });

        Assert.Same(wall, clip!.Value.Solid);
        Assert.StartsWith("a wall (Iron Wall (StructureWallIron 55)) on x=590", SurfaceClip.Describe(wall, 0.13, render));
    }

    [Fact]
    public void ABodyNeverCountsAsASurface()
    {
        CubeRotation turn = Turn("-y", "+z");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);
        Solid overlapping = new Solid(SolidKind.Body, "Pipe", 7, render);

        Assert.Null(SurfaceClip.First(render, null, new List<Solid> { overlapping }));
    }

    [Fact]
    public void TheLyingPumpsControlsFaceTheFurnaceAndSinkIntoTheFloor()
    {
        CubeRotation turn = Turn("-y", "+z");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);
        GridStep controls = turn.Turn(GridStep.All[2]);

        Assert.Equal("+z", controls.Name);
        (Solid Solid, double Distance)? furnaceOnly = ControlsReach.Blocker(render, controls, new List<Solid> { Furnace });
        Assert.Same(Furnace, furnaceOnly!.Value.Solid);
        Assert.Equal(0.44, furnaceOnly.Value.Distance, 2);

        // Half of its controls side is below the floor: the floor plate and the frame cover it, right at the side.
        (Solid Solid, double Distance)? all =
            ControlsReach.Blocker(render, controls, new List<Solid> { Furnace, Floor, FrameBelow });
        Assert.Equal(0.0, all!.Value.Distance, 6);
        Assert.Same(FrameBelow, all.Value.Solid);
    }

    [Fact]
    public void AnUprightPumpsControlsFaceOpenRoom()
    {
        CubeRotation turn = Turn("+z", "+y");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);

        Assert.Null(ControlsReach.Blocker(render, turn.Turn(GridStep.All[2]),
            new List<Solid> { Furnace, Floor, FrameBelow }));
    }

    [Fact]
    public void ControlsFacingDownOntoTheFloorAreBlocked()
    {
        Vec3 at = new Vec3(591, 212.5, 629);
        CubeRotation turn = Turn("+z", "-y");
        Box3 render = TurnedBox.Of(PumpMesh, turn, at);
        GridStep controls = turn.Turn(GridStep.All[2]);

        (Solid Solid, double Distance)? blocked =
            ControlsReach.Blocker(render, controls, new List<Solid> { Floor, FrameBelow });

        Assert.Equal("-y", controls.Name);
        Assert.Same(Floor, blocked!.Value.Solid);
        Assert.Equal(0.06, blocked.Value.Distance, 2);
        Assert.Equal("the floor (Composite Wall (StructureCompositeWall 44)) on y=212 0.06 m in front",
            ControlsReach.Describe(blocked.Value.Solid, blocked.Value.Distance, render));
    }

    [Fact]
    public void AFloorUnderATallDevicesSidePanelDoesNotBlockIt()
    {
        // The furnace stands on the floor; its controls face +z, over the next floor section, which covers only the
        // bottom 0.1 m of that side.
        Solid nextFloor = new Solid(SolidKind.Plate, "Composite Wall (StructureCompositeWall 45)", 45,
            new Box3(new Vec3(590, 212, 630), new Vec3(592, 212.13, 632)), FacePlane.Of(1, 2120));
        Solid nextFrame = new Solid(SolidKind.Frame, "Iron Frame (StructureFrameIron 34)", 34,
            new Box3(new Vec3(590, 210, 630), new Vec3(592, 212, 632)));

        Assert.Null(ControlsReach.Blocker(Furnace.Box, GridStep.All[4], new List<Solid> { nextFloor, nextFrame }));
    }

    [Fact]
    public void AThinMountedDevicesSideControlsAreNotBlockedByItsOwnWall()
    {
        // A 0.2 m deep box mounted facing +x on a wall plate at x 590, its mesh starting inside the plate, its controls
        // on its +z side: the plate it hangs on covers 40 % of that side, but it is the surface it rests on.
        Solid wall = new Solid(SolidKind.Plate, "Iron Wall (StructureWallIron 55)", 55,
            new Box3(new Vec3(590, 212, 628), new Vec3(590.13, 214, 630)), FacePlane.Of(0, 5900));
        Box3 render = new Box3(new Vec3(590.05, 212.75, 628.8), new Vec3(590.25, 213.25, 629.2));
        MountRect? mount = MountRect.Of(new Box3(new Vec3(589.75, 212.75, 628.75), new Vec3(590.25, 213.25, 629.25)),
            GridStep.All[0], render);

        Assert.NotNull(mount);
        Assert.NotNull(ControlsReach.Blocker(render, GridStep.All[4], new List<Solid> { wall }));
        Assert.Null(ControlsReach.Blocker(render, GridStep.All[4], new List<Solid> { wall }, mount));
        Assert.NotNull(ControlsReach.Blocker(render, GridStep.All[1], new List<Solid> { wall }, mount));
    }

    [Fact]
    public void ASolidPastTheClearanceDoesNotBlock()
    {
        CubeRotation turn = Turn("-y", "+z");
        Box3 render = TurnedBox.Of(PumpMesh, turn, PumpAt);
        Solid far = new Solid(SolidKind.Body, "Locker", 9,
            new Box3(new Vec3(590.5, 211.5, render.Max.Z + ControlsReach.ClearanceM + 0.01),
                new Vec3(591.5, 212.5, render.Max.Z + 1.0)));

        Assert.Null(ControlsReach.Blocker(render, GridStep.All[4], new List<Solid> { far }));
    }

    [Fact]
    public void TurnedBoxMatchesTheGamesTurnOfThePump()
    {
        Box3 box = TurnedBox.Of(PumpMesh, Turn("-y", "+z"), PumpAt);

        Assert.Equal(590.74, box.Min.X, 3);
        Assert.Equal(211.75, box.Min.Y, 3);
        Assert.Equal(212.25, box.Max.Y, 3);
        Assert.Equal(628.78, box.Min.Z, 3);
        Assert.Equal(629.31, box.Max.Z, 3);
    }
}

/// <summary>
/// blocks_window (LU 2026-10-08: pipe analysers mounted at z 626 in hub room 32, e.g. 4839444 at (574, 213, 626), cover a
/// glass wall; no device's face, body or mesh may intersect a window plane).
/// </summary>
public sealed class WindowCrossTests
{
    // A pipe analyser's mesh on the plane z 626, mounted there: its box reaches either side of the plane.
    private static readonly Box3 Analyser = new Box3(new Vec3(573.8, 212.8, 625.85), new Vec3(574.2, 213.2, 626.15));

    private static Solid Plate(bool window, long id) =>
        new Solid(SolidKind.Plate, window ? $"Window (StructureWindowIron {id})" : $"Iron Wall (StructureWallIron {id})",
            id, new Box3(new Vec3(573, 212, 626), new Vec3(575, 214, 626.13)), FacePlane.Of(2, 6260), window);

    [Fact]
    public void AnAnalyserOnAWindowPlaneCoversTheWindow()
    {
        Solid window = Plate(true, 61);

        Assert.Same(window, WindowCross.First(Analyser, new List<Solid> { window }));
        Assert.Equal("the window (Window (StructureWindowIron 61)) on z=626", window.Describe(Analyser));
    }

    [Fact]
    public void AnAnalyserOnASolidWallPasses()
    {
        Assert.Null(WindowCross.First(Analyser, new List<Solid> { Plate(false, 62) }));
    }

    [Fact]
    public void ABodyBesideTheWindowsRectangleOrClearOfItsPlanePasses()
    {
        Solid window = Plate(true, 61);
        Box3 beside = new Box3(new Vec3(575.05, 212.8, 625.85), new Vec3(575.45, 213.2, 626.15));
        Box3 clear = new Box3(new Vec3(573.8, 212.8, 626.25), new Vec3(574.2, 213.2, 626.75));

        Assert.Null(WindowCross.First(beside, new List<Solid> { window }));
        Assert.Null(WindowCross.First(clear, new List<Solid> { window }));
    }
}

/// <summary>
/// chute_outside_frame's port stub (LU 2026-10-08: chutes route inside frames; a device's own stub may leave them) and
/// the route planners' style (LU 2026-10-08: the caller passes a routing style; no kind keeps to frames by default).
/// </summary>
public sealed class ChuteFrameTests
{
    private static GridCell M(double x, double y, double z) =>
        new GridCell((int)Math.Round(x * 10), (int)Math.Round(y * 10), (int)Math.Round(z * 10));

    [Fact]
    public void ThePieceInAPortsJoiningCellIsItsStub()
    {
        Assert.True(PortStubs.IsStub(new List<GridCell> { M(591, 212.5, 629.5) },
            new List<GridCell> { M(591, 212.5, 629.5) }));
    }

    [Fact]
    public void ThePieceTurningOutOfTheFrameNextToTheStubIsPartOfIt()
    {
        Assert.True(PortStubs.IsStub(new List<GridCell> { M(591, 212, 629.5) },
            new List<GridCell> { M(591, 212.5, 629.5) }));
    }

    [Fact]
    public void APieceTwoCellsFromAnyPortIsNoStub()
    {
        Assert.False(PortStubs.IsStub(new List<GridCell> { M(591, 211.5, 629.5) },
            new List<GridCell> { M(591, 212.5, 629.5) }));
        Assert.False(PortStubs.IsStub(new List<GridCell> { M(591, 212, 629.5) }, new List<GridCell>()));
    }

    [Theory]
    [InlineData(null, "supported", "None", false, true)]
    [InlineData("frame_edges", "frame_edges", "FrameEdges", false, true)]
    [InlineData("walls", "walls", "Walls", false, true)]
    [InlineData("HIDDEN", "hidden", "Hidden", false, true)]
    [InlineData("inside_frames", "inside_frames", "None", true, true)]
    [InlineData(" free ", "free", "None", false, false)]
    public void EachRouteStyleMapsToOneSetOfRules(string? given, string name, string prefer, bool inside, bool airPenalty)
    {
        RouteStyle style = RouteStyle.Parse(given)!;

        Assert.Equal(name, style.Name);
        Assert.Equal(prefer, style.Prefer.ToString());
        Assert.Equal(inside, style.InsideFrames);
        Assert.Equal(airPenalty, style.FramesFirst);
    }

    [Fact]
    public void AnUnknownRouteStyleIsNone()
    {
        Assert.Null(RouteStyle.Parse("frames_first"));
        Assert.Equal(6, RouteStyle.Names.Length);
        Assert.All(RouteStyle.Names, name => Assert.NotNull(RouteStyle.Parse(name)));
    }
}

/// <summary>
/// The route planners honour the lint rules in effect (LU 2026-10-08: the lint config is what the tools build by):
/// chute_outside_frame refuses or avoids cells outside a frame's body whatever the style; turned off it does nothing.
/// </summary>
public sealed class RouteLintRuleTests
{
    private static SmallCellFacts Cell(CellVisibility visibility, CellSupport support) =>
        new SmallCellFacts(null, 0, null, false, new LargeCellFacts(true, true, 0), 1, 0, 1, new List<long>(), support,
            visibility);

    private static readonly SmallCellFacts OnFloor = Cell(CellVisibility.FrameSurface, CellSupport.Frame);

    private static readonly SmallCellFacts Inside = Cell(CellVisibility.Inside, CellSupport.Frame);

    private static RouteRuleSet Style(string style)
    {
        RouteStyle parsed = RouteStyle.Parse(style)!;
        return new RouteRuleSet(parsed.Prefer, parsed.InsideFrames, false, false, false, new HashSet<long>(),
            new HashSet<long>(), false, parsed.FramesFirst);
    }

    private static readonly GridCell Port = new GridCell(5910, 2125, 6295);

    [Fact]
    public void TheShippedChuteRuleRefusesCellsOutsideFramesExceptBesideTheEnds()
    {
        RouteLintRule? rule = RouteLintRule.From(LintTestKit.Defaults(), RouteLintRule.ChuteOutsideFrame,
            new[] { Port });

        Assert.NotNull(rule);
        Assert.True(rule!.Refuses);
        RouteRuleSet rules = Style("free").WithInsideRule(rule);
        Assert.False(rules.Cost(OnFloor, new GridCell(5910, 2000, 6295)).Passable);
        Assert.True(rules.Cost(Inside, new GridCell(5910, 2000, 6295)).Passable);
        Assert.True(rules.Cost(OnFloor, new GridCell(5910, 2120, 6295)).Passable);
    }

    [Fact]
    public void AWarningLevelRuleOnlyAddsCost()
    {
        RouteLintRule rule = new RouteLintRule(RouteLintRule.ChuteOutsideFrame, false, new HashSet<GridCell>());
        RouteRuleSet rules = Style("supported").WithInsideRule(rule);

        CellCost outside = rules.Cost(OnFloor, new GridCell(0, 0, 0));
        Assert.True(outside.Passable);
        Assert.Equal(1.0 + RouteRuleSet.LintPenalty, outside.Cost, 6);
        Assert.Equal(1.0, rules.Cost(Inside, new GridCell(0, 0, 0)).Cost, 6);
    }

    [Fact]
    public void ATurnedOffRuleIsNotHonoured()
    {
        LintRuleSet set = LintTestKit.Defaults(save: LintTestKit.File1("{\"id\": \"chute_outside_frame\", \"enabled\": false}"));

        Assert.Null(RouteLintRule.From(set, RouteLintRule.ChuteOutsideFrame, new[] { Port }));
    }

    [Theory]
    [InlineData(null, "Refuse")]
    [InlineData("{\"id\": \"run_crosses_window\", \"level\": \"warning\"}", "Penalty")]
    [InlineData("{\"id\": \"run_crosses_window\", \"enabled\": false}", "None")]
    public void RoutesTreatAWindowsPlaneAsTheWindowRuleSays(string? save, string expected)
    {
        LintRuleSet set = LintTestKit.Defaults(save: save != null ? LintTestKit.File1(save) : null);
        WindowRule rule = RouteLintRule.WindowsUnder(set);
        GridCell seam = new GridCell(7040, 2030, 6780);
        OpeningGuard guard = new OpeningGuard(cell => cell.Equals(seam) ? OpeningZone.OnWindow(500) : OpeningZone.Clear,
            new GridCell[0], false, rule);

        CellCost cost = guard.Guard(_ => CellCost.Of(1.0))(seam);

        Assert.Equal(expected, rule.ToString());
        Assert.Equal(rule != WindowRule.Refuse, cost.Passable);
        if (cost.Passable)
        {
            Assert.Equal(rule == WindowRule.Penalty ? 1.0 + OpeningGuard.WindowPenalty : 1.0, cost.Cost, 6);
        }
    }

    [Fact]
    public void ALoweredRuleAvoidsInsteadOfRefusing()
    {
        LintRuleSet set = LintTestKit.Defaults(save: LintTestKit.File1("{\"id\": \"chute_outside_frame\", \"level\": \"warning\"}"));

        Assert.False(RouteLintRule.From(set, RouteLintRule.ChuteOutsideFrame, new[] { Port })!.Refuses);
    }
}
