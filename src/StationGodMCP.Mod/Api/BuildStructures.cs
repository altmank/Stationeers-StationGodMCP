#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// place_structure: place any structure prefab some kit builds, at a position and turn, at a build state, with a label
/// and colour, checked as the game's own cursor checks it, paid from an inventory (or free in a creative world). A
/// list of placements is one job. Dry run by default; a real run needs dry_run false and confirm true, and is a job
/// polled with job_id (BuildJobs). Host only.
/// </summary>
internal static class PlaceStructureApi
{
    internal static object Handle(Args args)
    {
        switch (BuildArgs.ParsePlace(args))
        {
            case BuildForm<PlaceArguments>.Poll poll:
                return HeldTickJobs.Status(poll.JobId);
            case BuildForm<PlaceArguments>.Run run:
                PlacePlan plan = PlacePlanner.Plan(run.Arguments);
                string? acknowledge = GasHoldArgs.Acknowledgement(args);
                if (!run.Confirmed)
                {
                    GasHold.Preview(plan.TouchesPipes, acknowledge);
                }

                if (!run.Confirmed || !plan.Ready)
                {
                    return BuildReports.Of(plan, run.Confirmed ? BuildReports.Refused : BuildReports.DryRun, null);
                }

                PlaceReportView preflight = BuildReports.Of(plan, BuildReports.Scheduled, null);
                PlaceWork placing = new PlaceWork(run.Arguments, preflight, plan.TouchesPipes);
                return JobSnapshots.Record(
                    BuildJobs.Start("place", placing, args.OptionalBool("wait") ?? false, acknowledge),
                    "place_structure", Replaced(plan), args);
            default:
                throw ApiErrors.InvalidArgument("Pass job_id, or placements.");
        }
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

/// <summary>
/// remove_structure: remove structures by reference id as deconstructing them by hand would, giving back what that
/// gives back, with minimal guards (contents, a breach, the game's own refusal). One job; as place_structure.
/// </summary>
internal static class RemoveStructureApi
{
    internal static object Handle(Args args)
    {
        switch (BuildArgs.ParseRemove(args))
        {
            case BuildForm<RemoveArguments>.Poll poll:
                return HeldTickJobs.Status(poll.JobId);
            case BuildForm<RemoveArguments>.Run run:
                RemovePlan plan = RemovePlanner.Plan(run.Arguments);
                string? acknowledge = GasHoldArgs.Acknowledgement(args);
                if (!run.Confirmed)
                {
                    GasHold.Preview(plan.TouchesPipes, acknowledge);
                }

                if (!run.Confirmed || !plan.Ready)
                {
                    return BuildReports.Of(plan, run.Confirmed ? BuildReports.Refused : BuildReports.DryRun, null);
                }

                RemoveReportView preflight = BuildReports.Of(plan, BuildReports.Scheduled, null);
                RemoveWork removing = new RemoveWork(run.Arguments, preflight, plan.TouchesPipes);
                return JobSnapshots.Record(
                    BuildJobs.Start("remove", removing, args.OptionalBool("wait") ?? false, acknowledge),
                    "remove_structure", plan.Takedowns.ConvertAll(takedown => takedown.Piece), args);
            default:
                throw ApiErrors.InvalidArgument("Pass job_id, or reference_ids.");
        }
    }
}
