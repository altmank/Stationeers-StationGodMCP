#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// place_cables, place_pipes and place_chutes: lay a run (waypoints, cells or one piece) with the piece of the grade whose ends
/// match each cell's connections, joining what is there; optionally removing pieces in the same job (a reroute).
/// remove_cables, remove_pipes and remove_chutes: remove listed pieces or the pieces in listed cells. Dry run by default; a real
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

internal static class RemoveCablesApi
{
    internal static object Handle(Args args) => RunApi.Remove(args, new CableRunKind());
}

internal static class RemovePipesApi
{
    internal static object Handle(Args args) => RunApi.Remove(args, new PipeRunKind());
}

internal static class PlaceChutesApi
{
    internal static object Handle(Args args) => RunApi.Place(args, new ChuteRunKind());
}

internal static class RemoveChutesApi
{
    internal static object Handle(Args args) => RunApi.Remove(args, new ChuteRunKind());
}

/// <summary>The request forms the run tools share: a dry run, a confirmed run, or a job's status.</summary>
internal static class RunApi
{
    internal static object Place(Args args, RunKind kind)
    {
        if (args.Has("job_id"))
        {
            return Status(args, "waypoints", "cells", "piece", "branches", "grade", "join", "extra_ends",
                "remove_ids", "allow_bridge", "allow_split", "allow_split_long");
        }

        List<ExtraEnd> extra = RunArgs.ExtraEnds(args);
        List<GridCell> run = RunArgs.Run(args, extra) ??
                             throw ApiErrors.InvalidArgument("Pass waypoints, cells or piece.");
        RunBuild build = new RunBuild(RunArgs.Shape(args, run), RunArgs.Grade(args, kind),
            args.Has("piece") ? JoinMode.None : RunArgs.Join(args), extra);
        RunRemoval removal = args.Has("remove_ids")
            ? new RunRemoval(args.ThingIds("remove_ids", RunPlanner.MaximumRemovals), new List<GridCell>())
            : RunRemoval.None;
        return Run(args, new RunRequest(kind, kind.PlaceTool, build, removal, RunArgs.Options(args)));
    }

    internal static object Remove(Args args, RunKind kind)
    {
        if (args.Has("job_id"))
        {
            return Status(args, "reference_ids", "waypoints", "cells", "allow_split", "allow_bridge");
        }

        args.Reject(kind.RemoveTool, "grade", "join", "extra_ends", "piece", "branches", "remove_ids",
            "allow_split_long");
        int forms = (args.Has("reference_ids") ? 1 : 0) + (args.Has("waypoints") ? 1 : 0) +
                    (args.Has("cells") ? 1 : 0);
        if (forms != 1)
        {
            throw ApiErrors.InvalidArgument("Pass one of reference_ids, waypoints or cells.");
        }

        RunRemoval removal = args.Has("reference_ids")
            ? new RunRemoval(args.ThingIds("reference_ids", RunPlanner.MaximumRemovals), new List<GridCell>())
            : new RunRemoval(new List<ThingId>(), RunArgs.Run(args, new List<ExtraEnd>())!);
        return Run(args, new RunRequest(kind, kind.RemoveTool, null, removal, RunArgs.Options(args)));
    }

    private static object Status(Args args, params string[] others)
    {
        args.Reject("job_id", others);
        args.Reject("job_id", "dry_run", "confirm", "from_id", "refund", "limit");
        return HeldTickJobs.Status(args.String("job_id").Trim());
    }

    private static object Run(Args args, RunRequest request)
    {
        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        RunPlan plan = RunPlanner.Plan(request);
        if (dryRun)
        {
            return RunReports.Of(plan, RunReports.DryRun, null);
        }

        return plan.Ready
            ? RunJobs.Start(request, RunReports.Of(plan, RunReports.Scheduled, null))
            : RunReports.Of(plan, RunReports.Refused, null);
    }
}
