#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Structures;

namespace StationGodMCP.Api;

/// <summary>
/// replace_walls: replace walls and windows (exactly Wall or WallTransparent) with another wall or window prefab of
/// the same footprint, in place, without opening a face: the old piece is only removed once the new one stands in its
/// slot at its final build state, all in one frame with the game tick held. Dry run by default; a real run needs
/// dry_run false and confirm true, and is a job polled with job_id (StructureJobs). Host only.
/// </summary>
internal static class ReplaceWallsApi
{
    internal static object Handle(Args args) => ReplaceStructuresApi.Handle(args, new WallFamily());
}

/// <summary>
/// replace_frames: replace frames (exactly Frame) with another frame prefab of the same footprint, or by default
/// with their own prefab at its final state, which finishes unfinished frames in place. Otherwise as replace_walls.
/// </summary>
internal static class ReplaceFramesApi
{
    internal static object Handle(Args args) => ReplaceStructuresApi.Handle(args, new FrameFamily());
}

/// <summary>The request forms the replace tools share: a dry run, a confirmed run, or a job's status.</summary>
internal static class ReplaceStructuresApi
{
    internal static object Handle(Args args, StructureFamily family)
    {
        switch (StructureSwapArgs.Parse(args, family.TargetRequired))
        {
            case StructureSwapForm.Poll poll:
                return HeldTickJobs.Status(poll.JobId);
            case StructureSwapForm.Run run:
                StructureSwapRequest request = new StructureSwapRequest(family, run.Arguments);
                StructureSwapPlan plan = StructureSwapPlanner.Plan(request);
                if (!run.Confirmed)
                {
                    return StructureReports.Of(plan, StructureReports.DryRun, null);
                }

                return plan.Ready
                    ? StructureJobs.Start(request, plan, args.OptionalBool("wait") ?? false)
                    : StructureReports.Of(plan, StructureReports.Refused, null);
            default:
                throw ApiErrors.InvalidArgument("Pass job_id, or reference_ids or room_id.");
        }
    }
}
