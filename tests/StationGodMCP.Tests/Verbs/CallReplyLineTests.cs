#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// A verb call's reply is handed over as plain data and written by the connection's writer (cost design A5): the game
/// thread writes nothing; the writer applies the shape, stops keeping text at the size cap, chooses a report's form and
/// times the call.
/// </summary>
public class CallReplyLineTests
{
    private readonly List<(string What, bool Ok, double WriteMs)> _timed = new List<(string, bool, double)>();

    private VerbRunner Runner(int maxReplyBytes) =>
        new VerbRunner(TestVerbs.Every, maxReplyBytes, () => 42, (what, ok, _, writeMs, _) => _timed.Add((what, ok, writeMs)));

    private static CallRequest Request(string? shape = null) =>
        new CallRequest("c1", "find", null, shape == null ? null : ShapeRequest.Lenient(JObject.Parse(shape)), true);

    private string Written(int maxReplyBytes, VerbOutcome outcome, string? shape = null)
    {
        ReplyLine line = VerbCalls.Write(Runner(maxReplyBytes), Request(shape), "find.thing", outcome, 1.234, 0.5);
        Assert.Null(line.Text);
        return line.Render();
    }

    private static ReplyObject Things(int count)
    {
        List<ReplyObject> things = new List<ReplyObject>();
        for (int index = 0; index < count; index++)
        {
            things.Add(new ReplyObject().Add("name", "thing " + index));
        }

        return new ReplyObject().Add("things", things).Add("count", count);
    }

    [Fact]
    public void AnAnswerIsWrittenOnceByTheWriterAndTimedThere()
    {
        JObject reply = JObject.Parse(Written(1 << 20, new VerbOutcome.Answered(Things(2))));

        Assert.True((bool)reply["ok"]!);
        Assert.Equal(2, (int)reply["result"]!["count"]!);
        Assert.Equal(1.23, (double)reply["elapsed_ms"]!);
        Assert.Equal(42, (long)reply["frame"]!);
        Assert.Equal(("find.thing", true), (_timed[0].What, _timed[0].Ok));
    }

    [Fact]
    public void AReplyPastTheCapIsReplyTooLargeWithItsWholeSizeAndListLengths()
    {
        ReplyObject things = Things(200);
        int whole = Encoding.UTF8.GetByteCount(ApiJson.WriteReply(ApiJson.Serializer,
            Api.Views.CallReplyView.Of("c1", things, false, 1.23, 0.5, 42, false), ShapeRequest.None,
            new List<Truncation>(), long.MaxValue).Json!);

        JObject reply = JObject.Parse(Written(1024, new VerbOutcome.Answered(things)));

        Assert.Equal("reply_too_large", (string?)reply["error"]!["code"]);
        Assert.Equal(whole, (int)reply["error"]!["data"]!["bytes"]!);
        Assert.Equal(200, (int)reply["error"]!["data"]!["counts"]!["things"]!);
        Assert.False(_timed[0].Ok);
    }

    [Fact]
    public void TheCappedWriterKeepsNoTextOnceOverAndCountsOn()
    {
        CappedText text = new CappedText(4);
        text.Write("ab");
        Assert.False(text.Over);
        text.Write("cdé");

        Assert.True(text.Over);
        Assert.Null(text.Json);
        Assert.Equal(6, text.Bytes);
    }

    [Fact]
    public void AReportTooLargeEvenInSummaryIsRefusedByTheWriter()
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        for (int index = 0; index < 5000; index++)
        {
            targets.Add(new ReportTarget(index.ToString(), null, "Cable", 1, 2, "[undo]", new[] { "lint" }));
        }

        JObject reply = JObject.Parse(Written(1 << 24, new VerbOutcome.Answered(Report.Of(targets))));

        Assert.Equal("report_too_large", (string?)reply["error"]!["code"]);
        Assert.Equal(5000, (int)reply["error"]!["data"]!["targets"]!);
    }

    [Fact]
    public void ARefusalIsWrittenByTheWriterToo()
    {
        JObject reply = JObject.Parse(Written(1 << 20,
            new VerbOutcome.Refused(new VerbError("thing_not_found", "No thing 7."))));

        Assert.Equal("thing_not_found", (string?)reply["error"]!["code"]);
        Assert.False(_timed[0].Ok);
    }

    [Fact]
    public void FieldsShapeTheReplyAsItIsWritten()
    {
        JObject reply = JObject.Parse(Written(1 << 20, new VerbOutcome.Answered(Things(3)),
            """{"fields":["count"]}"""));

        Assert.Equal(3, (int)reply["result"]!["count"]!);
        Assert.Null(reply["result"]!["things"]);
    }
}
