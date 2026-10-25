#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The shared reply writers: the per-id rows with their counts (SPEC-NOTES 14; D13 for one id), a list page (PLAN 2.0
/// paging) and the one report shape (SPEC-NOTES 18: every target as [id or cell, name, current -> new] with its undo
/// mark, lifted guards and driven_by; report_only).
/// </summary>
public sealed class ReplyWriterTests
{
    private static string Text(JToken token) => token.ToString(Formatting.None);

    private static string Text(object reply) => StationGodMCP.Api.Shared.ApiJson.WriteShared(reply);

    private static PerIdList Ids(params string[] ids) => new(ids, ids.Length != 1, null);

    [Fact]
    public void RowsAreOnePerIdInOrderWithTheirCounts()
    {
        Rows rows = new();
        rows.Succeeded("1", new JObject { ["value"] = 1.0 });
        rows.Failed("2", new VerbError("not_found", "No thing 2."));

        JObject reply = Replies.Tree(rows.Reply(new JObject { ["gateway_id"] = "world" }));

        Assert.Equal(
            """{"gateway_id":"world","results":[{"index":0,"ok":true,"reference_id":"1","value":1.0},""" +
            """{"index":1,"ok":false,"reference_id":"2","error":{"code":"not_found","message":"No thing 2."}}],""" +
            "\"count\":2,\"success_count\":1,\"error_count\":1}",
            Text(reply));
    }

    [Fact]
    public void APayloadCannotOverwriteARowsOwnKeys()
    {
        Rows rows = new();
        rows.Succeeded("1", new JObject { ["index"] = 9, ["ok"] = false, ["reference_id"] = "x", ["value"] = 2 });

        Assert.Equal("""{"index":0,"ok":true,"reference_id":"1","value":2}""", Text(Replies.Tree(rows.Reply())["results"]![0]!));
    }

    [Fact]
    public void AListOfIdsAnswersTheRows()
    {
        Rows rows = new();
        rows.Succeeded("1", new JObject { ["value"] = 1 });

        VerbOutcome.Answered answered = Assert.IsType<VerbOutcome.Answered>(rows.For(new PerIdList(new[] { "1" }, true, null)));

        Assert.Equal(1, (int)Replies.Tree(answered.Reply)["count"]!);
    }

    [Fact]
    public void OneIdAnswersItsPayloadAsTheSingleShape()
    {
        Rows rows = new();
        rows.Succeeded("7", new JObject { ["value"] = 3 });

        VerbOutcome.Answered answered = Assert.IsType<VerbOutcome.Answered>(rows.For(Ids("7"), new JObject { ["gateway_id"] = "world" }));

        Assert.Equal("""{"gateway_id":"world","reference_id":"7","value":3}""", Text(answered.Reply));
    }

    [Fact]
    public void OneIdThatFailedIsTheCallsRefusal()
    {
        Rows rows = new();
        rows.Failed("7", new VerbError("not_found", "No thing 7.", new JObject { ["id"] = "7" }));

        VerbOutcome.Refused refused = Assert.IsType<VerbOutcome.Refused>(rows.For(Ids("7")));

        Assert.Equal(("not_found", "No thing 7."), (refused.Error.Code, refused.Error.Message));
        Assert.Equal("7", (string?)refused.Error.Data!["id"]);
    }

    [Fact]
    public void AListIsOnePageWithItsCountsAndTheWhatsOtherKeys()
    {
        Pure.Verbs.Paging paging = new(2, 1, Pure.ListOrder.Nearest);

        JObject reply = Replies.Tree(ListReply.Of("things", new[] { "a", "b", "c", "d" }, paging, entry => new JValue(entry),
            new JObject { ["scanned"] = 40 }));

        Assert.Equal(
            """{"things":["b","c"],"count":2,"total":4,"offset":1,"limit":2,"has_more":true,"scanned":40}""",
            Text(reply));
        Assert.DoesNotContain("limit", Replies.Tree(ListReply.Of("things", new[] { 1 }, Pure.Verbs.Paging.All,
            entry => new JValue(entry))).Properties().Select(key => key.Name));
    }

    [Fact]
    public void AReportListsEveryTargetWithItsMarkAndIsReportOnly()
    {
        List<ReportTarget> targets = new()
        {
            new ReportTarget("4701001", null, "Wall Light 1", new JValue(1), new JValue(0), "[undo]"),
            new ReportTarget(null, JArray.Parse("[1,2,3]"), "Cable", null, new JValue("StructureCableStraight"),
                "[cannot undo]"),
            new ReportTarget("88", null, "Active Vent", new JValue(0), new JValue(1), "[undo]", new[] { "lint" },
                JArray.Parse("""[{"reference_id":"5","kind":"chip"}]"""))
        };

        JObject report = Replies.Tree(Report.Of(targets, new JObject { ["placed"] = new JArray() }));

        Assert.Equal(
            """{"placed":[],"report_only":true,"targets":[""" +
            """{"id":"4701001","name":"Wall Light 1","current":1,"new":0,"undo":"[undo]","text":"[4701001, Wall Light 1, 1 -> 0] [undo]"},""" +
            """{"cell":[1,2,3],"name":"Cable","current":null,"new":"StructureCableStraight","undo":"[cannot undo]","text":"[[1,2,3], Cable, ? -> StructureCableStraight] [cannot undo]"},""" +
            """{"id":"88","name":"Active Vent","current":0,"new":1,"undo":"[undo]","passed":["lint"],"driven_by":[{"reference_id":"5","kind":"chip"}],"text":"[88, Active Vent, 0 -> 1] [undo] PASSED: lint"}]}""",
            Text(report));
    }

