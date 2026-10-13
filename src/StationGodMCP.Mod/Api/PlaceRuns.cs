#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api;

/// <summary>
/// place_cables, place_pipes and place_chutes: lay a run (waypoints, cells or one piece) with the piece of the grade whose ends
/// match each cell's connections, joining what is there; optionally removing pieces in the same job (a reroute), or
/// checking it as if things were already gone (assume_removed: a real run needs them gone). Dry run by default; a real
/// run needs dry_run false and confirm true and is a job polled with job_id (RunJobs). Host only.
/// </summary>
internal static class PlaceCablesApi
{
    internal static object Handle(Args args) => RunApi.Place(args, new CableRunKind());
}

internal static class PlacePipesApi
{
    internal static object Handle(Args args) => RunApi.Place(args, new PipeRunKind());
}

internal static class PlaceChutesApi
{
    internal static object Handle(Args args) => RunApi.Place(args, new ChuteRunKind());
}

/// <summary>The request forms the place tools share: a dry run, a confirmed run, or a job's status.</summary>
internal static class RunApi
{
    /// <summary>
    /// The switch for the whole job view: a poll's preflight and final check, and a confirmed run's preflight (its
    /// reply otherwise carries the preflight in short).
    /// </summary>
    private const string VerboseArgument = "verbose";

    internal static object Place(Args args, RunKind kind)
    {
        if (args.Has("job_id"))
        {
            return Status(args, "waypoints", "cells", "piece", "pieces", "branches", "grade", "join", "extra_ends",
                "remove_ids", "assume_removed", "allow_bridge", "allow_split", "allow_split_long", "root", "join_to",
                "join_trunk", "wait");
        }

        List<ExtraEnd> extra = RunArgs.ExtraEnds(args);
        List<GridCell> run = RunArgs.Run(args, extra) ??
                             throw ApiErrors.InvalidArgument(Vocabulary.Text("Pass {arg:waypoints}, {arg:cells}, " +
                                 "{arg:piece} or {arg:pieces}."));
        RunBuild build = new RunBuild(RunArgs.Shape(args, run), RunArgs.Grade(args, kind),
            args.Has("piece") || args.Has("pieces") ? JoinMode.None : RunArgs.Join(args), extra);
        RunRemoval removal = new RunRemoval(
            args.Has("remove_ids") ? args.ThingIds("remove_ids", RunPlanner.MaximumRemovals) : new List<ThingId>(),
            new List<GridCell>(),
            args.Has("assume_removed")
                ? args.ThingIds("assume_removed", RunPlanner.MaximumRemovals)
                : new List<ThingId>());
        return Run(args, new RunRequest(kind, kind.PlaceTool, build, removal, RunArgs.Options(args, kind)));
    }

    private static object Status(Args args, params string[] others)
    {
        args.Reject("{arg:job_id}", others);
        args.Reject("{arg:job_id}", "dry_run", "confirm", "from_id", "refund", "refund_to", "limit", "include_links",
            "include_notes", "include_network_devices", GasHoldVerdict.AcknowledgeArgument);
        object polled = HeldTickJobs.Status(args.String("job_id").Trim());
        return (args.OptionalBool(VerboseArgument) ?? false) ? polled : RunJobView.Brief(polled);
    }

    private static object Run(Args args, RunRequest request)
    {
        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun)
        {
            args.Reject("a dry run (it is for a confirmed run or a {arg:job_id} poll)", VerboseArgument);
        }

        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("{arg:confirm}: true needs {arg:dry_run}: false; nothing " +
                "was changed."));
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                Vocabulary.Text("A real run needs {arg:dry_run}: false and {arg:confirm}: true; nothing was changed."));
        }

        RunPlan plan = RunPlanner.Plan(request);
        string? acknowledge = GasHoldArgs.Acknowledgement(args);
        if (dryRun)
        {
            GasHold.Preview(request.Kind.Family is PipeFamily, acknowledge);
            return RunReports.Of(plan, RunReports.DryRun, null);
        }

        // Queued behind another job (wait), assumed things may be what that job removes: the check is made again when
        // this one starts (RunWaiting), on the world the earlier jobs left.
        bool wait = args.OptionalBool("wait") ?? false;
        if (!(wait && HeldTickJobs.Occupied))
        {
            RunPlanner.RequireAssumedGone(plan);
        }

        if (!plan.Ready)
        {
            return RunReports.Of(plan, RunReports.Refused, null);
        }

        RunReportView preflight = RunReports.Of(plan, RunReports.Scheduled, null);
        object started = JobSnapshots.Record(RunJobs.Start(request, preflight, wait, acknowledge), request.Tool,
            RunTakedowns.Removed(plan), args, RunTakedowns.Burnt(plan));
        return (args.OptionalBool(VerboseArgument) ?? false)
            ? started
            : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
    }
}
