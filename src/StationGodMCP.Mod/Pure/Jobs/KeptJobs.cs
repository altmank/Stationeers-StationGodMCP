#nullable enable

using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure.Jobs;

/// <summary>
/// What the mod keeps of building jobs for polls and undo_job, bounded by memory (protocol.md, Job pins): each job's
/// snapshots (JobSnapshots: how what it removes stood, its tool and source) and each ended job's final status
/// (HeldTickJobs: the view its polls answer), in two stores with a budget each, and one set of pins over both, so a
/// job's snapshot and final status are pinned and released together.
/// <para>
/// A job is pinned to the (owner, stream) of the call that started or queued it, stream 0 included. release of that
/// stream drops its pins, on a stream already ended too, which is its main use: a hub releases a run's jobs when its
/// reference to them expires or under its own memory bound. stream_end leaves pins alone: a run's undo outlives its
/// stream. An owner's end releases every pin of it: a connection without a resume token that closed, a resume token's
/// window that passed or a bye on it, and session_end. Then nothing of that owner can come back for them, while a
/// resume token's pins survive a break and its resume. A world change drops every pin.
/// </para>
/// <para>
/// The stores evict oldest first, passing over pinned jobs (KeptJobStore); a job a store evicts is remembered for
/// undo_expired. Main thread only: it hears release, ended streams and session ends through StreamStores.Main.
/// </para>
/// </summary>
internal sealed class KeptJobs : IPinStore, ISessionEndStore
{
    /// <summary>
    /// Snapshot memory: 32 MB holds about sixty removals of 2,000 network pieces, or thousands of typical ones (about
    /// 7 KB each).
    /// </summary>
    internal const long DefaultSnapshotBudget = 32L * 1024 * 1024;

    /// <summary>
    /// Final-status memory: 4 MB holds about forty-five views of 500-piece runs, or several hundred typical ones (2 to
    /// 8 KB each). A job past it is still undone from the request's facts while its snapshots are kept.
    /// </summary>
    internal const long DefaultFinalBudget = 4L * 1024 * 1024;

    internal KeptJobs(long snapshotBudget = DefaultSnapshotBudget, long finalBudget = DefaultFinalBudget,
        int newestKept = KeptJobStore<object>.DefaultNewestKept)
    {
        Snapshots = new KeptJobStore<RecordedJob>(snapshotBudget, Pins, newestKept);
        Finals = new KeptJobStore<object>(finalBudget, Pins, newestKept);
        Snapshots.Evicted += jobId => LeftOne(jobId, Finals.Contains(jobId));
        Finals.Evicted += jobId => LeftOne(jobId, Snapshots.Contains(jobId));
    }

    /// <summary>The mod's kept jobs.</summary>
    internal static KeptJobs Main { get; } = new KeptJobs();

    internal JobPins Pins { get; } = new JobPins();

    internal KeptJobStore<RecordedJob> Snapshots { get; }

    /// <summary>Ended jobs' final views, as their polls answer them.</summary>
    internal KeptJobStore<object> Finals { get; }

    internal void Pin(string jobId, CallContext context) => Pins.Pin(jobId, context);

    public void Release(string owner, long stream) => Pins.Release(owner, stream);

    public void Ended(StreamEnd end)
    {
        if (end.IsWorld)
        {
            Pins.Clear();
        }
        else if (end.IsWholeConnection)
        {
            Pins.ReleaseOwner(end.Owner!);
        }
    }

    public void SessionEnded(string owner) => Pins.ReleaseOwner(owner);

    private void LeftOne(string jobId, bool otherKeeps)
    {
        if (!otherKeeps)
        {
            Pins.Forget(jobId);
        }
    }
}
