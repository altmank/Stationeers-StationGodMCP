#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// No reply drops entries without saying so. Every reply carries truncated: per list held back, the list, how many it
/// carries, how many there are, and how to get more; empty when nothing was held back. The notice is written by the
/// shaping writer itself, so fields and omit cannot remove it, and the output_file pointer carries it. Enforced for
/// every tool: the writer's default cuts on the large world, and every handler or view that caps a list notes it.
/// </summary>
public sealed class TruncationNoticeTests
{
    private static ShapedText Reply(string json, ShapeRequest shape, IReadOnlyList<Truncation> notes) =>
        ApiJson.WriteShaped(ApiJson.Fresh(), ShapingChecks.Parse(json), shape, ShapingRoot.Result, notes, announce: true);

    [Fact]
    public void AReplyThatHoldsNothingBackSaysSo()
    {
        Assert.Equal("""{"things":[{"a":1}],"truncated":[]}""",
            Reply("""{"things":[{"a":1}]}""", ShapeRequest.None, new List<Truncation>()).Json);
    }

    [Fact]
    public void AHandlersPageIsNotedWithItsTotalAndHowToGetMore()
    {
        Truncations.Begin();
        PageRequest page = PageRequest.From(new Args(JObject.Parse("{}")), 8, 500);
        page.Note("things", 8, 412);
        List<Truncation> notes = Truncations.Take();

        JObject reply = JObject.Parse(Reply("""{"things":[]}""", ShapeRequest.None, notes).Json);

        JToken entry = reply["truncated"]![0]!;
        Assert.Equal("things", (string?)entry["list"]);
        Assert.Equal(8, (int)entry["returned"]!);
        Assert.Equal(412, (int)entry["total"]!);
        Assert.Equal("pass limit (max 500) or offset 8", (string?)entry["more"]);
        Assert.Null(entry["at_least"]);
    }

    [Fact]
    public void AWholePageIsNotNoted()
    {
        Truncations.Begin();
        PageRequest.From(new Args(JObject.Parse("{}")), 8, 500).Note("things", 3, 3);
        Truncations.Capped("totals", 12, 12, "limit", 500);

        Assert.Empty(Truncations.Take());
    }

    [Fact]
    public void ALowerBoundSaysAtLeast()
    {
        Truncations.Begin();
        Truncations.Note("spots", 5, 5, "pass count (max 20)", atLeast: true);

        JObject reply = JObject.Parse(Reply("{}", ShapeRequest.None, Truncations.Take()).Json);

        Assert.True((bool)reply["truncated"]![0]!["at_least"]!);
    }

