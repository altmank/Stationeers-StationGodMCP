#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>build cable (catalogue build.json): a run of cable pieces, laid as a player lays them.</summary>
internal static class BuildCable
{
    internal static VerbOutcome Run(BuildCableOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        RunBuildOrder? order = RunBuildOrder.Of(options, problems, out VerbError? missing);
        return order != null
            ? BuildRuns.Run(new CableRunKind(), order)
            : new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(BuildCableOptions.Spec.Label, problems));
    }
}

/// <summary>build pipe (catalogue build.json): a run of pipes, whose contents a gas hold is judged for.</summary>
internal static class BuildPipe
{
    internal static VerbOutcome Run(BuildPipeOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        RunBuildOrder? order = RunBuildOrder.Of(options, problems, out VerbError? missing);
        return order != null
            ? BuildRuns.Run(new PipeRunKind(), order)
            : new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(BuildPipeOptions.Spec.Label, problems));
    }
}

/// <summary>build chute (catalogue build.json): a run of chute pieces, checked for the flow of items along it.</summary>
internal static class BuildChute
{
    internal static VerbOutcome Run(BuildChuteOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        RunBuildOrder? order = RunBuildOrder.Of(options, problems, out VerbError? missing);
        return order != null
            ? BuildRuns.Run(new ChuteRunKind(), order)
            : new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(BuildChuteOptions.Spec.Label, problems));
    }
}

/// <summary>
/// build cable, pipe and chute over RunPlanner and RunJobs: a run laid along waypoints or cells, one piece with its
/// ends, or pieces each with their own, joining what is there; the pieces remove_ids names are taken down in the same
/// job, and everything assume_removed names is checked as if it were gone. Without confirm, a Report.Of report: every
/// placed or changed cell and every piece the job takes down a target (an id, or the cell for a new piece) [name,
/// current -> new] with the what's undo mark ([cannot undo: burnt] for a burnt cable, which undo leaves gone; PASSED
/// for a row a lint problem was let through on), the run's planned view the body, every problem (the index of the id
/// it is about, remove_ids then assume_removed), and a gas hold the confirmed pipe run would be refused on among them.
/// Confirmed with a problem, the report answers and nothing starts; confirmed and ready, one job starts and its undo
/// is recorded (JobSnapshots). rows is how many cells the job's result lists.
/// </summary>
internal static class BuildRuns
{
    private const string Removed = "removed";

    private const int MaximumPoints = 1024;
    private const int MaximumPieces = 256;
    private const int MaximumBranches = 16;
    private const int MaximumExtraEnds = 64;
    private const int MaximumPieceEnds = 6;

    /// <summary>The guards a lint problem was let through on, for the rows about it.</summary>
    private static readonly string[] LintLifted = { WriteLint.Guard };

