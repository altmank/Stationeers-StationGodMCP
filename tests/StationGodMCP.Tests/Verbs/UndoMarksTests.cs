#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// Undo of a removal builds each thing again with the label and paint it had (ThingSnapshot.Label and Colour, recorded
/// by JobSnapshots): a structure's placement carries label and color as build structure takes them, a removed pipe,
/// cable or chute piece's cells carry them as build's pieces form takes them; a thing with neither carries neither.
/// </summary>
public sealed class UndoMarksTests
{
    private static ThingSnapshot Tank(string? label, int? colour) =>
        new ThingSnapshot(30, "StructureTankSmall", new Vec3(1.5, 2, 3), CubeRotation.Identity, 0, label,
            colour: colour);

    private static ThingSnapshot Pipe(string? label, int? colour) =>
        new ThingSnapshot(31, "StructurePipeStraight", new Vec3(1, 2, 3), CubeRotation.Identity, 0, label,
            new NetworkPiece("pipe", "gas", new List<PieceCell>
            {
                new PieceCell(new GridCell(10, 20, 30), RunModels.Ends("+x", "-x")),
                new PieceCell(new GridCell(15, 20, 30), RunModels.Ends("+x", "-x"))
            }), colour: colour);

    [Fact]
    public void ARemovedStructureComesBackLabelledAndPainted()
    {
        JObject placement = (JObject)UndoReplies.Placements(new List<ThingSnapshot> { Tank("Fuel", 4) })[0]!;
        JObject plain = (JObject)UndoReplies.Placements(new List<ThingSnapshot> { Tank(null, null) })[0]!;

        Assert.Equal("Fuel", (string?)placement["label"]);
        Assert.Equal(4, (int)placement["color"]!);
        Assert.Null(plain["label"]);
        Assert.Null(plain["color"]);
    }

    [Fact]
    public void ARemovedPieceComesBackLabelledAndPaintedInEveryCell()
    {
        JArray cells = UndoReplies.Cells(new PieceRestore("pipe", "gas", new List<ThingSnapshot> { Pipe("Main", 2) }));
        JArray plain = UndoReplies.Cells(new PieceRestore("pipe", "gas", new List<ThingSnapshot> { Pipe(null, null) }));

        Assert.Equal(2, cells.Count);
        Assert.All(cells, cell =>
        {
            Assert.Equal("Main", (string?)cell["label"]);
            Assert.Equal(2, (int)cell["color"]!);
        });
        Assert.All(plain, cell =>
        {
            Assert.Null(cell["label"]);
            Assert.Null(cell["color"]);
        });
    }
}
