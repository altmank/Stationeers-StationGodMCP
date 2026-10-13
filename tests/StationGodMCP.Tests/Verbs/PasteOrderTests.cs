#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// build blueprint's order and report: the option problems are the what's own, in verb words (blueprint, at, turn); a
/// ready report lists every piece of the file as a target, at the cell the paste geometry gives it, with the what's
/// undo mark; and a check that refused the paste is the report's one problem (WriteCheck.Outcome).
/// </summary>
public sealed class PasteOrderTests
{
    private static PasteOrder? Order(string json, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        return PasteOrder.Of(BuildBlueprintOptions.Bind(new OptionReader(BuildBlueprintOptions.Spec, JObject.Parse(json))),
            problems);
    }

    [Fact]
    public void TheCataloguesExampleReads()
    {
        JObject call = JObject.Parse(
            """{"what": "blueprint", "blueprint": "airlock", "at": [700, 202, 660], "confirm": true}""");

        Assert.Null(OptionCheck.Check(VerbSurface.Verb("build")!, call, out _));
    }

    [Fact]
    public void APasteOrderReadsTheBlueprintTheAnchorAndTheTurn()
    {
        PasteOrder order = Order("""{"blueprint": "airlock", "at": [10, 2.5, -4]}""", out List<OptionProblem> problems)!;

        Assert.Empty(problems);
        Assert.Equal("airlock", order.Blueprint);
        Assert.Equal((10.0, 2.5, -4.0), (order.At.X, order.At.Y, order.At.Z));
        Assert.Equal(0, order.Turn.Degrees);
        Assert.False(order.Report.Acts);
        Assert.Equal("build blueprint", order.Label);
        Assert.Equal("[undo] the newest paste only", order.Mark);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TheTurnIsAQuarterTurn(int degrees)
    {
        PasteOrder order = Order(
            "{\"blueprint\": \"airlock\", \"at\": [1, 2, 3], \"turn\": " + degrees + "}",
            out List<OptionProblem> problems)!;

        Assert.Empty(problems);
        Assert.Equal(degrees, order.Turn.Degrees);
        Assert.True(Order("{\"blueprint\": \"airlock\", \"at\": [1, 2, 3], \"turn\": " + degrees +
            ", \"confirm\": true}", out _)!.Report.Acts);
    }

    [Fact]
    public void ABadOptionIsAProblemOfTheWhatInVerbWords()
    {
        Assert.Null(Order("""{"blueprint": "", "at": [1, 2], "turn": 45}""", out List<OptionProblem> problems));
        Assert.Equal(new[] { "blueprint", "at", "turn" }, Paths(problems));
        Assert.Equal("is a blueprint's file name in BlueprintMod's folder, or an absolute path", problems[0].Problem);
        Assert.Equal("is the anchor's position [x, y, z] in metres", problems[1].Problem);
        Assert.Equal("is 0, 90, 180 or 270 degrees", problems[2].Problem);
    }

    [Fact]
    public void AnAnchorOffTheWorldIsAProblem()
    {
        Assert.Null(Order("""{"blueprint": "airlock", "at": [1, 2, 100001]}""", out List<OptionProblem> problems));

        Assert.Equal("at", Assert.Single(problems).Path);
        Assert.Equal("is beyond 100000 m of the world origin", problems[0].Problem);
    }

    [Fact]
    public void AReadyReportListsEveryPieceWithTheUndoMark()
    {
        List<PastePiece> pieces = new List<PastePiece>
        {
            new PastePiece("StructureWallLight", new Vec3(700.0, 202.0, 660.0)),
            new PastePiece("StructureWall", new Vec3(701.25, 202.0, 660.5))
        };
        BlueprintPasteReportView body = new BlueprintPasteReportView(
            new BlueprintFileView(@"C:\Blueprints\airlock.blueprint", 2), new[] { 700.0, 202.0, 660.0 }, 90, 180f, 2.0);

        JObject report = PasteReplies.Reported(pieces, body, "[undo] the newest paste only");

        Assert.True((bool)report["report_only"]!);
        Assert.Empty(report["problems"]!);
        Assert.Equal(2, (int)report["entries"]!);
        Assert.Equal(90, (int)report["turn"]!);
        Assert.Equal("C:\\Blueprints\\airlock.blueprint", (string?)report["file"]);
        Assert.Equal("[undo] the newest paste only", (string?)report["targets"]![0]!["undo"]);
        Assert.Equal("[[700.0,202.0,660.0], StructureWallLight, ? -> StructureWallLight] [undo] the newest paste only",
            (string?)report["targets"]![0]!["text"]);
        Assert.Equal("[[701.25,202.0,660.5], StructureWall, ? -> StructureWall] [undo] the newest paste only",
            (string?)report["targets"]![1]!["text"]);
        Assert.Equal(BlueprintPasteReportView.StartChecksNote, (string?)report["notes"]![0]);
        Assert.Equal(BlueprintPasteReportView.LoosePiecesNote, (string?)report["notes"]![1]);
    }

    [Fact]
    public void ARefusedCheckIsTheReportsOneProblemAndNothingStarts()
    {
        int acts = 0;
        VerbOutcome outcome = WriteCheck<string>.Run(() =>
                throw ApiErrors.Refused("paste_refused",
                    "Another paste is still placing (12 pieces created so far); nothing was started."))
            .Outcome(Reporting, plan =>
            {
                acts++;
                return new VerbOutcome.Answered(new JObject());
            }, plan => new JObject());

        VerbOutcome.Answered answered = Assert.IsType<VerbOutcome.Answered>(outcome);
        Assert.Equal(0, acts);
        Assert.True((bool)answered.Reply["report_only"]!);
        Assert.Equal("paste_refused", (string?)answered.Reply["problems"]![0]!["code"]);
        Assert.Equal("Another paste is still placing (12 pieces created so far); nothing was started.",
            (string?)answered.Reply["problems"]![0]!["message"]);
    }

    private static ReportConfirm Reporting { get; } = new ReportConfirm(true, false, null);

    private static List<string> Paths(List<OptionProblem> problems)
    {
        List<string> paths = new List<string>(problems.Count);
        foreach (OptionProblem problem in problems)
        {
            paths.Add(problem.Path);
        }

        return paths;
    }
}
