#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>remove cable (catalogue remove.json): cable pieces as wire cutters remove them.</summary>
internal static class RemoveCable
{
    internal static VerbOutcome Run(RemoveCableOptions options) =>
        RemoveRuns.Run(new CableRunKind(), RemoveCableOptions.Spec, new RemoveRunCall(options.Selector,
            options.Waypoints, options.Cells, options.Allow, options.Root, options.Materials, options.Fields,
            options.Report, options.Queue ?? true, null));
}

/// <summary>remove pipe (catalogue remove.json): pipe pieces as a player removes them.</summary>
internal static class RemovePipe
{
    internal static VerbOutcome Run(RemovePipeOptions options) =>
        RemoveRuns.Run(new PipeRunKind(), RemovePipeOptions.Spec, new RemoveRunCall(options.Selector,
            options.Waypoints, options.Cells, options.Allow, options.Root, options.Materials, options.Fields,
            options.Report, options.Queue ?? true, options.AcknowledgeGasLost));
}

/// <summary>remove chute (catalogue remove.json): chute pieces as a player removes them.</summary>
internal static class RemoveChute
{
    internal static VerbOutcome Run(RemoveChuteOptions options) =>
        RemoveRuns.Run(new ChuteRunKind(), RemoveChuteOptions.Spec, new RemoveRunCall(options.Selector,
            options.Waypoints, options.Cells, options.Allow, options.Root, options.Materials, options.Fields,
            options.Report, options.Queue ?? true, null));
}

/// <summary>What a remove cable, pipe or chute call gives, in verb names.</summary>
internal sealed class RemoveRunCall
{
    internal RemoveRunCall(Selector selector, JToken? waypoints, JToken? cells, IReadOnlyList<string>? allow,
        string? root, MaterialsSource materials, Fields fields, ReportConfirm report, bool queue,
        string? acknowledgeGasLost)
    {
        Selector = selector;
        Waypoints = waypoints;
        Cells = cells;
        Allow = allow;
        Root = root;
        Materials = materials;
        Fields = fields;
        Report = report;
        Queue = queue;
        AcknowledgeGasLost = acknowledgeGasLost;
    }

    public Selector Selector { get; }

    public JToken? Waypoints { get; }

    public JToken? Cells { get; }

    public IReadOnlyList<string>? Allow { get; }

    public string? Root { get; }

    public MaterialsSource Materials { get; }

    public Fields Fields { get; }

    public ReportConfirm Report { get; }

    public bool Queue { get; }

    public string? AcknowledgeGasLost { get; }
}

/// <summary>
/// remove cable, pipe and chute over RunPlanner and RunJobs: the pieces by id (one job for a list), or the piece in
/// every cell along waypoints or in each of cells. Without confirm, a Report.Of report: every piece the job takes down
/// a target [id, name, prefab -> "removed"] marked [undo] ([cannot undo: burnt] for a burnt cable, which undo leaves
/// gone), every piece it changes [id, name, prefab -> new prefab] [undo], the run's planned view the body with every
/// cell listed, and every problem, a gas hold the confirmed call would be refused on among them. Confirmed with a
/// problem, the report answers and nothing starts; confirmed and ready, one job starts and its undo is recorded
/// (JobSnapshots). rows is how many cells the job's result lists.
/// </summary>
internal static class RemoveRuns
{
    private const string Removed = "removed";
    internal const string BurntMark = "[cannot undo: burnt]";