    [Fact]
    public void FieldsAndOmitNeverShapeTheNoticeAway()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"fields":["a"],"omit":["truncated","count"]}"""))!;
        List<Truncation> notes = new List<Truncation> { new Truncation("things", 1, 9, "pass limit (max 500) or offset 1") };

        JObject reply = JObject.Parse(Reply("""{"count":9,"things":[{"a":1,"b":2}]}""", shape, notes).Json);

        Assert.Equal(9, (int)reply["truncated"]![0]!["total"]!);
        Assert.Null(reply["count"]);
    }

    [Fact]
    public void AWriterCutOfAPagedListKeepsTheRealTotalAndBothWays()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"limit":{"things":1}}"""))!;
        List<Truncation> notes = new List<Truncation> { new Truncation("things", 3, 412, "pass limit (max 500) or offset 3") };

        JObject reply = JObject.Parse(Reply("""{"things":[{"a":1},{"a":2},{"a":3}]}""", shape, notes).Json);

        JArray truncated = (JArray)reply["truncated"]!;
        Assert.Single(truncated);
        Assert.Equal(1, (int)truncated[0]["returned"]!);
        Assert.Equal(412, (int)truncated[0]["total"]!);
        Assert.Contains("offset 3", (string?)truncated[0]["more"]);
        Assert.Contains("list_limits", (string?)truncated[0]["more"]);
    }

    [Fact]
    public void ViewsThatCapNoteUnderTheirPath()
    {
        Truncations.Begin();
        RunLogView log = new RunLogView();
        log.PlacedPieces.Add(new ThingView(new ThingId(1), "StructureCableSuperHeavyStraight", "Cable"));
        RunJobView job = RunJobView.Brief(new RunJobView("j1", "place_cables", "applied", null,
            new RunJobResultView(null, log, null, null)), null);
        TruncatingViews.Note(new PlanHolder(job));

        Truncation note = Assert.Single(Truncations.Take());
        Assert.Equal("job.log.placed", note.List);
        Assert.Equal(1, note.Total);
    }

    private sealed class PlanHolder
    {
        internal PlanHolder(RunJobView job) => Job = job;

        public RunJobView Job { get; }
    }

    [Fact]
    public void ThePointerOfAFileReplyCarriesTheNotice()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sgm-truncated-" + Guid.NewGuid().ToString("N"));
        try
        {
            using JsonDocument reply = JsonDocument.Parse(
                """{"count":8,"things":[],"truncated":[{"list":"things","returned":8,"total":412,"more":"pass limit"}]}""");
            JsonElement pointer = new StationGodMCP.Client.OutputFolder(folder).Write("find_things",
                new StationGodMCP.Client.OutputTarget.Auto(), reply.RootElement);

            Assert.Equal(412, pointer.GetProperty("truncated")[0].GetProperty("total").GetInt32());
            Assert.False(pointer.GetProperty("counts").TryGetProperty("truncated", out _));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>
    /// Every method whose catalogue cuts lists by default (x-default-limits): on the large world its reply names each
    /// list it cut, with the list's whole length.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReplyBudgetTests.Shaped), MemberType = typeof(ReplyBudgetTests))]
    public void EveryDefaultCutOnTheLargeWorldIsAnnounced(string method)
    {
        IReadOnlyDictionary<string, int> limits = ReplyBudgetTests.DefaultLimitsOf(method);
        if (limits.Count == 0)
        {
            return;
        }

        foreach (ReplyShape shape in ReplyShapes.ByMethod[method])
        {
            object view = new ReplySynth(shape.Over(ReplyShapes.Common)).Make(shape.View);
            JObject whole = JObject.Parse(ApiJson.WriteFresh(view));
            ShapeRequest cut = ShapeRequest.WithDefaultLimits(null, limits)!;
            JObject written = JObject.Parse(ApiJson.WriteShaped(ApiJson.Fresh(), view, cut, ShapingRoot.Result,
                new List<Truncation>(), announce: true).Json);
            JArray truncated = (JArray)written["truncated"]!;
            foreach (KeyValuePair<string, int> limit in limits)
            {
                int length = whole[limit.Key] is JArray list ? list.Count : 0;
                JToken? entry = truncated.FirstOrDefault(note => (string?)note["list"] == limit.Key);
                if (length > limit.Value)
                {
                    Assert.True(entry != null, $"{method} {shape.View.Name}: {limit.Key} cut to {limit.Value} of {length} unannounced");
                    Assert.Equal(length, (int)entry!["total"]!);
                    Assert.Equal(limit.Value, (int)entry["returned"]!);
                }
                else
                {
                    Assert.Null(entry);
                }
            }
        }
    }

    /// <summary>
    /// Files that set how much a list holds by default or at most (a ReplyDefaults constant, a page) and where that
    /// cut is noted: in the file itself, or by the view its reply carries the list in (ITruncatingView).
    /// </summary>
    private static readonly Dictionary<string, Type> NotedByView = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["Api/GetIcStatus.cs"] = typeof(StackWindowView),
        ["Api/RunConsoleCommand.cs"] = typeof(ConsoleRunView),
        ["Api/Shared/Game/ConsoleBridge.cs"] = typeof(ConsoleRunView),
        ["Api/Shared/Game/LuaChips.cs"] = typeof(LuaLogView),
        ["Api/Shared/Game/Runs/RunArgs.cs"] = typeof(RunReportView),
        ["Api/Shared/Game/Upgrades/UpgradePlan.cs"] = typeof(UpgradeReportView),
        ["Api/UpgradeNetwork.cs"] = typeof(UpgradeReportView),
        ["Api/Shared/StructureSwapArgs.cs"] = typeof(StructureSwapReportView),
    };

    [Fact]
    public void EveryCapAHandlerSetsIsNoted()
    {
        List<string> unnoted = new List<string>();
        int checkedFiles = 0;
        foreach (SourceFile file in ModSource.Instance.Files)
        {
            string relative = file.Path.Replace('\\', '/');
            relative = relative.Substring(relative.IndexOf("Api/", StringComparison.Ordinal) >= 0
                ? relative.IndexOf("Api/", StringComparison.Ordinal)
                : 0);
            string text = file.Root.ToFullString();
            bool caps = (text.Contains("ReplyDefaults.") || text.Contains("PageRequest.From(")) &&
                        !relative.EndsWith("ReplyDefaults.cs", StringComparison.Ordinal) &&
                        !relative.EndsWith("Paging.cs", StringComparison.Ordinal);
            if (!caps)
            {
                continue;
            }

            checkedFiles++;
            bool noted = text.Contains("Truncations.") || text.Contains(".Note(\"") ||
                         text.Contains("NoteTruncations(") ||
                         (NotedByView.TryGetValue(relative, out Type? view) && typeof(ITruncatingView).IsAssignableFrom(view));
            if (!noted)
            {
                unnoted.Add(relative);
            }
        }

        Assert.True(checkedFiles >= 20, $"only {checkedFiles} files set a cap: the scan found no sources");
        Assert.True(unnoted.Count == 0, "Caps a list without noting it: " + string.Join(", ", unnoted));
    }
}
