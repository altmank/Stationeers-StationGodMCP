#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>place_structure's layout preview (1.4.3): mount plane, sections, flow, uprightness, visual up.</summary>
public sealed class PlacementPreviewTests
{
    private static GridStep S(string name) => RunModels.Step(name);

    // A wall console as the game registers it on the wall z = 668: four small cells, x 718.5 and 719,
    // y 200.5 and 201.
    private static readonly List<GridCell> Console = new List<GridCell>
    {
        new GridCell(7185, 2005, 6680), new GridCell(7190, 2005, 6680), new GridCell(7185, 2010, 6680),
        new GridCell(7190, 2010, 6680)
    };

    [Fact]
    public void TheLiveConsoleRestsOnOneWallSection()
    {
        MountRect rect = MountRect.Of(Box3.OfSmallCells(Console), S("+z"))!;
        Assert.Equal("z=668", rect.Plane.ToString());
        Assert.Equal(718.25, rect.MinU, 6);
        Assert.Equal(719.25, rect.MaxU, 6);
        List<GridCell> faces = rect.Faces();
        Assert.Single(faces);
        Assert.Equal(new GridCell(7190, 2010, 6680), faces[0]);
        Assert.False(rect.CrossesSeam);
    }

    [Fact]
    public void TheSameConsoleHalfAMetreLeftCrossesTheSeamAtX718()
    {
        List<GridCell> shifted = Console.ConvertAll(cell => new GridCell(cell.X - 5, cell.Y, cell.Z));
        MountRect rect = MountRect.Of(Box3.OfSmallCells(shifted), S("+z"))!;
        Assert.True(rect.CrossesSeam);
        Assert.Equal(2, rect.Faces().Count);
    }

    [Fact]
    public void AFootprintOffEveryPlaneRestsOnNothing()
    {
        Box3 floating = new Box3(new Vec3(718.25, 200.25, 666.6), new Vec3(719.25, 201.25, 667.1));
        Assert.Null(MountRect.Of(floating, S("+z")));
    }

    [Fact]
    public void AStandingDeviceRestsOnTheFloorBelowIt()
    {
        Box3 box = Box3.OfSmallCells(new List<GridCell> { new GridCell(7190, 2000, 6710), new GridCell(7190, 2005, 6710) });
        MountRect rect = MountRect.Of(box, S("+y"))!;
        Assert.Equal("y=200", rect.Plane.ToString());
        Assert.Equal(new List<GridCell> { new GridCell(7190, 2000, 6710) }, rect.Faces());
        Box3 onSeam = Box3.OfSmallCells(new List<GridCell> { new GridCell(7190, 2000, 6700) });
        Assert.True(MountRect.Of(onSeam, S("+y"))!.CrossesSeam);
    }

    [Theory]
    [InlineData(718.25, 719.25, new[] { 7190 })]
    [InlineData(717.75, 718.25, new[] { 7170, 7190 })]
    [InlineData(716.0, 718.0, new[] { 7170 })]
    [InlineData(716.0, 718.01, new[] { 7170 })]
    [InlineData(716.0, 718.2, new[] { 7170, 7190 })]
    [InlineData(717.98, 718.03, new[] { 7190 })]
    public void AnIntervalCoversTheFacesItOverlaps(double min, double max, int[] centres)
    {
        Assert.Equal(new List<int>(centres), MountRect.Centres(min, max));
    }

    [Theory]
    [InlineData("Input", "in")]
    [InlineData("Input2", "in")]
    [InlineData("Output", "out")]
    [InlineData("Output2", "out")]
    [InlineData("Waste", "out")]
    [InlineData("None", null)]
    public void PortsFlowByTheirRole(string role, string? flow)
    {
        Assert.Equal(flow, PortFlow.Of(role));
    }

    [Fact]
    public void AWallDeviceFacingOutWithItsTopUpIsUpright()
    {
        Assert.Null(Uprightness.Problem(CubeRotation.FromFacing(S("+z"), S("+y"))!, S("+y")));
        Assert.NotNull(Uprightness.Problem(CubeRotation.FromFacing(S("+z"), S("+x"))!, S("+y")));
        Assert.Null(Uprightness.Problem(CubeRotation.FromFacing(S("+y"), S("+z"))!, S("+y")));
    }

    [Fact]
    public void AnUncheckedPrefabIsUprightOnlyWhenTheCursorCannotTipIt()
    {
        Assert.True(VisualUp.Of("StructureLiquidVolumePump", tips: false).Verified);
        Assert.StartsWith("CODE", VisualUp.Of("StructureLiquidVolumePump", tips: false).Source);
        Assert.False(VisualUp.Of("StructureLiquidVolumePump", tips: true).Verified);
        Assert.StartsWith("ASSUMED", VisualUp.Of("StructureLiquidVolumePump", tips: true).Source);
    }

    [Fact]
    public void CompactFiltrationReadsUpsideDownAtPlusY()
    {
        foreach (string prefab in new[] { "StructureCompactFiltration", "StructureCompactFiltrationMirror" })
        {
            VisualUp up = VisualUp.Of(prefab, tips: true);
            Assert.Equal("-y", up.LocalUp.Name);
            Assert.True(up.LyingAllowed);
            Assert.False(up.Verified);
        }
    }

    [Fact]
    public void InLineTanksMayLieDown()
    {
        VisualUp tank = VisualUp.Of("StructureInsulatedInLineTankGas1x3", tips: true);
        Assert.True(tank.LyingAllowed);
        Assert.True(tank.Verified);
        Assert.Equal("+y", tank.LocalUp.Name);
        Assert.False(VisualUp.Of("StructureConsole3x3", tips: true).LyingAllowed);
        Assert.False(VisualUp.Of("StructureTankSmallInLine", tips: true).Verified);
        // The live 1x3 standing up: +x facing, up +y.
        Assert.Null(Uprightness.Problem(CubeRotation.FromFacing(S("+x"), S("+y"))!, tank.LocalUp));
        // A device lying on its side against a wall: facing +x, top +z.
        Assert.NotNull(Uprightness.Problem(CubeRotation.FromFacing(S("+x"), S("+z"))!, S("+y")));
    }

