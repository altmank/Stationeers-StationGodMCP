#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// place_cables, place_pipes and place_chutes: lay a run (waypoints, cells or one piece) with the piece of the grade whose ends
/// match each cell's connections, joining what is there; optionally removing pieces in the same job (a reroute), or
/// checking it as if things were already gone (assume_removed: a real run needs them gone).
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
                "remove_ids", "assume_removed", "allow_bridge", "allow_split", "allow_split_long", "root", "join_to",
                "join_trunk", "wait");
        }

        List<ExtraEnd> extra = RunArgs.ExtraEnds(args);
        List<GridCell> run = RunArgs.Run(args, extra) ??
                             throw ApiErrors.InvalidArgument("Pass waypoints, cells or piece.");
        RunBuild build = new RunBuild(RunArgs.Shape(args, run), RunArgs.Grade(args, kind),
            args.Has("piece") ? JoinMode.None : RunArgs.Join(args), extra);
        RunRemoval removal = new RunRemoval(
            args.Has("remove_ids") ? args.ThingIds("remove_ids", RunPlanner.MaximumRemovals) : new List<ThingId>(),
            new List<GridCell>(),
            args.Has("assume_removed")
                ? args.ThingIds("assume_removed", RunPlanner.MaximumRemovals)
                : new List<ThingId>());
        return Run(args, new RunRequest(kind, kind.PlaceTool, build, removal, RunArgs.Options(args, kind)));
    }

    internal static object Remove(Args args, RunKind kind)
    {
        if (args.Has("job_id"))
        {
            return Status(args, "reference_ids", "waypoints", "cells", "allow_split", "allow_bridge", "root", "wait");
        }

        args.Reject(kind.RemoveTool, "grade", "join", "extra_ends", "piece", "branches", "remove_ids",
            "assume_removed", "allow_split_long", "network_id", "kind", "join_to", "join_trunk");
        return Run(args,
            new RunRequest(kind, kind.RemoveTool, null, Removal(args, kind), RunArgs.Options(args, kind)));
    }

    /// <summary>
    /// plan_removal: a remove tool's dry run under a read-only name, so a refund and would_split can be priced where
    /// the remove tools may not be called. Also takes network_id: every piece of that network.
    /// </summary>
    internal static RunReportView PlanRemoval(Args args)
    {
        args.Reject("plan_removal", "dry_run", "confirm", "job_id", "grade", "join", "extra_ends", "piece",
            "branches", "remove_ids", "assume_removed", "allow_split_long", "join_to", "join_trunk", "wait");
        RunKind kind = (args.OptionalString("kind") ?? "cable").Trim().ToLowerInvariant() switch
        {
            "cable" => new CableRunKind(),
            "pipe" => new PipeRunKind(),
            "chute" => new ChuteRunKind(),
            _ => throw ApiErrors.InvalidArgument("kind must be cable, pipe or chute.")
        };
        RunPlan plan = RunPlanner.Plan(new RunRequest(kind, "plan_removal", null, Removal(args, kind),
            RunArgs.Options(args, kind)));
        return RunReports.Of(plan, RunReports.DryRun, null);
    }

    private static RunRemoval Removal(Args args, RunKind kind)
    {
        int forms = (args.Has("reference_ids") ? 1 : 0) + (args.Has("waypoints") ? 1 : 0) +
                    (args.Has("cells") ? 1 : 0) + (args.Has("network_id") ? 1 : 0);
        if (forms != 1)
        {
            throw ApiErrors.InvalidArgument(
                "Pass one of reference_ids, waypoints or cells (plan_removal also takes network_id).");
        }

        if (args.Has("network_id"))
        {
            ThingId network = NetworkHandles.Resolve(args, "network_id", kind.Family);
            List<ThingId> ids = new List<ThingId>();
            foreach (SmallGrid member in kind.Family.NetworkMembers(network))
            {
                if (kind.Family.IsPiece(member))
                {
                    ids.Add(new ThingId(member.ReferenceId));
                }
            }

            if (ids.Count == 0)
            {
                throw ApiErrors.Refused("network_empty", $"Network {network} has no {kind.Noun} piece.");
            }

            return new RunRemoval(ids, new List<GridCell>());
        }

        return args.Has("reference_ids")
            ? new RunRemoval(args.ThingIds("reference_ids", RunPlanner.MaximumRemovals), new List<GridCell>())
            : new RunRemoval(new List<ThingId>(), RunArgs.Run(args, new List<ExtraEnd>())!);
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

        if (plan.AssumedPresent.Count > 0)
        {
            plan.Problem(RunPlanner.AssumedPresentCode,
                $"{plan.AssumedPresent.Count} thing(s) in assume_removed still stand (first " +
                $"{plan.AssumedPresent[0]}); remove them first, or pass the {request.Kind.Noun} pieces as " +
                "remove_ids to remove them in this job.", plan.AssumedPresent[0]);
        }

        return plan.Ready
            ? RunJobs.Start(request, RunReports.Of(plan, RunReports.Scheduled, null),
                args.OptionalBool("wait") ?? false)
            : RunReports.Of(plan, RunReports.Refused, null);
    }
}
