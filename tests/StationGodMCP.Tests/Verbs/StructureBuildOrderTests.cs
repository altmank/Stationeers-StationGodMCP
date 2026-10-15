#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// build structure without the game: one placement at the top level or a placements list (at most 64); a placement's
/// fields read by the shared place readers (prefab, at, rotation, facing, up, face, orient, above_floor, build_state,
/// label, color); spread {from, to, step} or a list of them standing in for at (from included, to only on a step);
/// materials and free; allow door_keepout and lint; include notes and footprint_cells; verbose, confirm, queue and a
/// blank acknowledge_gas_lost.
/// </summary>
public sealed class StructureBuildOrderTests
{
    private static StructureBuildOrder? Order(string json, List<OptionProblem> problems, out VerbError? missing) =>
        StructureBuildOrder.Of(BuildStructureOptions.Bind(new OptionReader(BuildStructureOptions.Spec,
            JObject.Parse(json))), problems, out missing);

    [Fact]
    public void OnePlacementAtTheTopLevelIsAReportByDefault()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order(
            """{"prefab": "StructureWallLight", "at": [1, 2.5, 3], "facing": "-x"}""", problems,
            out VerbError? missing)!;

        Assert.Empty(problems);
        Assert.Null(missing);
        Assert.Equal("build structure", order.Label);
        Assert.Equal("[undo]", order.Mark);
        PlacementRequest placement = Assert.Single(order.Placements);
        Assert.Equal(0, placement.Index);
        Assert.Equal("StructureWallLight", Assert.IsType<PrefabRef.Named>(placement.Prefab).Name);
        Assert.Equal(2.5, Assert.IsType<AtForm.Absolute>(placement.At).Point.Y);
        Assert.Equal("-x", Assert.IsType<RotationSpec.Facing>(placement.Rotation).Forward.Name);
        Assert.IsType<BuildStatePick.Last>(placement.State);
        Assert.Null(placement.Label);
        Assert.Null(placement.Color);
        Assert.False(order.Free);
        Assert.False(order.Report.Acts);
        Assert.True(order.Queue);
        Assert.Same(MaterialsSource.Player.Payers, order.Materials.Payers);
        Assert.False(order.AllowDoorKeepOut || order.AllowLint || order.AllowPad);
        Assert.False(order.FootprintCells);
    }

    [Fact]
    public void AListKeepsItsOrderAndReadsEveryField()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""
            {"placements": [
                {"prefab": -1234, "at": {"x": 0, "y": 0, "z": 0}, "rotation": [0, 270, 0], "build_state": "first",
                 "label": "Hall", "color": "Blue"},
                {"prefab": "42", "at": [1, 1, 1], "face": "+z", "up": "+y", "build_state": 2, "color": 3,
                 "above_floor": 1.5}],
             "free": true}
            """, problems, out VerbError? missing)!;

        Assert.Empty(problems);
        Assert.Null(missing);
        Assert.Equal(2, order.Placements.Count);
        Assert.True(order.Free);
        PlacementRequest first = order.Placements[0];
        Assert.Equal(-1234, Assert.IsType<PrefabRef.Hashed>(first.Prefab).Hash);
        Assert.Equal(3, Assert.IsType<RotationSpec.Euler>(first.Rotation).YTurns);
        Assert.Equal(0, Assert.IsType<BuildStatePick.Index>(first.State).Value);
        Assert.Equal("Hall", first.Label);
        Assert.Equal("Blue", first.Color);
        PlacementRequest second = order.Placements[1];
        Assert.Equal(1, second.Index);
        Assert.Equal(42, Assert.IsType<PrefabRef.Hashed>(second.Prefab).Hash);
        Assert.Equal("+z", Assert.IsType<RotationSpec.OnFace>(second.Rotation).Face.Name);
        Assert.Equal("+y", ((RotationSpec.OnFace)second.Rotation).Up!.Value.Name);
        Assert.Equal(2, Assert.IsType<BuildStatePick.Index>(second.State).Value);
        Assert.Equal("3", second.Color);
        Assert.Equal(1.5, second.AboveFloorM);
    }

    [Theory]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}, {"at": [0, 0, 0]}]}""", "placements[1].prefab")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}, 3]}""", "placements[1]")]
    [InlineData("""{"placements": []}""", "placements")]
    [InlineData("""{"prefab": "A"}""", "at")]
    [InlineData("""{"prefab": "A", "at": [0, 0]}""", "at")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "rotation": [0, 90, 0], "facing": "+x"}""", "prefab")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "up": "+y"}""", "up")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "rotation": [0, 45, 0]}""", "rotation")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "facing": "north"}""", "facing")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "build_state": "half"}""", "build_state")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0], "label": 3}]}""", "placements[0].label")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "color": [1]}""", "color")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "above_floor": 21}""", "above_floor")]
    [InlineData("""{"prefab": "*Wall*", "at": [0, 0, 0]}""", "prefab")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}], "at": [1, 1, 1]}""", "at")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0], "spread": {"from": [0, 0, 0]}}]}""",
        "placements[0].spread")]
    public void AMalformedPlacementIsAProblemAtItsPath(string json, string path)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Equal(path, Assert.Single(problems).Path);
    }

    [Fact]
    public void NoPlacementIsMissingTarget()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("{}", problems, out VerbError? missing));
        Assert.Empty(problems);
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal("build structure: name the placements (placements), or one placement by prefab with at.",
            missing.Message);
        Assert.Equal(new[] { "placements", "prefab" },
            missing.Data!["options"]!.Select(option => (string)option!));

        problems.Clear();
        Assert.Null(Order("""{"free": true}""", problems, out missing));
        Assert.Equal("missing_target", missing!.Code);
    }

    [Fact]
    public void ConfirmQueueAndTheAcknowledgementAreRead()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder acting = Order(
            """{"prefab": "A", "at": [0, 0, 0], "confirm": true, "queue": false, "acknowledge_gas_lost": "run-7"}""",
            problems, out _)!;

        Assert.Empty(problems);
        Assert.True(acting.Report.Acts);
        Assert.False(acting.Queue);
        Assert.Equal("run-7", acting.AcknowledgeGasLost);

        problems.Clear();
        Assert.Null(Order("""{"prefab": "A", "at": [0, 0, 0], "acknowledge_gas_lost": "  "}""", problems, out _));
        Assert.Equal(GasHoldVerdict.AcknowledgeArgument, Assert.Single(problems).Path);
    }

    [Fact]
    public void MaterialsAllowIncludeAndVerboseAreRead()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""
            {"prefab": "A", "at": [0, 0, 0], "materials": ["720", "player"], "allow": ["door_keepout", "lint"],
             "include": ["notes", "footprint_cells"], "verbose": true}
            """, problems, out _)!;

        Assert.Empty(problems);
        Assert.Equal(new[] { "720", "player" },
            order.Materials.Payers.Select(payer => payer is Payer.Holder holder ? holder.Id : "player"));
        Assert.Same(RefundRoute.Default, order.Materials.RefundTo);
        Assert.True(order.AllowDoorKeepOut && order.AllowLint);
        Assert.True(order.FootprintCells);
        Assert.True(order.Fields.Includes("notes"));
        Assert.True(order.Fields.Verbose);
    }

    [Fact]
    public void AllowPadIsReadAndAReplayNeverLiftsTheGuard()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""{"prefab": "A", "at": [0, 0, 0], "allow": ["pad"]}""", problems,
            out _)!;

        Assert.Empty(problems);
        Assert.True(order.AllowPad);
        Assert.False(order.AllowLint || order.AllowDoorKeepOut);
        Assert.True(order.Arguments(null, new List<ThingId>()).AllowPad);

        // undo's replay gives no allow, so the pad approach guard stands on the placements it puts back.
        StructureBuildOrder replay = StructureBuildOrder.Replay(
            JArray.Parse("""[{"prefab": "A", "at": [0, 0, 0]}]"""), null, acts: true, queue: true,
            acknowledgeGasLost: null, problems: problems)!;

        Assert.False(replay.AllowPad);
        Assert.False(replay.Arguments(null, new List<ThingId>()).AllowPad);
    }

    [Fact]
    public void ASpreadTakesEveryStepIncludingFromAndToWhenItLands()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""
            {"prefab": "StructureWallLight", "spread": {"from": [700, 203, 668], "to": [724, 203, 668], "step": 4},
             "facing": "-z", "label": "Hangar"}
            """, problems, out VerbError? missing)!;

        Assert.Empty(problems);
        Assert.Null(missing);
        Assert.Equal(7, order.Placements.Count);
        Assert.Equal(new[] { 700.0, 704.0, 708.0, 712.0, 716.0, 720.0, 724.0 },
            order.Placements.Select(placement => ((AtForm.Absolute)placement.At).Point.X));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, order.Placements.Select(placement => placement.Index));
        foreach (PlacementRequest placement in order.Placements)
        {
            Assert.Equal("-z", Assert.IsType<RotationSpec.Facing>(placement.Rotation).Forward.Name);
            Assert.Equal("Hangar", placement.Label);
        }
    }

    [Fact]
    public void AStepThatMissesToLeavesItOut()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order(
            """{"prefab": "A", "spread": {"from": [0, 0, 0], "to": [13, 0, 0], "step": 4}}""", problems, out _)!;
        StructureBuildOrder still = Order(
            """{"prefab": "A", "spread": {"from": [3, 0, 3], "to": [3, 0, 3], "step": 2}}""", problems, out _)!;

        Assert.Empty(problems);
        Assert.Equal(new[] { 0.0, 4.0, 8.0, 12.0 },
            order.Placements.Select(placement => ((AtForm.Absolute)placement.At).Point.X));
        Assert.Equal(new[] { 3.0 }, still.Placements.Select(placement => ((AtForm.Absolute)placement.At).Point.X));
    }

    [Fact]
    public void ASpreadListIsOneLineEach()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""
            {"prefab": "A", "spread": [
                {"from": [0, 0, 0], "to": [2, 0, 0], "step": 1},
                {"from": [0, 0, 5], "to": [0, 0, 7], "step": 1}]}
            """, problems, out VerbError? missing)!;

        Assert.Empty(problems);
        Assert.Null(missing);
        Assert.Equal(new[] { 0.0, 1.0, 2.0, 0.0, 0.0, 0.0 },
            order.Placements.Select(placement => ((AtForm.Absolute)placement.At).Point.X));
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 5.0, 6.0, 7.0 },
            order.Placements.Select(placement => ((AtForm.Absolute)placement.At).Point.Z));
    }

    [Fact]
    public void ASpreadOverThePlacementLimitIsRefused()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"prefab": "A", "spread": {"from": [0, 0, 0], "to": [64, 0, 0], "step": 1}}""",
            problems, out _));
        OptionProblem over = Assert.Single(problems);
        Assert.Equal("spread", over.Path);
        Assert.Contains("65 placements", over.Problem);

        // 64 exactly is the limit itself: the whole placements list.
        problems.Clear();
        Assert.Equal(64, Order("""{"prefab": "A", "spread": {"from": [0, 0, 0], "to": [63, 0, 0], "step": 1}}""",
            problems, out _)!.Placements.Count);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("""{"prefab": "A", "spread": {"from": [0, 0, 0], "to": [4, 0, 0]}}""", "spread")]
    [InlineData("""{"prefab": "A", "spread": {"from": [0, 0, 0], "to": [4, 0, 0], "step": 0}}""", "spread.step")]
    [InlineData("""{"prefab": "A", "spread": {"from": [0, 0], "to": [4, 0, 0], "step": 1}}""", "spread.from")]
    [InlineData("""{"prefab": "A", "spread": 3}""", "spread")]
    [InlineData("""{"prefab": "A", "spread": [3]}""", "spread[0]")]
    [InlineData("""{"prefab": "A", "spread": {"from": [0, 0, 0], "to": [4, 0, 0], "step": 1}, "at": [1, 1, 1]}""",
        "spread")]
    [InlineData("""{"placements": [{"prefab": "A", "at": [0, 0, 0]}], "spread": {"from": [0, 0, 0], "to": [4, 0, 0], "step": 1}}""", "spread")]
    public void AMalformedSpreadIsAProblemAtItsPath(string json, string path)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems, out _));
        Assert.Equal(path, Assert.Single(problems).Path);
    }

    [Fact]
    public void ASpotsBuildArgumentsFeedTheReader()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""
            {"what": "structure", "prefab": "StructureWallLight", "at": [1, 2, 3], "facing": "-x", "up": "+y"}
            """, problems, out VerbError? missing)!;

        Assert.Empty(problems);
        Assert.Null(missing);
        PlacementRequest placement = Assert.Single(order.Placements);
        Assert.Equal("StructureWallLight", Assert.IsType<PrefabRef.Named>(placement.Prefab).Name);
        Assert.Equal("-x", Assert.IsType<RotationSpec.Facing>(placement.Rotation).Forward.Name);
        Assert.Equal("+y", ((RotationSpec.Facing)placement.Rotation).Up!.Value.Name);

        // up is a prose default ("+y, +z when facing is vertical"): left out, the game applies it (D-A4-18).
        problems.Clear();
        StructureBuildOrder plain = Order("""{"prefab": "StructureWallLight", "at": [1, 2, 3], "facing": "+y"}""",
            problems, out _)!;
        RotationSpec.Facing facing = Assert.IsType<RotationSpec.Facing>(Assert.Single(plain.Placements).Rotation);
        Assert.Null(facing.Up);
        Assert.Equal("+z", RotationSpec.DefaultUp(facing.Forward).Name);
    }

    [Fact]
    public void AReplayReadsThePlacementsAndTheSource()
    {
        List<OptionProblem> problems = new();
        JArray placements = JArray.Parse("""
            [{"prefab": "StructureWallIron", "at": [1, 2, 3], "rotation": [0, 90, 0], "build_state": 1,
              "label": "Hall"}]
            """);

        StructureBuildOrder order = StructureBuildOrder.Replay(placements, "77", acts: true, queue: true,
            acknowledgeGasLost: "run-7", problems: problems)!;

        Assert.Empty(problems);
        Assert.True(order.Report.Acts);
        Assert.True(order.Queue);
        Assert.Equal("run-7", order.AcknowledgeGasLost);
        PlacementRequest placement = Assert.Single(order.Placements);
        Assert.Equal("StructureWallIron", Assert.IsType<PrefabRef.Named>(placement.Prefab).Name);
        Assert.Equal(1, Assert.IsType<BuildStatePick.Index>(placement.State).Value);
        Assert.Equal("Hall", placement.Label);
        Assert.Equal("77", Assert.IsType<Payer.Holder>(Assert.Single(order.Materials.Payers)).Id);

        problems.Clear();
        Assert.Null(StructureBuildOrder.Replay(JArray.Parse("""[{"prefab": "A"}]"""), null, acts: false,
            queue: false, acknowledgeGasLost: null, problems: problems));
        Assert.Equal("placements[0].at", Assert.Single(problems).Path);
    }
}
