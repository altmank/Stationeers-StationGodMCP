#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>build structure (catalogue build.json): any structure a kit builds, at a point with a turn.</summary>
internal static class BuildStructure
{
    internal static VerbOutcome Run(BuildStructureOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        StructureBuildOrder? order = StructureBuildOrder.Of(options, problems, out VerbError? missing);
        return order != null
            ? StructureBuild.Run(order)
            : new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(BuildStructureOptions.Spec.Label, problems));
    }
}

/// <summary>
/// build structure over PlacePlanner, BuildReports and BuildJobs: any structure a kit builds, one placement or a list
/// of them in one job, at a point with a turn, at a build state, with a label and a colour, checked as the game's own
/// cursor checks it, paid from the holders materials names (or free in a creative world). Without confirm, a Report.Of
/// report: every placement a target (the id of the fuselage piece it replaces, else the cell) [name, current -> new]
/// with the what's undo mark ([undo]; PASSED for a placement a lint problem was let through on), the plan's report the
/// body (tool, status and job_id left out), every problem with the placement it is about, and a gas hold the confirmed
/// call would be refused on among them. Confirmed with a problem, the report answers and nothing starts; confirmed and
/// ready, one job starts and its undo is recorded (JobSnapshots). verbose answers the whole preflight instead of the
/// started job with its summary.
/// </summary>
internal static class StructureBuild
{
    // The job's name in its status, in inspect job and in its undo record.
    private const string JobTool = "build structure";

    /// <summary>The guard a lint problem was let through on, for the rows about it.</summary>
    private static readonly string[] LintLifted = { WriteLint.Guard };

    internal static VerbOutcome Run(StructureBuildOrder order)
    {
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(order.Materials, order.Label, more);
        PlaceRequest arguments = order.Arguments(from, more);
        JobSource source = new JobSource(from?.Value, order.Free, null, null);
        return WriteCheck<PlacePlan>.Planned(answer => PlacePlanner.Planning(arguments, true, answer), order.Report,
            plan => Act(plan, arguments, order, source), plan => Described(plan, order));
    }

    /// <summary>
    /// One placement call's reply, for a caller that runs the call itself (undo's replay): the report until it
    /// acts, or the started job. A refusal the call cannot be read with is thrown, as any call's is.
    /// </summary>
    internal static object Reply(StructureBuildOrder order) => VerbOutcome.Settled(Run(order)) switch
    {
        VerbOutcome.Answered answered => answered.Reply,
        VerbOutcome.Refused refused => throw ApiErrors.Refused(refused.Error.Code, refused.Error.Message),
        _ => throw new InvalidOperationException("A settled call is answered or refused.")
    };

    private static VerbOutcome Act(PlacePlan plan, PlaceRequest arguments, StructureBuildOrder order,
        JobSource source)
    {
        if (!plan.Ready)
        {
            return new VerbOutcome.Answered(Described(plan, order));
        }

        PlaceReportView preflight = BuildReports.Of(plan, BuildReports.Scheduled, null);
        PlaceWork placing = new PlaceWork(arguments, preflight, plan.TouchesPipes);
        object started = JobSnapshots.Record(BuildJobs.Start("place", placing, order.Queue, order.AcknowledgeGasLost),
            JobTool, Replaced(plan), source);
        object reply = order.Fields.Verbose
            ? started
            : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Serializer));
    }

    private static ReportReply Described(PlacePlan plan, StructureBuildOrder order)
    {
        List<ReportProblem> problems = new List<ReportProblem>(plan.Problems.Count);
        foreach (BuildIssueView issue in plan.Problems)
        {
            problems.Add(new ReportProblem(issue.Index, issue.ReferenceId?.ToString(),
                new VerbError(issue.Code, issue.Message)));
        }

        GasHold.Preview(plan.TouchesPipes, order.AcknowledgeGasLost);
        if (GasHold.Judge(plan.TouchesPipes, order.AcknowledgeGasLost) is GasHoldVerdict.Refusing refusing)
        {
            problems.Add(new ReportProblem(null, null, VerbError.Of(GasHold.Refusal(refusing))));
        }

        List<ReportTarget> targets = new List<ReportTarget>(plan.Placements.Count);
        foreach (PlannedPlacement placement in plan.Placements)
        {
            targets.Add(Target(plan, placement, order.Mark));
        }

        PlaceReportView view = BuildReports.Of(plan, BuildReports.DryRun, null);
        return Report.Of(targets, JobReports.Body(view, problems.Count == 0), problems);
    }

    // One placement's row: the id of the fuselage piece it replaces (a merge), else the cell it goes to; what stands
    // there now (the piece it replaces) and the prefab it becomes.
    private static ReportTarget Target(PlacePlan plan, PlannedPlacement placement, string mark)
    {
        Structure? prefab = placement.Prefab;
        string name = prefab != null ? prefab.PrefabName : placement.Args.Prefab.ToString();
        IReadOnlyList<string>? passed = Passed(plan, placement.Index);
        Structure? replaced = placement.Replaces;
        return replaced != null
            ? new ReportTarget(new ThingId(replaced.ReferenceId).ToString(), null, Names.Of(replaced),
                new JValue(replaced.PrefabName), new JValue(name), mark, passed)
            : new ReportTarget(null, CellOf(placement), name, JValue.CreateNull(), new JValue(name), mark, passed);
    }

    // Where the placement goes, in metres: as planned, else the point asked for, else the point of its at.
    private static JArray? CellOf(PlannedPlacement placement)
    {
        if (placement.Position is { } position)
        {
            return Point(position.x, position.y, position.z);
        }

        if (placement.At is { } at)
        {
            return Point(at.X, at.Y, at.Z);
        }

        return placement.Args.At is AtForm.Absolute absolute
            ? Point(absolute.Point.X, absolute.Point.Y, absolute.Point.Z)
            : null;
    }

    private static JArray Point(double x, double y, double z) => new JArray(Decimal(x), Decimal(y), Decimal(z));

    private static double Decimal(double metres) => Math.Round(metres * 100.0) / 100.0;

    // PASSED: allow lint let a problem-level finding about this placement through, so the row says so.
    private static IReadOnlyList<string>? Passed(PlacePlan plan, int index)
    {
        List<string>? guards = null;
        foreach (PlaceLift lift in plan.Lifted)
        {
            if (lift.Index == index)
            {
                (guards ??= new List<string>()).Add(lift.Guard);
            }
        }

        return guards ?? (IReadOnlyList<string>?)null;
    }

    // The fuselage pieces the run replaces (merges): the job removes them, so their snapshots are taken with it.
    private static List<Structure> Replaced(PlacePlan plan)
    {
        List<Structure> replaced = new List<Structure>();
        foreach (PlannedPlacement placement in plan.Placements)
        {
            if (placement.Replaces != null)
            {
                replaced.Add(placement.Replaces);
            }
        }

        return replaced;
    }
}
