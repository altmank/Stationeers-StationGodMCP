#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// show preview without the game: the placements list and the one top-level placement (read by the shared place
/// readers, so a missing at, above_floor, a * prefab pattern and the fields that go without a list are the refusals
/// build structure makes), the cells and boxes a call draws, allow door_keepout, the seconds cap and the clear branch
/// (which reads nothing else).
/// </summary>
public sealed class PreviewOrderTests
{
    private static PreviewOrder? Order(string json, List<OptionProblem> problems) =>
        PreviewOrder.Of(ShowPreviewOptions.Bind(new OptionReader(ShowPreviewOptions.Spec, JObject.Parse(json))),
            problems);

    private static PreviewOrder Drawn(string json) => Order(json, new List<OptionProblem>())!;

    [Fact]
    public void APlacementsListIsReadThroughTheSharedReader()
    {
        PreviewOrder order = Drawn("""
            {"placements": [{"prefab": "StructureWallLight", "at": [1, 2.5, 3], "facing": "-x"},
                            {"prefab": -1234, "at": {"x": 0, "y": 0, "z": 0}, "build_state": "first",
                             "above_floor": 1.5}],
             "seconds": 90, "keep": true, "xray": true, "allow": ["door_keepout"]}
            """);

        Assert.Equal(90, order.Seconds);
        Assert.True(order.Keep);
        Assert.True(order.Xray);
        Assert.False(order.Clear);
        Assert.True(order.HasPlacements);

        // The request the game plans: nobody pays, it is not free, no lint, no pad, a brief report.
        PlaceRequest arguments = order.Arguments;
        Assert.True(arguments.AllowDoorKeepOut);
        Assert.Null(arguments.From);
        Assert.False(arguments.Free);
        Assert.False(arguments.AllowLint);
        Assert.False(arguments.AllowPad);
        Assert.False(arguments.FootprintCells);
        Assert.False(arguments.Detail.Notes);
        Assert.False(arguments.Detail.LayoutsWhenRefused);
        Assert.Empty(arguments.MoreFrom);
        Assert.Equal(2, arguments.Placements.Count);
        PlacementRequest first = arguments.Placements[0];
        Assert.Equal(0, first.Index);
        Assert.Equal("StructureWallLight", Assert.IsType<PrefabRef.Named>(first.Prefab).Name);
        Assert.Equal(2.5, Assert.IsType<AtForm.Absolute>(first.At).Point.Y);
        Assert.Equal("-x", Assert.IsType<RotationSpec.Facing>(first.Rotation).Forward.Name);
        PlacementRequest second = arguments.Placements[1];
        Assert.Equal(1, second.Index);
        Assert.Equal(-1234, Assert.IsType<PrefabRef.Hashed>(second.Prefab).Hash);
        Assert.Equal(0, Assert.IsType<BuildStatePick.Index>(second.State).Value);
        Assert.Equal(1.5, second.AboveFloorM);
    }

    [Fact]
    public void TheOneTopLevelPlacementIsRead()
    {
        PreviewOrder order = Drawn("""
            {"prefab": "StructureWallLight", "at": [1, 2.5, 3], "rotation": [0, 90, 0], "above_floor": 1.5}
            """);

        Assert.True(order.HasPlacements);
        PlacementRequest placement = Assert.Single(order.Arguments.Placements);
        Assert.Equal(0, placement.Index);
        Assert.Equal(3, Assert.IsType<AtForm.Absolute>(placement.At).Point.Z);
        Assert.Equal(1, Assert.IsType<RotationSpec.Euler>(placement.Rotation).YTurns);
        Assert.Equal(1.5, placement.AboveFloorM);
        Assert.Equal(30, order.Seconds);
        Assert.False(order.Keep || order.Xray || order.Clear);
        Assert.False(order.Arguments.AllowDoorKeepOut);
    }

    [Fact]
    public void CellsAndBoxesWithoutAPlacementAreNoProblem()
    {
        PreviewOrder order = Drawn("""
            {"cells": [[700, 203, 668]],
             "boxes": [{"min": [0, 0, 0], "max": [1, 2, 3], "color": "green"},
                       {"min": [0, 0, 0], "max": [1, 1, 1]}]}
            """);

        Assert.False(order.HasPlacements);
        Assert.Empty(order.Arguments.Placements);
        PreviewBox cell = Assert.Single(order.Cells);
        Assert.Equal("cell", cell.Name);
        Assert.Equal(700 - 0.25, cell.Box.Min.X, 6);
        Assert.Equal(700 + 0.25, cell.Box.Max.X, 6);
        Assert.Equal(668 - 0.25, cell.Box.Min.Z, 6);
        Assert.Equal(30f, cell.Seconds);
        Assert.Equal(PreviewColours.Cell, cell.Color);
        Assert.Equal(2, order.Boxes.Count);
        PreviewBox green = order.Boxes[0];
        Assert.Equal("box", green.Name);
        Assert.Equal(PreviewColours.Footprint, green.Color);
        Assert.Equal(1, green.Box.Max.X, 6);
        Assert.Equal(3, green.Box.Max.Z, 6);
        Assert.Equal(0, green.Box.Min.X, 6);
        Assert.Equal(PreviewColours.Cell, order.Boxes[1].Color);
    }

    [Fact]
    public void ABoxColourIsReadByItsName()
    {
        PreviewOrder order = Drawn("""{"xray": true, "seconds": 5, "boxes": [{"min": [0, 0, 0], "max": [1, 1, 1], "color": " CYAN "}]}""");

        PreviewBox box = Assert.Single(order.Boxes);
        Assert.Equal(PreviewColours.Port, box.Color);
        Assert.True(box.XRay);
        Assert.Equal(5f, box.Seconds);
    }

