#nullable enable

using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure;

/// <summary>
/// Where undo_job takes a job's facts (JobEndFacts) from. The job's own final view while the mod keeps it (the last 16
/// jobs ended): the request's facts are then ignored, since the mod's view is the job as it ended and a hub's copy may
/// be partial (a brief poll) or stale. Past that, the request's created_ids, removed, changed and placed, but only
/// while the job's snapshots are still kept: they hold how what the job removed stood, its tool and its source, and
/// they show the job was started in this world. Otherwise nothing: job_not_found, as without the request's facts.
/// </summary>
internal abstract class UndoFactsSource
{
    private UndoFactsSource()
    {
    }

    /// <param name="kept">The job's final view as the client sees it (wire JSON); null when the mod no longer keeps it.</param>
    /// <param name="snapshotsKept">Whether the job's snapshots are still kept.</param>
    /// <param name="given">The request's facts (JobEndFacts.FromRequest); null when it gave none.</param>
    internal static UndoFactsSource Choose(JObject? kept, bool snapshotsKept, JObject? given)
    {
        if (kept != null)
        {
            return new Kept(JobEndFacts.Of(kept));
        }

        return given != null && snapshotsKept ? new Given(given) : new NotFound(given != null);
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

        internal static string Note(string jobId, int keptJobs) =>
            $"{jobId} is past the last {keptJobs} jobs kept: what it built and removed is the request's created_ids, " +
            "removed, changed and placed, and only what they list is undone.";
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
