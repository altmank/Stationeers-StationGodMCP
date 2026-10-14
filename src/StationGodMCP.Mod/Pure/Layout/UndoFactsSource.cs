#nullable enable

using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure;

/// <summary>
/// Where undo takes a job's facts (JobEndFacts) from: the job's own final view while the mod keeps it (KeptJobs,
/// bounded by memory), planned as it ended. A job whose snapshots the mod evicted, or whose final status it evicted
/// with no snapshots left to plan from, is Expired (undo_expired), whatever else is kept: what it removed can no
/// longer be built back as it stood. Otherwise nothing: NotFound, answered job_not_found (an id the mod never had, or
/// one evicted before the ids its stores remember).
/// </summary>
internal abstract class UndoFactsSource
{
    private UndoFactsSource()
    {
    }

    /// <param name="kept">The job's final view as the client sees it (wire JSON); null when the mod no longer keeps it.</param>
    /// <param name="snapshotEvicted">Whether the snapshot store evicted the job (KeptJobStore.WasEvicted).</param>
    /// <param name="finalEvicted">Whether the final-status store evicted the job.</param>
    /// <param name="jobId">The job, which Expired names.</param>
    internal static UndoFactsSource Choose(JObject? kept, bool snapshotEvicted = false, bool finalEvicted = false,
        string jobId = "")
    {
        if (snapshotEvicted)
        {
            return new Expired(jobId);
        }

        if (kept != null)
        {
            return new Kept(JobEndFacts.Of(kept));
        }

        return finalEvicted ? new Expired(jobId) : new NotFound();
    }

    /// <summary>The job's own final view, which the mod still keeps.</summary>
    internal sealed class Kept : UndoFactsSource
    {
        internal Kept(JObject facts)
        {
            Facts = facts;
        }

        internal JObject Facts { get; }
    }

    /// <summary>
    /// undo_expired (data: {job_id}): the mod had the job, but its snapshots, or its final status with no snapshots
    /// left to plan from, were evicted under their memory bound (KeptJobs: its pins released, or pinned jobs alone past
    /// the bound). Nothing can be built back as it stood.
    /// </summary>
    internal sealed class Expired : UndoFactsSource
    {
        internal const string Code = "undo_expired";

        internal Expired(string jobId)
        {
            JobId = jobId;
        }

        internal string JobId { get; }

        internal string Message => $"{JobId} can no longer be undone: what undoing it needs was evicted under the " +
            "memory bound for kept jobs (its stream's pins were released, or pinned jobs alone passed the bound). " +
            "Nothing was changed.";
    }

    /// <summary>No facts: job_not_found, the refusal the job's own poll answered.</summary>
    internal sealed class NotFound : UndoFactsSource
    {
    }
}
