#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>swap wall (catalogue swap.json): walls and windows replaced in place, without opening a face.</summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class SwapWall
{
    internal static VerbOutcome Run(SwapWallOptions options) =>
        SwapStructures.Run(new WallFamily(), SwapWallOptions.Spec, new SwapStructureCall(options.Selector,
            options.Into, options.Materials, options.SkipUnmatched ?? false, options.Fields, options.Report,
            options.Queue ?? true));
}

/// <summary>swap frame (catalogue swap.json): frames replaced in place, or finished, without opening a cell.</summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class SwapFrame
{
    internal static VerbOutcome Run(SwapFrameOptions options) =>
        SwapStructures.Run(new FrameFamily(), SwapFrameOptions.Spec, new SwapStructureCall(options.Selector,
            options.Into, options.Materials, options.SkipUnmatched ?? false, options.Fields, options.Report,
            options.Queue ?? true));
}

/// <summary>What a swap wall or swap frame call gives, in verb names.</summary>
internal sealed class SwapStructureCall
{
    internal SwapStructureCall(Selector selector, string? into, MaterialsSource materials, bool skipUnmatched,
        Fields fields, ReportConfirm report, bool queue)
    {
        Selector = selector;
        Into = into;
        Materials = materials;
        SkipUnmatched = skipUnmatched;
        Fields = fields;
        Report = report;
        Queue = queue;
    }

    public Selector Selector { get; }

    public string? Into { get; }

    public MaterialsSource Materials { get; }

    public bool SkipUnmatched { get; }

    public Fields Fields { get; }

    public ReportConfirm Report { get; }

    public bool Queue { get; }
}

/// <summary>
/// swap wall and swap frame over StructureSwapPlanner and StructureJobs: the pieces by id (one job for a list) or by
/// room. Without confirm, a Report.Of report: every piece to swap a target [id, name, prefab -> new prefab] with the
/// what's undo mark, the planned view the body, every problem listed. Confirmed and ready, one job starts and the reply
/// is its start, in brief unless verbose; confirmed with a problem, the report answers and nothing starts. rows is how
/// many pieces each list of the job's result shows; a report lists every piece.
/// </summary>
internal static class SwapStructures
{
    internal static VerbOutcome Run(StructureFamily family, WhatSpec spec, SwapStructureCall call)
    {
        string label = spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        bool byIds = StructureScopes.ByIds(call.Selector, label, family.Noun, problems, out StructureScope.Room? room,
            out VerbError? missing);
        StructureScopes.Into(call.Into, problems);

        if (missing != null)
        {
            return new VerbOutcome.Refused(missing);
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        List<ReportProblem> unread = new List<ReportProblem>();
        List<ThingId?> named = byIds
            ? JobTargets.Ids(call.Selector.Ids!.Ids, label, unread)
            : new List<ThingId?>();
        StructureScope scope = byIds ? new StructureScope.Pieces(JobTargets.Read(named)) : room!;
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(call.Materials, label, more);
        StructureSwapRequest request = new StructureSwapRequest(family, new StructureSwapArguments(
            call.Into?.Trim(), scope, call.Selector.Prefab, from,
            new StructureSwapOptions(call.Materials.RefundTo, call.SkipUnmatched,
                call.Report.Rows ?? StructureSwapArguments.DefaultRows, call.Fields.Includes("notes")), more));
        string mark = spec.Write!.UndoMark;
        return WriteCheck<StructureSwapPlan>.Planned(answer => StructureSwapPlanner.Planning(request, answer),
            call.Report, plan => Act(plan, request, unread, named, mark, call),
            plan => WorkResult<ReportReply>.Of(answer => Describing(plan, unread, named, mark, answer),
                static report => report));
    }

    private static VerbOutcome Act(StructureSwapPlan plan, StructureSwapRequest request, List<ReportProblem> unread,
        List<ThingId?> named, string mark, SwapStructureCall call)
    {
        if (unread.Count > 0 || !plan.Ready)
        {
            return new VerbOutcome.Answered(Described(plan, unread, named, mark));
        }

        object started = StructureJobs.Start(request, plan, call.Queue);
        object reply = JobBrief.Started(started,
            JobPreflightSummaryView.Of(StructureReports.Of(plan, StructureReports.DryRun, null)), call.Fields.Verbose);
        return new VerbOutcome.Answered(reply);
    }

    private static ReportReply Described(StructureSwapPlan plan, List<ReportProblem> unread, List<ThingId?> named,
        string mark)
    {
        Pure.Scheduling.Answer<ReportReply> report = new Pure.Scheduling.Answer<ReportReply>();
        foreach (object? _ in Describing(plan, unread, named, mark, report))
        {
        }

        return report.Value;
    }

    // The report as work over frames (A3): its targets a batch a step, then the plan's report view (StructureReports).
    [StationGodMCP.Pure.Profiling.Profiled]
    private static IEnumerable<object?> Describing(StructureSwapPlan plan, List<ReportProblem> unread,
        List<ThingId?> named, string mark, Pure.Scheduling.Answer<ReportReply> answer)
    {
        List<ReportProblem> problems = new List<ReportProblem>(unread);
        problems.AddRange(JobReports.Problems(plan.Problems, named));
        List<ReportTarget> targets = new List<ReportTarget>(plan.Swaps.Count);
        for (int at = 0; at < plan.Swaps.Count; at++)
        {
            PlannedStructureSwap swap = plan.Swaps[at];
            targets.Add(new ReportTarget(new ThingId(swap.Old.ReferenceId).ToString(), null, Names.Of(swap.Old),
                swap.Old.PrefabName, swap.Target.PrefabName, mark));
            if (at % StructureReports.PiecesAStep == StructureReports.PiecesAStep - 1)
            {
                yield return null;
            }
        }

        Pure.Scheduling.Answer<StructureSwapReportView> view = new Pure.Scheduling.Answer<StructureSwapReportView>();
        foreach (object? step in StructureReports.Building(plan, StructureReports.DryRun, null,
                     StructureSwapArguments.MaximumPieces, view))
        {
            yield return step;
        }

        answer.Value = Report.Of(targets, JobReports.Body(view.Value, problems.Count == 0), problems);
    }
}