    [Fact]
    public void TheLayoutWireShape()
    {
        MountRect rect = MountRect.Of(Box3.OfSmallCells(Console), S("+z"))!;
        PlacementLayoutView view = new PlacementLayoutView(
            new FootprintView(new CellListView(Console, 64), new List<PositionView>(),
                new BodyView(new Vec3(719, 200.5, 668), Box3.OfSmallCells(Console), Box3.OfSmallCells(Console), 4),
                new MountView(rect)),
            new SectionsView(new List<SectionWallView>
            {
                new SectionWallView(PointView.OfCell(rect.Faces()[0]),
                    new ThingView(new ThingId(158861), "StructureCompositeWall", "Composite Wall"), "wall")
            }, false),
            new List<ConflictView>
            {
                new ConflictView(new LayoutConflict(ConflictCodes.VisualOverlap, ConflictLevel.Warning, "m", 161368))
            },
            null);
        string json = WireCheck.New(view);
        Assert.Contains("\"mount\":{\"plane\":\"z=668\",\"outward\":\"+z\",\"axes\":[\"x\",\"y\"]," +
                        "\"min\":[718.25,200.25],\"max\":[719.25,201.25]}", json);
        Assert.Contains("\"sections\":{\"walls\":[{\"face\":{\"x\":719.0,\"y\":201.0,\"z\":668.0}," +
                        "\"reference_id\":\"158861\"", json);
        Assert.Contains("\"conflicts\":[{\"code\":\"visual_overlap\",\"level\":\"warning\",\"message\":\"m\"," +
                        "\"reference_id\":\"161368\"}]", json);
        Assert.DoesNotContain("port_checks", json);
    }
}

/// <summary>
/// visual_overlap's clash rule (1.4.4): the mesh boxes decide, past 0.1 m. The numbers are live (Vulcan, 2026-09-29):
/// Console3x3 "Coolant Monitor" 196328 on the wall z = 668, its gas sensor 161368 beside it.
/// </summary>
public sealed class VisualClashTests
{
    // 196328's render box and its 1 x 1 m footprint; 161368 (a gas sensor at 719.5, 200.5) is flush beside it.
    internal static readonly Box3 ConsoleMesh = new Box3(new Vec3(717.76, 200.25, 667.95), new Vec3(719.23, 201.71, 668.2));
    internal static readonly Box3 ConsoleCells = new Box3(new Vec3(718.25, 200.25, 667.75), new Vec3(719.25, 201.25, 668.25));
    private static readonly Box3 FlushSensorMesh = new Box3(new Vec3(719.35, 200.25, 667.96), new Vec3(719.65, 200.65, 668.17));

    // A gas sensor's mesh (describe_prefab: x +-0.15, y -0.25..0.15, z -0.04..0.17) at a point on the wall.
    internal static Box3 SensorAt(double x, double y) =>
        new Box3(new Vec3(x - 0.15, y - 0.25, 667.96), new Vec3(x + 0.15, y + 0.15, 668.17));

    [Fact]
    public void AFlushNeighbourIsNotAClash()
    {
        Assert.False(VisualClash.Clashes(ConsoleMesh, FlushSensorMesh));
        Assert.True(VisualClash.Depth(ConsoleMesh, FlushSensorMesh) <= 0);
    }

    [Fact]
    public void ASmallDeviceUnderAConsolesOverhangClashes()
    {
        // find_spot offered (719, 201.5) and (718.5, 201.5): outside the console's cells, inside its mesh.
        Assert.True(VisualClash.Clashes(ConsoleMesh, SensorAt(719, 201.5)));
        Assert.True(VisualClash.Clashes(ConsoleMesh, SensorAt(718.5, 201.5)));
        Assert.True(VisualClash.Clashes(SensorAt(719, 201.5), ConsoleMesh));
        Assert.True(Box3.OfSmallCells(new List<GridCell> { new GridCell(7190, 2015, 6680) }).Penetration(ConsoleCells) <= 0);
    }

    [Fact]
    public void MeshesOverlappingByATenthOfAMetreOrLessDoNotClash()
    {
        Box3 left = new Box3(new Vec3(0, 0, 0), new Vec3(1, 1, 0.2));
        Assert.False(VisualClash.Clashes(left, new Box3(new Vec3(0.9, 0, 0), new Vec3(1.9, 1, 0.2))));
        Assert.False(VisualClash.Clashes(left, new Box3(new Vec3(1, 0, 0), new Vec3(2, 1, 0.2))));
        Assert.True(VisualClash.Clashes(left, new Box3(new Vec3(0.85, 0, 0), new Vec3(1.85, 1, 0.2))));
    }

    [Fact]
    public void AThinBodyInsideADeepOneClashesHoweverThin()
    {
        Box3 deep = new Box3(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        Box3 plate = new Box3(new Vec3(0.2, 0.2, 0.5), new Vec3(0.8, 0.8, 0.55));
        Assert.True(double.IsPositiveInfinity(VisualClash.Depth(deep, plate)));
        Assert.True(VisualClash.Clashes(plate, deep));
        Assert.Equal(0.5, new Box3(new Vec3(0, 0, 0), new Vec3(1, 1, 1))
            .ClashDepth(new Box3(new Vec3(0.5, 0.5, 0.5), new Vec3(2, 2, 2))), 6);
    }
}
