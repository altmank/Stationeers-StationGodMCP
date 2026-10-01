#nullable enable

using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace StationGodMCP.Pure;

/// <summary>
/// Work run on a pool thread while the caller waits, with a time limit, and what is left over when the limit passes.
/// A task that overran is not cancelled (the game's atmosphere code cannot be stopped part way): it is kept as
/// Outstanding, and until it ends no new work starts (Run throws WorkStillRunning) and the job runner keeps the game
/// tick held (HeldTickJobs). Settle, every frame, sees it end, hands back its failure for the log and forgets it.
/// One instance per kind of work; every member is called on the main thread.
/// </summary>
internal sealed class OutstandingWork
{
    private readonly TimeSpan _limit;
    private Task? _outstanding;
    private string? _owner;

    internal OutstandingWork(TimeSpan limit)
    {
        _limit = limit;
    }

    /// <summary>Whether work that overran its limit is still running.</summary>
    internal bool Busy => _outstanding != null;

    /// <summary>
    /// Runs work on a pool thread and waits up to the limit. Its own exception is rethrown as it was. Past the limit
    /// it throws TimeoutException and the task is kept as Outstanding, owned by owner (the job, for the gas hold).
    /// </summary>
    internal T Run<T>(Func<T> work, string? owner)
    {
        if (_outstanding != null)
        {
            throw new WorkStillRunningException(
                $"Earlier atmosphere work (job {_owner ?? "unknown"}) is still running past its {_limit.TotalSeconds} s limit.");
        }

        Task<T> task = Task.Run(work);
        try
        {
            if (!task.Wait(_limit))
            {
                _outstanding = task;
                _owner = owner;
                throw new TimeoutException($"Atmosphere work did not finish within {_limit.TotalSeconds} s.");
            }
        }
        catch (AggregateException failure) when (failure.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
        }

        return task.Result;
    }

    /// <summary>
    /// Every frame: Running while overrun work goes on, Idle when there is none, or Ended once (with the work's
    /// failure, if any, and its owner) the first frame after it finished; it is forgotten then.
    /// </summary>
    internal WorkSettlement Settle()
    {
        Task? task = _outstanding;
        if (task == null)
        {
            return WorkSettlement.Idle.Instance;
        }

        if (!task.IsCompleted)
        {
            return WorkSettlement.Running.Instance;
        }

        _outstanding = null;
        string? owner = _owner;
        _owner = null;
        // Reading Exception marks the task's failure observed (no UnobservedTaskException later).
        Exception? failure = task.IsFaulted ? task.Exception?.GetBaseException() : null;
        return new WorkSettlement.Ended(owner, failure, task.IsCanceled);
    }
}

/// <summary>What OutstandingWork.Settle found this frame. A closed set.</summary>
internal abstract class WorkSettlement
{
    private WorkSettlement()
    {
    }

    /// <summary>No overrun work.</summary>
    internal sealed class Idle : WorkSettlement
    {
        internal static readonly Idle Instance = new Idle();

        private Idle()
        {
        }
    }

    /// <summary>Overrun work is still running: nothing may touch what it touches.</summary>
    internal sealed class Running : WorkSettlement
    {
        internal static readonly Running Instance = new Running();

        private Running()
        {
        }
    }

    /// <summary>Overrun work has just ended, with its failure if it threw.</summary>
    internal sealed class Ended : WorkSettlement
    {
        internal Ended(string? owner, Exception? failure, bool canceled)
        {
            Owner = owner;
            Failure = failure;
            Canceled = canceled;
        }

        internal string? Owner { get; }

        internal Exception? Failure { get; }

        internal bool Canceled { get; }
    }
}

/// <summary>Run refused: work that overran its limit earlier has not finished yet.</summary>
internal sealed class WorkStillRunningException : InvalidOperationException
{
    internal WorkStillRunningException(string message)
        : base(message)
    {
    }
}