    internal static VerbOutcome Run(RunKind kind, WhatSpec spec, RemoveRunCall call)
    {
        string label = spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        string? form = RemoveRunOrders.Form(call.Selector.Ids != null, call.Waypoints, call.Cells, label, kind.Noun,
            problems, out VerbError? missing);
        GasHoldArgs.Check(call.AcknowledgeGasLost, problems);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        if (missing != null)
        {
            return new VerbOutcome.Refused(missing);
        }

        List<ThingId?> named = new List<ThingId?>();
        List<ReportProblem> unread = new List<ReportProblem>();
        RunRemoval removal;
        if (form == RemoveRunOrders.ById)
        {
            named.AddRange(JobTargets.Ids(call.Selector.Ids!.Ids, label, unread));
            removal = new RunRemoval(JobTargets.Read(named), new List<GridCell>());
        }
        else
        {
            List<GridCell>? cells = CellsOf(form!, form == RemoveRunOrders.ByWaypoints ? call.Waypoints! : call.Cells!,
                out string? error);
            if (cells == null)
            {
                return new VerbOutcome.Refused(OptionProblem.Refusal(label,
                    new[] { new OptionProblem(form!, error ?? "is not a run") }));
            }

            removal = new RunRemoval(new List<ThingId>(), cells);
        }

        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(call.Materials, label, more);
        ThingId? root = call.Root != null ? TargetIds.Of(call.Root, label) : null;
        RunOptions options = new RunOptions(new EditAllowance(new HashSet<long>(), Allows(call.Allow, "split")), from,
            call.Materials.RefundTo, call.Report.Rows ?? RunArgs.DefaultListLimit, true,
            new RunTargets(root, null, false), false,
            new RunDetail(call.Fields.Includes("links"), call.Fields.Includes("notes"),
                call.Fields.Includes("network_devices")), more);
        RunRequest request = new RunRequest(kind, kind.RemoveTool, null, removal, options);
        string mark = spec.Write!.UndoMark;
        bool pipes = kind.Family is PipeFamily;
        JobSource source = new JobSource(from?.Value, false, call.Materials.RefundTo, null);
        return WriteCheck<RunPlan>.Planned(answer => RunPlanner.Planning(request, true, answer), call.Report,
            plan => Act(plan, request, call, named, unread, mark, pipes, source),
            plan => Described(plan, named, unread, mark, pipes, call.AcknowledgeGasLost));
    }

    /// <summary>The cells along waypoints, or the cells listed; null with the reason when they make no run.</summary>
    internal static List<GridCell>? CellsOf(string option, JToken given, out string? error)
    {
        List<GridCell> points = new List<GridCell>();
        int index = 0;
        foreach (JToken point in given as JArray ?? new JArray())
        {
            points.Add(RunArgs.CellOf(RunArgs.PositionOf(point, $"{option}[{index++}]")));
        }

        return option == RemoveRunOrders.ByWaypoints
            ? RunPath.FromWaypoints(points, out error)
            : RunPath.FromCells(points, out error);
    }

    private static bool Allows(IReadOnlyList<string>? allow, string guard)
    {
        foreach (string word in allow ?? System.Array.Empty<string>())
        {
            if (word == guard)
            {
                return true;
            }
        }

        return false;
    }

    private static VerbOutcome Act(RunPlan plan, RunRequest request, RemoveRunCall call, List<ThingId?> named,
        List<ReportProblem> unread, string mark, bool pipes, JobSource source)
    {
        if (unread.Count > 0 || !plan.Ready ||
            GasHold.Judge(pipes, call.AcknowledgeGasLost) is GasHoldVerdict.Refusing)
        {
            return new VerbOutcome.Answered(Described(plan, named, unread, mark, pipes, call.AcknowledgeGasLost));
        }

        RunReportView preflight = RunReports.Of(plan, RunReports.Scheduled, null);
        object started = JobSnapshots.Record(RunJobs.Start(request, preflight, call.Queue, call.AcknowledgeGasLost),
            request.Tool, RunTakedowns.Removed(plan), source, RunTakedowns.Burnt(plan));
        object reply = call.Fields.Verbose
            ? started
            : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Serializer));
    }

    private static ReportReply Described(RunPlan plan, List<ThingId?> named, List<ReportProblem> unread, string mark,
        bool pipes, string? acknowledge)
    {
        List<ReportProblem> problems = new List<ReportProblem>(unread);
        foreach (LayoutIssue issue in plan.Problems)
        {
            problems.Add(JobReports.Problem(issue.Code, issue.Message,
                issue.Id.HasValue ? new ThingId(issue.Id.Value) : null, named));
        }

        GasHold.Preview(pipes, acknowledge);
        if (GasHold.Judge(pipes, acknowledge) is GasHoldVerdict.Refusing refusing)
        {
            problems.Add(new ReportProblem(null, null, VerbError.Of(GasHold.Refusal(refusing))));
        }

        List<ReportTarget> targets = new List<ReportTarget>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (!removal.Assumed)
            {
                targets.Add(Target(removal.Piece, Removed, removal.Debris ? BurntMark : mark));
            }
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            if (cell.Existing is { } existing)
            {
                targets.Add(Target(existing, cell.Choice.Prefab.PrefabName, mark));
            }
        }

        RunReportView view = RunReports.Of(plan, RunReports.DryRun, null, RunPath.MaximumCells);
        return Report.Of(targets, JobReports.Body(view, problems.Count == 0), problems);
    }

    private static ReportTarget Target(SmallGrid piece, string becomes, string mark) =>
        new ReportTarget(new ThingId(piece.ReferenceId).ToString(), null, Names.Of(piece),
            new JValue(piece.PrefabName), new JValue(becomes), mark);
}
