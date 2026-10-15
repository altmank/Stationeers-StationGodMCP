#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// undo's replies without the game (T-A5-2, T-A5-7, T-A5-9): one id's plan body (its typed removal call, its typed
/// placement call, its piece runs and its notes, and no status word), an ok row's payload (the plan plus the jobs it
/// started) and the rows shape, the report (targets, the plans, every refused or diverged id a problem), and the two
/// codes a refused id is answered with: undo_refused for a job the world diverged from, queue_full for a start the
/// full job queue refused.
/// </summary>
public sealed class UndoReplyTests
{
    [Fact]
    public void ThePlanBodyIsTheTypedCallsInTheVerbsOwnWords()
    {
        JObject body = Body("run-7");

        Assert.Equal("run-7", (string?)body["id"]);
        Assert.Equal("build cable", (string?)body["tool"]);
        Assert.False(body.ContainsKey("status"));

        JObject remove = (JObject)body["remove"]!;
        Assert.Equal("12", (string?)remove["id"]![0]);
        Assert.Equal("none", (string?)remove["refund_to"]);
        Assert.Equal("4936423", (string?)remove["materials"]);
        Assert.Equal("run-3", (string?)remove["acknowledge_gas_lost"]);

        JObject build = (JObject)body["build"]!;
        JObject placement = (JObject)build["placements"]![0]!;
        Assert.Equal("StructureWallIron", (string?)placement["prefab"]);
        Assert.Equal(1.5, (double)placement["at"]![0]!);
        Assert.Null(build["acknowledge_gas_lost"]);

        JObject piece = (JObject)body["piece_runs"]![0]!;
        Assert.Equal("cable", (string?)piece["what"]);
        Assert.Equal("heavy", (string?)piece["grade"]);
        Assert.Equal("3", (string?)piece["remove_ids"]![0]);
        Assert.Equal("4", (string?)piece["assume_removed"]![0]);
        Assert.Equal("2 piece(s) the job built are removed by the cable run itself (remove_ids).",
            (string?)body["notes"]![0]);
    }

    [Fact]
    public void OneIdActsWithItsJobsAndAListAnswersTheRows()
    {
        UndoResult undone = Done("run-7");

        JObject single = Answered(UndoReplies.Acted(new PerIdList(new[] { "run-7" }, false, null),
            new[] { undone }));
        Assert.Equal("run-7", (string?)single["id"]);
        Assert.Equal("remove-9", (string?)single["jobs"]![0]!["job_id"]!);

        JObject rows = Answered(UndoReplies.Acted(new PerIdList(new[] { "run-7" }, true, null), new[] { undone }));
        Assert.Equal(1, (int)rows["count"]!);
        Assert.Equal(1, (int)rows["success_count"]!);
        Assert.Equal(0, (int)rows["error_count"]!);
        Assert.Equal("run-7", (string?)rows["results"]![0]!["reference_id"]);
        Assert.Equal("remove-9", (string?)rows["results"]![0]!["jobs"]![0]!["job_id"]!);
    }

    [Fact]
    public void ARefusedIdIsThatRowsErrorAndTheRestStillAct()
    {
        JObject rows = Answered(UndoReplies.Acted(new PerIdList(new[] { "remove-9", "run-7" }, true, null),
            new[]
            {
                new UndoResult.Failed(0, "remove-9", new VerbError("job_not_found", "No job with that id is kept.")),
                Done("run-7")
            }));

        Assert.Equal(2, (int)rows["count"]!);
        Assert.Equal(1, (int)rows["success_count"]!);
        Assert.Equal(1, (int)rows["error_count"]!);
        Assert.False((bool)rows["results"]![0]!["ok"]!);
        Assert.Equal("job_not_found", (string?)rows["results"]![0]!["error"]!["code"]);
        Assert.True((bool)rows["results"]![1]!["ok"]!);
    }

    [Fact]
    public void TheReportListsTheTargetsThePlansAndEveryProblemsId()
    {
        JObject report = Replies.Tree(UndoReplies.Reported(new[]
        {
            Done("run-7"),
            new UndoResult.Failed(1, "run-10", UndoReplies.Refused(Diverged()))
        }, asList: true));

        Assert.True((bool)report["report_only"]!);
        Assert.Equal("run-7", (string?)report["plans"]![0]!["id"]);
        Assert.Equal("12", (string?)report["targets"]![0]!["id"]);
        Assert.Equal("removed", (string?)report["targets"]![0]!["new"]);
        Assert.Equal("[cannot undo]", (string?)report["targets"]![0]!["undo"]);
        Assert.Equal(1, (int)report["problems"]![0]!["index"]!);
        Assert.Equal("run-10", (string?)report["problems"]![0]!["reference_id"]);
        Assert.Equal("undo_refused", (string?)report["problems"]![0]!["code"]);
        Assert.Contains("already undone", (string?)report["problems"]![0]!["message"]!);
    }

