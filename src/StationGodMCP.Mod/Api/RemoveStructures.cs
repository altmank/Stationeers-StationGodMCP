#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api;

/// <summary>
/// A structure removal in its Args form (dry run, or dry_run false with confirm), as undo_job runs it to take back
/// what a build job placed: structures by reference id taken apart as deconstructing them by hand would, giving back
/// what that gives back, with minimal guards (contents, a breach, the game's own refusal). One job.
/// </summary>
internal static class RemoveStructureApi
{
    internal static object Handle(Args args)
    {
        switch (BuildArgs.ParseRemove(args))
        {
            case BuildForm<RemoveArguments>.Poll poll:
                return PlaceStructureApi.Polled(poll.JobId, poll.Verbose);
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
                object started = JobSnapshots.Record(
                    BuildJobs.Start("remove", removing, args.OptionalBool("wait") ?? false, acknowledge),
                    "remove_structure", plan.Takedowns.ConvertAll(takedown => takedown.Piece), args);
                return run.Verbose ? started : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
            default:
                throw ApiErrors.InvalidArgument(Vocabulary.Text("Pass {arg:job_id}, or {arg:reference_ids}."));
        }
    }
}