    [Theory]
    [InlineData("""{"placements": []}""", "placements")]
    [InlineData("""{"placements": [{"prefab": "A"}]}""", "placements[0].at")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}, 3]}""", "placements[1]")]
    [InlineData("""{"prefab": "A"}""", "at")]
    [InlineData("""{"prefab": "A", "at": [0, 0]}""", "at")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "facing": "north"}""", "facing")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "build_state": "half"}""", "build_state")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "above_floor": 21}""", "above_floor")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0], "above_floor": 21}]}""",
        "placements[0].above_floor")]
    [InlineData("""{"prefab": "*Wall*", "at": [0, 0, 0]}""", "prefab")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}], "at": [1, 1, 1]}""", "at")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}], "prefab": "B"}""", "prefab")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}], "above_floor": 1}""", "above_floor")]
    [InlineData("""{"cells": [[1, 1]]}""", "cells[0]")]
    [InlineData("""{"boxes": [{"min": [0, 0, 0]}]}""", "boxes[0].max")]
    [InlineData("""{"boxes": [{"min": [0, 0, 0], "max": [1, 1, 1], "color": "purple"}]}""", "boxes[0].color")]
    [InlineData("""{"boxes": [3]}""", "boxes[0]")]
    [InlineData("""{"seconds": 301}""", "seconds")]
    public void AMalformedCallIsAProblemAtItsPath(string json, string path)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        Assert.Equal(path, Assert.Single(problems).Path);
    }

    [Fact]
    public void TheCapsHold()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("{\"cells\": [" + Points(PreviewOrder.MaximumCells + 1) + "]}", problems));
        Assert.Equal("cells", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(Order("{\"boxes\": [" + Boxes(PreviewOrder.MaximumBoxes + 1) + "]}", problems));
        Assert.Equal("boxes", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(Order("{\"placements\": [" + Placements(PreviewOrder.MaximumPlacements + 1) + "]}", problems));
        Assert.Equal("placements", Assert.Single(problems).Path);
    }

    [Fact]
    public void AnItemKeyThatNamesNoPlacementFieldIsRefused()
    {
        // R-D-22: a key that names no placement field is a problem with the nearest names, never dropped (the preview
        // has no spread at all; above_floor_m is no name of this catalogue).
        List<OptionProblem> problems = new();

        Assert.Null(Order(
            """{"placements": [{"prefab": "A", "at": [0, 0, 0], "bogus": 1, "spread": {"from": [0, 0, 0]}, "above_floor_m": 1}]}""",
            problems));

        Assert.Equal(new[] { "placements[0].bogus", "placements[0].spread", "placements[0].above_floor_m" },
            problems.ConvertAll(problem => problem.Path));
        Assert.Contains("above_floor", problems[2].Nearest!);
    }

    [Fact]
    public void ABoxKeyThatNamesNoBoxFieldIsRefused()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"boxes": [{"min": [0, 0, 0], "max": [1, 1, 1], "colour": "red"}]}""", problems));

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("boxes[0].colour", problem.Path);
        Assert.Contains("color", problem.Nearest!);
    }

    [Fact]
    public void TheReplyCarriesThePlanUnderItsKey()
    {
        // The plan is build structure's own report of the placements, without the keys a job reply has: the reply's
        // key is plan (the catalogue's), and it is left out altogether when the call drew no placement.
        JObject plan = new JObject { ["ready"] = true, ["placements"] = new JArray() };
        JObject drawn = JObject.Parse(WireCheck.New(new ShowPreviewView(3, 0, 30, plan, new List<string>(), null,
            new ViewSourceView("local", 0.0))));

        Assert.Equal(3, (int)drawn["shown"]!);
        Assert.True((bool)drawn["plan"]!["ready"]!);
        Assert.False(drawn.ContainsKey("dry_run"));

        JObject cleared = JObject.Parse(
            WireCheck.New(new ShowPreviewView(0, 2, 0, null, new List<string> { "Cleared." })));
        Assert.False(cleared.ContainsKey("plan"));
    }

    [Fact]
    public void AClearCallReadsNothingElse()
    {
        // clear only removes what is drawn: the old surface read no placements, cells or boxes then (a seconds the
        // schema would refuse is never read either).
        PreviewOrder order = Drawn("""
            {"clear": true, "seconds": 120, "xray": true, "cells": [[1, 1, 1]],
             "boxes": [{"min": [0, 0, 0], "max": [1, 1, 1], "color": "purple"}]}
            """);

        Assert.True(order.Clear);
        Assert.Equal(0, order.Seconds);
        Assert.False(order.HasPlacements);
        Assert.Empty(order.Arguments.Placements);
        Assert.Empty(order.Cells);
        Assert.Empty(order.Boxes);
        Assert.False(order.Keep);
        Assert.False(order.Xray);
    }

    private static string Points(int count)
    {
        StringBuilder points = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            points.Append(index > 0 ? ", " : "").Append("[1, 2, 3]");
        }

        return points.ToString();
    }

    private static string Boxes(int count)
    {
        StringBuilder boxes = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            boxes.Append(index > 0 ? ", " : "").Append("""{"min": [0, 0, 0], "max": [1, 1, 1]}""");
        }

        return boxes.ToString();
    }

    private static string Placements(int count)
    {
        StringBuilder placements = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            placements.Append(index > 0 ? ", " : "").Append("""{"prefab": "A", "at": [0, 0, 0]}""");
        }

        return placements.ToString();
    }
}