    internal static VerbOutcome Run(RunKind kind, RunBuildOrder order)
    {
        string label = order.Label;
        Grade grade = GradeOf(kind, order, label);
        List<ExtraEnd> extra = new List<ExtraEnd>();
        RunShape? shape = Shape(order, extra, out OptionProblem? problem);
        if (shape == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, new[] { problem! }));
        }

        List<ThingId?> removed = new List<ThingId?>();
        List<ReportProblem> unread = new List<ReportProblem>();
        List<ThingId?> ids = JobTargets.Ids(order.RemoveIds, label, unread);
        List<ThingId?> assumed = JobTargets.Ids(order.AssumeRemoved, label, unread, ids.Count);
        removed.AddRange(ids);
        removed.AddRange(assumed);
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(order.Materials, label, more);
        HashSet<long> bridge = NetworkHandles.ResolveAllowances(order.AllowBridge, "allow_bridge", kind.Family,
            kind.Bridges);
        ThingId? joinTo = order.JoinTo != null ? NetworkHandles.Resolve(order.JoinTo, "join_to", kind.Family) : null;
        if (joinTo.HasValue)
        {
            // A handle naming nothing is taken as a network id: it must be one (network_not_found otherwise).
            kind.Family.NetworkMembers(joinTo.Value);
        }

        Fields fields = order.Fields;
        RunOptions options = new RunOptions(new EditAllowance(bridge, order.AllowSplit), from, order.Materials.RefundTo,
            order.Report.Rows ?? RunArgs.DefaultListLimit, order.SplitLong,
            new RunTargets(order.Root, joinTo, order.JoinTrunk), order.AllowDoorKeepOut,
            new RunDetail(fields.Includes("links"), fields.Includes("notes"), fields.Includes("network_devices")),
            more, order.AllowLint);
        RunRemoval removal = new RunRemoval(JobTargets.Read(ids), new List<GridCell>(), JobTargets.Read(assumed));
        RunRequest request = new RunRequest(kind, kind.PlaceTool,
            new RunBuild(shape, grade, JoinOf(order), extra), removal, options);
        JobSource source = new JobSource(from?.Value, false, order.Source.RefundTo, order.Source.Refund);
        return WriteCheck<RunPlan>.Run(() => RunPlanner.Plan(request))
            .Outcome(order.Report, plan => Act(plan, request, order, removed, unread, source),
                plan => Described(plan, request, order, removed, unread));
    }

    /// <summary>
    /// One piece run's reply, for a caller that runs the call itself (undo_job's replay): the report until it acts, or
    /// the started job. A refusal the call cannot be read with is thrown, as any call's is.
    /// </summary>
    internal static object Reply(RunKind kind, RunBuildOrder order) => Run(kind, order) switch
    {
        VerbOutcome.Answered answered => answered.Reply,
        VerbOutcome.Refused refused => throw ApiErrors.Refused(refused.Error.Code, refused.Error.Message),
        _ => throw new InvalidOperationException("A build call answers in the call that reads it.")
    };

    // The grade word as the family's grade; a what that must name one is refused before this (RunBuildOrder).
    private static Grade GradeOf(RunKind kind, RunBuildOrder order, string label)
    {
        string given = order.Grade ?? kind.DefaultGrade ?? throw ApiErrors.InvalidArgument(
            $"{label}: grade is required: one of {string.Join(", ", kind.GradeNames)}.");
        return kind.GradeOf(given) ?? throw ApiErrors.InvalidArgument(
            $"{label}: grade must be one of {string.Join(", ", kind.GradeNames)}, not {given}.");
    }

    // What the run joins: a piece or pieces run joins nothing (its ends are its own); the rest says join.
    private static JoinMode JoinOf(RunBuildOrder order) =>
        order.Form == RunBuildOrder.ByPiece || order.Form == RunBuildOrder.ByPieces
            ? JoinMode.None
            : JoinWord(order.Join);

    private static JoinMode JoinWord(string join) => join switch
    {
        "none" => JoinMode.None,
        "all" => JoinMode.All,
        _ => JoinMode.Ends
    };

    // The run the call gives: its extra ends, its cells from waypoints, cells, piece or pieces, its branches and the
    // shape they make; null with the problem when the call names no run.
    private static RunShape? Shape(RunBuildOrder order, List<ExtraEnd> extra, out OptionProblem? problem)
    {
        ExtraEnds(order.ExtraEnds, extra, out problem);
        if (problem != null)
        {
            return null;
        }

        List<GridCell>? run = CellsOf(order, extra, out problem);
        if (run == null)
        {
            return null;
        }

        if (order.Form == RunBuildOrder.ByPieces)
        {
            if (order.Branches != null)
            {
                problem = new OptionProblem("branches", "go with waypoints or cells, not pieces");
                return null;
            }

            RunShape? pieces = RunShape.Pieces(run, out string? piecesError);
            if (pieces == null)
            {
                problem = new OptionProblem(order.Form, piecesError ?? "is not a run");
            }

            return pieces;
        }

        if (Branches(order, out problem) is not { } branches)
        {
            return null;
        }

        RunShape? shape = RunShape.Of(run, branches, out string? shapeError);
        if (shape == null)
        {
            problem = new OptionProblem(order.Form, shapeError ?? "is not a run");
        }

        return shape;
    }

    // The run's cells from waypoints, cells, piece or pieces; null with the problem when they make no run.
    private static List<GridCell>? CellsOf(RunBuildOrder order, List<ExtraEnd> extra, out OptionProblem? problem)
    {
        problem = null;
        if (order.Form == RunBuildOrder.ByWaypoints)
        {
            List<GridCell>? path = RunPath.FromWaypoints(Points(order.Waypoints, RunBuildOrder.ByWaypoints),
                out string? wayError);
            return path ?? NoRun(RunBuildOrder.ByWaypoints, wayError, out problem);
        }

        if (order.Form == RunBuildOrder.ByCells)
        {
            List<GridCell>? listed = RunPath.FromCells(Points(order.Cells, RunBuildOrder.ByCells), out string? error);
            return listed ?? NoRun(RunBuildOrder.ByCells, error, out problem);
        }

        if (order.Form == RunBuildOrder.ByPiece)
        {
            return order.Piece is JObject piece
                ? Piece(piece, RunBuildOrder.ByPiece, extra, out problem)
                : NoRun(RunBuildOrder.ByPiece, "must be {at, ends}", out problem);
        }

        return Pieces(order.Pieces, extra, out problem);
    }

    private static List<GridCell>? NoRun(string option, string? why, out OptionProblem? problem)
    {
        problem = new OptionProblem(option, why ?? "is not a run");
        return null;
    }

    // The branch runs: {waypoints} or {cells} each, and attach, the cell of the run or an earlier branch its last
    // cell joins (by default the first found next to it). null with the problem.
    private static List<RunBranch>? Branches(RunBuildOrder order, out OptionProblem? problem)
    {
        problem = null;
        List<RunBranch> branches = new List<RunBranch>();
        if (order.Branches == null)
        {
            return branches;
        }

        if (order.Form == RunBuildOrder.ByPiece)
        {
            problem = new OptionProblem("branches", "go with waypoints or cells, not piece");
            return null;
        }

        if (!(order.Branches is JArray items) || items.Count == 0 || items.Count > MaximumBranches)
        {
            problem = new OptionProblem("branches",
                $"is a list of 1 to {MaximumBranches} of {{waypoints}} or {{cells}}");
            return null;
        }

        for (int index = 0; index < items.Count; index++)
        {
            string path = $"branches[{index}]";
            if (!(items[index] is JObject item))
            {
                problem = new OptionProblem(path, "must be {waypoints} or {cells}");
                return null;
            }

            bool byWaypoints = item["waypoints"] != null;
            if (byWaypoints == (item["cells"] != null))
            {
                problem = new OptionProblem(path, "needs waypoints or cells (one of them)");
                return null;
            }

            bool byCells = !byWaypoints;
            List<GridCell>? cells = byWaypoints
                ? RunPath.FromWaypoints(Points(item["waypoints"], "waypoints"), out string? branchError)
                : RunPath.FromCells(Points(item["cells"], "cells"), out branchError);
            if (cells == null)
            {
                problem = new OptionProblem(path, branchError ?? "is not a run");
                return null;
            }

            JToken? attach = item["attach"];
            branches.Add(new RunBranch(cells,
                attach != null ? RunArgs.CellOf(RunArgs.PositionOf(attach, $"{path}.attach")) : (GridCell?)null));
        }

        return branches;
    }

    // extra_ends: {at, toward} each, an end the piece at that cell has towards that step.
    private static void ExtraEnds(JToken? given, List<ExtraEnd> extra, out OptionProblem? problem)
    {
        problem = null;
        if (given == null)
        {
            return;
        }

        if (!(given is JArray items) || items.Count == 0 || items.Count > MaximumExtraEnds)
        {
            problem = new OptionProblem("extra_ends", $"is a list of 1 to {MaximumExtraEnds} of {{at, toward}}");
            return;
        }

        for (int index = 0; index < items.Count; index++)
        {
            string path = $"extra_ends[{index}]";
            JToken? at = (items[index] as JObject)?["at"];
            JToken? toward = (items[index] as JObject)?["toward"];
            string? text = toward != null && toward.Type == JTokenType.String ? (string?)toward : null;
            if (at == null || !GridStep.TryParse(text, out GridStep step))
            {
                problem = new OptionProblem(path,
                    "must be {at: [x, y, z], toward: \"+x\"|\"-x\"|\"+y\"|\"-y\"|\"+z\"|\"-z\"}");
                return;
            }

            extra.Add(new ExtraEnd(RunArgs.CellOf(RunArgs.PositionOf(at, $"{path}.at")), step));
        }
    }

    // pieces: [{at, ends}], each one piece as the piece form lays it.
    private static List<GridCell>? Pieces(JToken? given, List<ExtraEnd> extra, out OptionProblem? problem)
    {
        problem = null;
        if (!(given is JArray items) || items.Count == 0 || items.Count > MaximumPieces)
        {
            problem = new OptionProblem(RunBuildOrder.ByPieces,
                $"is a list of 1 to {MaximumPieces} pieces, each {{at, ends}}");
            return null;
        }

        List<GridCell> cells = new List<GridCell>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            if (!(items[index] is JObject item))
            {
                problem = new OptionProblem($"pieces[{index}]", "must be {at, ends}");
                return null;
            }

            List<GridCell>? one = Piece(item, $"pieces[{index}]", extra, out problem);
            if (one == null)
            {
                return null;
            }

            cells.AddRange(one);
        }

        return cells;
    }

    // One piece: the cell at names, and the ends it has there, each an extra end of that cell.
    private static List<GridCell>? Piece(JObject piece, string name, List<ExtraEnd> extra, out OptionProblem? problem)
    {
        GridCell cell = RunArgs.CellOf(RunArgs.PositionOf(piece["at"] ?? JValue.CreateNull(), $"{name}.at"));
        if (!(piece["ends"] is JArray ends) || ends.Count == 0 || ends.Count > MaximumPieceEnds)
        {
            problem = new OptionProblem($"{name}.ends",
                $"is a list of 1 to {MaximumPieceEnds} ends: +x, -x, +y, -y, +z, -z");
            return null;
        }

        EndSet seen = EndSet.None;
        for (int index = 0; index < ends.Count; index++)
        {
            string? text = ends[index].Type == JTokenType.String ? (string?)ends[index] : null;
            if (!GridStep.TryParse(text, out GridStep step))
            {
                problem = new OptionProblem($"{name}.ends[{index}]", "must be one of +x, -x, +y, -y, +z, -z");
                return null;
            }

            if (seen.Contains(step))
            {
                problem = new OptionProblem($"{name}.ends[{index}]", $"repeats {step.Name}; name each end once");
                return null;
            }

            seen = seen.With(step);
            extra.Add(new ExtraEnd(cell, step));
        }

        problem = null;
        return new List<GridCell> { cell };
    }

    // The cells the option lists: each a position [x, y, z] in metres, snapped to the cell it falls in.
    private static List<GridCell> Points(JToken? given, string option)
    {
        if (!(given is JArray points) || points.Count == 0 || points.Count > MaximumPoints)
        {
            throw ApiErrors.InvalidArgument(
                $"{option} must be a list of 1 to {MaximumPoints} positions [x, y, z] in metres.");
        }

        List<GridCell> cells = new List<GridCell>(points.Count);
        for (int index = 0; index < points.Count; index++)
        {
            cells.Add(RunArgs.CellOf(RunArgs.PositionOf(points[index], $"{option}[{index}]")));
        }

        return cells;
    }

    private static VerbOutcome Act(RunPlan plan, RunRequest request, RunBuildOrder order, List<ThingId?> named,
        List<ReportProblem> unread, JobSource source)
    {
        if (unread.Count > 0 || !plan.Ready || Refusing(request, order))
        {
            return new VerbOutcome.Answered(Described(plan, request, order, named, unread));
        }

        RunReportView preflight = RunReports.Of(plan, RunReports.Scheduled, null);
        object started = JobSnapshots.Record(RunJobs.Start(request, preflight, order.Queue,
            order.AcknowledgeGasLost), request.Tool, RunTakedowns.Removed(plan), source, RunTakedowns.Burnt(plan));
        object reply = order.Fields.Verbose
            ? started
            : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Fresh()));
    }

    private static JObject Described(RunPlan plan, RunRequest request, RunBuildOrder order, List<ThingId?> named,
        List<ReportProblem> unread)
    {
        List<ReportProblem> problems = new List<ReportProblem>(unread);
        foreach (LayoutIssue issue in plan.Problems)
        {
            problems.Add(JobReports.Problem(issue.Code, issue.Message,
                issue.Id.HasValue ? new ThingId(issue.Id.Value) : null, named));
        }

        bool pipes = request.Kind.Family is PipeFamily;
        GasHold.Preview(pipes, order.AcknowledgeGasLost);
        if (GasHold.Judge(pipes, order.AcknowledgeGasLost) is GasHoldVerdict.Refusing refusing)
        {
            problems.Add(new ReportProblem(null, null, VerbError.Of(GasHold.Refusal(refusing))));
        }

        List<ReportTarget> targets = new List<ReportTarget>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (!removal.Assumed)
            {
                targets.Add(new ReportTarget(new ThingId(removal.Piece.ReferenceId).ToString(), null,
                    Names.Of(removal.Piece), new JValue(removal.Piece.PrefabName), new JValue(Removed),
                    removal.Debris ? RemoveRuns.BurntMark : order.Mark, Lifted(plan, removal.Piece.ReferenceId)));
            }
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            targets.Add(cell.Existing != null
                ? new ReportTarget(new ThingId(cell.Existing.ReferenceId).ToString(), null, Names.Of(cell.Existing),
                    new JValue(cell.Existing.PrefabName), new JValue(cell.Choice.Prefab.PrefabName), order.Mark,
                    Lifted(plan, cell.Cell))
                : new ReportTarget(null, CellOf(cell.Cell), cell.Choice.Prefab.PrefabName, JValue.CreateNull(),
                    new JValue(cell.Choice.Prefab.PrefabName), order.Mark, Lifted(plan, cell.Cell)));
        }

        RunReportView view = RunReports.Of(plan, RunReports.DryRun, null, RunPath.MaximumCells);
        return Report.Of(targets, JobReports.Body(view, problems.Count == 0), problems);
    }

    private static bool Refusing(RunRequest request, RunBuildOrder order) =>
        GasHold.Judge(request.Kind.Family is PipeFamily, order.AcknowledgeGasLost) is GasHoldVerdict.Refusing;

    // The cell's centre in metres, as a waypoint is given: [x, y, z]. A new piece has no id, so its cell is the row.
    private static JArray CellOf(GridCell cell)
    {
        UnityEngine.Vector3 centre = PieceShapes.CentreOf(cell);
        return new JArray(Decimal(centre.x), Decimal(centre.y), Decimal(centre.z));
    }

    private static double Decimal(float metres) => Math.Round(metres * 100.0) / 100.0;

    // PASSED: allow lint let a problem-level finding about this cell through, so the row says so.
    private static IReadOnlyList<string>? Lifted(RunPlan plan, GridCell cell)
    {
        foreach (LayoutIssue issue in plan.Lifted)
        {
            if (issue.Cell.HasValue && issue.Cell.Value.Equals(cell))
            {
                return LintLifted;
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? Lifted(RunPlan plan, long id)
    {
        foreach (LayoutIssue issue in plan.Lifted)
        {
            if (issue.Id.HasValue && issue.Id.Value == id)
            {
                return LintLifted;
            }
        }

        return null;
    }
}
