#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure;

/// <summary>
/// Where undo_job takes a job's facts (JobEndFacts) from. The job's own final view while the mod keeps it (KeptJobs,
/// bounded by memory): the request's facts are then ignored, since the mod's view is the job as it ended and a hub's
/// copy may be partial (a brief poll) or stale. Past that, the request's created_ids, removed, changed and placed, but
/// only while the job's snapshots are still kept: they hold how what the job removed stood, its tool and its source,
/// and they show the job was started in this world. A job whose snapshots the mod evicted, or whose final status it
/// evicted with no snapshots and no facts to plan from, is Expired (undo_expired), whatever else is kept: what it
/// removed can no longer be built back as it stood. Otherwise nothing: job_not_found, as without the request's facts
/// (an id the mod never had, or one evicted before the ids its stores remember).
/// </summary>
internal abstract class UndoFactsSource
{
    private UndoFactsSource()
    {
    }

    /// <param name="kept">The job's final view as the client sees it (wire JSON); null when the mod no longer keeps it.</param>
    /// <param name="snapshotsKept">Whether the job's snapshots are still kept.</param>
    /// <param name="given">The request's facts (JobEndFacts.FromRequest); null when it gave none.</param>
    /// <param name="snapshotEvicted">Whether the snapshot store evicted the job (KeptJobStore.WasEvicted).</param>
    /// <param name="finalEvicted">Whether the final-status store evicted the job.</param>
    /// <param name="jobId">The job, which Expired names.</param>
    internal static UndoFactsSource Choose(JObject? kept, bool snapshotsKept, JObject? given,
        bool snapshotEvicted = false, bool finalEvicted = false, string jobId = "")
    {
        if (snapshotEvicted)
        {
            return new Expired(jobId, false);
        }

        if (kept != null)
        {
            return new Kept(JobEndFacts.Of(kept));
        }

        if (given != null && snapshotsKept)
        {
            return new Given(given);
        }

        return finalEvicted ? new Expired(jobId, snapshotsKept) : new NotFound(given != null);
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
    /// The request's facts, for a job past the ones the mod keeps. The job ended (it is neither running nor queued),
    /// so it is planned as finished; only what the facts list is undone, which plan.notes says (Note).
    /// </summary>
    internal sealed class Given : UndoFactsSource
    {
        /// <summary>The status the job is planned with: finished, as the request's facts tell it.</summary>
        internal const string Status = "applied";

        internal Given(JObject facts)
        {
            Facts = facts;
        }

        internal JObject Facts { get; }

        internal static string Note(string jobId) =>
            $"{jobId}'s final status is no longer kept: what it built and removed is the request's " +
            Vocabulary.Text("{arg:created_ids}, {arg:removed}, {arg:changed} and {arg:placed}, and only what they " +
                "list is undone.");
    }

    /// <summary>
    /// undo_expired (data: {job_id}): the mod had the job, but its snapshots, or its final status with no snapshots
    /// left to plan from, were evicted under their memory bound (KeptJobs: its pins released, or pinned jobs alone past
    /// the bound). FactsWouldDo: its snapshots are kept, so the request's facts would plan it.
    /// </summary>
    internal sealed class Expired : UndoFactsSource
    {
        internal const string Code = "undo_expired";

        internal Expired(string jobId, bool factsWouldDo)
        {
            JobId = jobId;
            FactsWouldDo = factsWouldDo;
        }

        internal string JobId { get; }

        internal bool FactsWouldDo { get; }

        internal string Message => FactsWouldDo
            ? $"{JobId}'s final status was evicted under the memory bound for kept jobs. " + Vocabulary.Text(
                "Pass the {arg:created_ids}, {arg:removed}, {arg:changed} and {arg:placed} its job_ended event or " +
                "final reply gave to undo it; nothing was changed.")
            : $"{JobId} can no longer be undone: what undoing it needs was evicted under the memory bound for kept " +
              "jobs (its stream's pins were released, or pinned jobs alone passed the bound). Nothing was changed.";
    }

    /// <summary>No facts: job_not_found. FactsGiven: the request gave facts, but the job's snapshots are gone too.</summary>
    internal sealed class NotFound : UndoFactsSource
    {
        internal NotFound(bool factsGiven)
        {
            FactsGiven = factsGiven;
        }

        internal bool FactsGiven { get; }
    }
}