    [Fact]
    public void OneIdsReportIsThatIdsPlanAtTheTopLevel()
    {
        JObject report = Replies.Tree(UndoReplies.Reported(new[] { Done("run-7") }, asList: false));

        Assert.Equal("run-7", (string?)report["id"]);
        Assert.Equal("build cable", (string?)report["tool"]);
        Assert.True((bool)report["report_only"]!);
    }

    [Fact]
    public void ADivergedIdIsUndoRefusedWithThePlannersLinesJoined()
    {
        VerbError refused = UndoReplies.Refused(Diverged());

        Assert.Equal("undo_refused", refused.Code);
        Assert.Contains("run-10 was already undone", refused.Message);
        Assert.Contains("Nothing is left to undo", refused.Message);
    }

    [Fact]
    public void AStartTheFullQueueRefusedSaysWhatStaysRunning()
    {
        JObject busy = new JObject
        {
            ["tool"] = "remove structure",
            ["status"] = "busy",
            ["waiting"] = 16,
            ["message"] = "16 runs are already queued; nothing was changed."
        };

        VerbError refused = UndoReplies.QueueFull(busy, "run-7", 2);

        Assert.Equal("queue_full", refused.Code);
        Assert.Contains("remove structure did not start", refused.Message);
        Assert.Contains("16 jobs are already waiting", refused.Message);
        Assert.Contains("The 2 job(s) run-7 started before it stay running.", refused.Message);
        Assert.Contains("Nothing of run-7 ran.", UndoReplies.QueueFull(busy, "run-7", 0).Message);
    }

    [Fact]
    public void AReportsFirstProblemIsWhatTheActingCallWouldBeRefusedWith()
    {
        Assert.Null(UndoReplies.Problem(Report.Of(System.Array.Empty<ReportTarget>(), null,
            System.Array.Empty<ReportProblem>())));

        ReportReply refused = Report.Of(System.Array.Empty<ReportTarget>(), null, new[]
        {
            new ReportProblem(0, null, new VerbError("gas_hold", "The pipe job would lose gas; acknowledge it.")),
            new ReportProblem(1, null, new VerbError("would_burst", "a tank bursts"))
        });

        VerbError problem = UndoReplies.Problem(refused)!;
        Assert.Equal("gas_hold", problem.Code);
        Assert.Equal("The pipe job would lose gas; acknowledge it.", problem.Message);
    }

    // One id's plan body, as the handler builds it: the removal of what the job built, the placements it puts back, its
    // piece run and its notes.
    private static JObject Body(string id) =>
        UndoReplies.Plan(id, "build cable",
            UndoReplies.RemoveCall(new long[] { 12 }, RefundRoute.Nothing, 4936423, "run-3"),
            UndoReplies.BuildCall(UndoReplies.Placements(new List<ThingSnapshot> { Wall() }), 4936423, null),
            new[] { new PieceRun(Group(12), new List<long> { 3 }, new List<string> { "4" }) },
            new List<string> { "2 piece(s) the job built are removed by the cable run itself (remove_ids)." });

    private static UndoResult Done(string id) =>
        new UndoResult.Undone(id, Body(id), Targets(),
            new List<object> { new JObject { ["job_id"] = "remove-9", ["status"] = "queued" } });

    private static JObject Answered(VerbOutcome outcome) => Replies.Tree(((VerbOutcome.Answered)outcome).Reply);

    private static List<ReportTarget> Targets()
    {
        List<ReportTarget> targets = new List<ReportTarget>(1);
        targets.Add(new ReportTarget("12", null, "StructureCableStraight", new JValue("StructureCableStraight"),
            new JValue("removed"), "[cannot undo]"));
        return targets;
    }

    private static UndoPlan Diverged() => new UndoPlan(new List<long>(), new List<ThingSnapshot>(),
        new List<string>
        {
            "run-10 was already undone: undo started run-11 (applied).",
            "Nothing is left to undo; undo those jobs, the last first, to have it back."
        }, new List<string>());

    private static PieceRestore Group(long id)
    {
        List<PieceCell> cells = new List<PieceCell> { new PieceCell(new GridCell(0, 0, 0), default) };
        NetworkPiece piece = new NetworkPiece("cable", "heavy", cells);
        return new PieceRestore("cable", "heavy", new List<ThingSnapshot>
        {
            new ThingSnapshot(id, "StructureCableStraight", new Vec3(7, 1, 2), CubeRotation.Identity, 0, null, piece)
        });
    }

    private static ThingSnapshot Wall() =>
        new ThingSnapshot(21, "StructureWallIron", new Vec3(1.5, 2, 3), CubeRotation.Identity, 0, null);
}
