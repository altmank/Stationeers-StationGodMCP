#nullable enable

using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// drop_job's rule over the job slot (HeldTickJobs.DropWaiting): only a job still waiting in the line can be dropped.
/// The running job and an ended one are refused job_not_waiting, an id the slot does not know job_not_found, and
/// nothing leaves the line then.
/// </summary>
internal static class JobDrops
{
    internal const string DroppedCode = "dropped";
    internal const string NotWaitingCode = "job_not_waiting";

    /// <summary>
    /// Takes the waiting job out of the line and returns what would have started it; throws the refusal otherwise.
    /// runningId is the job that holds the slot (null for none), ended whether the id is among the finished jobs kept.
    /// </summary>
    internal static T Take<T>(JobLine<T> waiting, string id, string? runningId, bool ended, int kept)
    {
        if (waiting.Remove(id, out T start))
        {
            return start;
        }

        if (runningId == id)
        {
            throw ApiErrors.Refused(NotWaitingCode,
                $"Job {id} has started; it cannot be dropped. Poll it with {Vocabulary.Arg("job_id")} to follow it " +
                "to its end.");
        }

        if (ended)
        {
            throw ApiErrors.Refused(NotWaitingCode,
                $"Job {id} has ended; there is nothing to drop. Poll it with {Vocabulary.Arg("job_id")} for its " +
                "result.");
        }

        throw ApiErrors.Refused("job_not_found", $"No job {id} is running, queued or among the last {kept}.");
    }

    /// <summary>The error a dropped job's poll answers (JobDroppedView).</summary>
    internal static ErrorView Dropped() =>
        new ErrorView(DroppedCode, "Dropped from the queue before it started; nothing was changed.");
}