    [Fact]
    public void AVeryLargeReportAnswersItsSummaryForm()
    {
        List<ReportTarget> targets = new();
        JArray pieces = new();
        for (int index = 0; index < 200; index++)
        {
            string id = (5000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            targets.Add(new ReportTarget(id, null, "Cable", new JValue("StructureCableStraight"),
                new JValue("StructureCableStraightH"), "[cannot undo: change job]"));
            pieces.Add(new JObject { ["reference_id"] = id, ["prefab_name"] = "StructureCableStraight" });
        }

        targets.Add(new ReportTarget("88", null, "Active Vent", new JValue(0), new JValue(1), "[undo]",
            new[] { "lint" }));
        JObject body = new()
        {
            ["pieces_total"] = 201,
            ["warnings"] = JArray.Parse("""[{"code":"materials_from_player","message":"m"}]"""),
            ["pieces"] = pieces
        };

        JObject report = Replies.Tree(Report.Of(targets, body,
            new[] { new ReportProblem(null, "5000", new VerbError("not_replaceable", "no")) }));

        Assert.Equal(new[] { "pieces_total", "warnings", "problems", "report_only", "targets" },
            report.Properties().Select(key => key.Name).ToArray());
        JArray listed = (JArray)report["targets"]!;
        Assert.Equal(201, listed.Count);
        Assert.Equal("""["5000","Cable"]""", Text(listed[0]));
        Assert.Equal(new JArray("lint"), listed[200]["passed"]);

        JObject small = Replies.Tree(Report.Of(targets.GetRange(0, 3), new JObject { ["pieces"] = new JArray(pieces[0]) }));
        Assert.True(Encoding.UTF8.GetByteCount(Text(small)) <= Report.SummaryBytes);
        Assert.Equal(new[] { "pieces", "report_only", "targets" }, small.Properties().Select(key => key.Name).ToArray());
        Assert.Equal("[5000, Cable, StructureCableStraight -> StructureCableStraightH] [cannot undo: change job]",
            (string)small["targets"]![0]!["text"]!);
    }

    // The hub's script and program faces send max_bytes: code takes the whole report, its lists kept (LIVE 2026-10-25: a
    // script's build pipe report had no cells, only the summary's targets).
    [Fact]
    public void AReportToACallerNamingItsBudgetStaysWhole()
    {
        List<ReportTarget> targets = new();
        JArray cells = new();
        for (int index = 0; index < 200; index++)
        {
            targets.Add(new ReportTarget(null, new JArray(index, 2, 3), "StructurePipeStraight", JValue.CreateNull(),
                new JValue("StructurePipeStraight"), "[undo]"));
            cells.Add(new JObject { ["at"] = new JArray(index, 2, 3), ["rotation_deg"] = new JArray(0, 90, 0) });
        }

        StationGodMCP.Pure.Shaping.ShapeRequest code = new(null, new Dictionary<string, int>(), 16777216);
        object form = Report.Of(targets, new JObject { ["cells"] = cells })
            .Form(StationGodMCP.Api.Shared.ApiJson.Serializer, code);
        JObject whole = JObject.Parse(Text(form));

        Assert.Equal(200, ((JArray)whole["cells"]!).Count);
        Assert.Equal(new JArray(0, 90, 0), whole["cells"]![0]!["rotation_deg"]);
        Assert.IsType<JObject>(whole["targets"]![0]);
        Assert.Null(Replies.Tree(Report.Of(targets, new JObject { ["cells"] = cells }))["cells"]);
    }

    [Fact]
    public void AReportTooLargeEvenAsASummaryIsReportTooLarge()
    {
        List<ReportTarget> targets = new();
        for (int index = 0; index < 5000; index++)
        {
            targets.Add(new ReportTarget((100000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                null, "Insulated Pipe (Straight)", null, null, "[undo]"));
        }

        StationGodMCP.Api.Shared.ApiException refused =
            Assert.Throws<StationGodMCP.Api.Shared.ApiException>(() => Report.Of(targets).Form(StationGodMCP.Api.Shared.ApiJson.Serializer, null));

        Assert.Equal("report_too_large", refused.Code);
        Assert.Equal("""{"targets":5000,"limit_tokens":25000,"fix":"narrow the target: a filter on the find, a smaller ref, fewer spread lines"}""",
            Text((JObject)refused.Details!));
    }

    [Fact]
    public void ABuildPaidByThePlayerCarriesTheWarning()
    {
        Assert.Equal("materials_from_player", (string?)MaterialsSource.FromPlayerNote()["code"]);
    }
}
