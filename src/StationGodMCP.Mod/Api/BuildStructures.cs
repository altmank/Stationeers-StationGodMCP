#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;

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
                return Polled(poll.JobId, poll.Verbose);
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
                object started = JobSnapshots.Record(
                    BuildJobs.Start("place", placing, args.OptionalBool("wait") ?? false, acknowledge),
                    "place_structure", Replaced(plan), args);
                return run.Verbose ? started : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
            default:
                throw ApiErrors.InvalidArgument(Vocabulary.Text("Pass {arg:job_id}, or {arg:placements}."));
        }
    }

    /// <summary>A job as polled: brief (no preflight, no final check unless refused) unless verbose.</summary>
    internal static object Polled(string jobId, bool verbose)
    {
        object polled = HeldTickJobs.Status(jobId);
        return verbose ? polled : BuildJobView.Brief(polled);
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
