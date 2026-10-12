#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// drop_job's rule over the job slot (HeldTickJobs.DropWaiting): only a job still waiting in the line can be dropped.
/// The running job and an ended one are refused job_not_waiting, an id the slot does not know job_not_found, and
/// nothing leaves the line then. An owner's abrupt end (session_end, a resume window that passed, a connection without
/// a token closed without bye) takes every job its calls queued out of the line (TakeOwners), so none starts after its
/// held restores; a job that has started runs on.
/// </summary>
internal static class JobDrops
{
    internal const string DroppedCode = "dropped";
    internal const string NotWaitingCode = "job_not_waiting";
    internal const string SessionEndedCode = "session_ended";
    internal const string NotFoundCode = "job_not_found";

    /// <summary>job_not_found: the id is not running, not queued, and no final status is kept for it.</summary>
    internal static ApiException NotFound(string id) =>
        ApiErrors.Refused(NotFoundCode, $"No job {id} is running, queued or among the ended jobs kept.");

    /// <summary>
    /// Takes the waiting job out of the line and returns what would have started it; throws the refusal otherwise.
    /// runningId is the job that holds the slot (null for none), ended whether the id is among the finished jobs kept.
    /// </summary>
    internal static T Take<T>(JobLine<T> waiting, string id, string? runningId, bool ended)
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

        throw NotFound(id);
    }

    /// <summary>
    /// The abrupt end of owner's session: every waiting job queued by a call of that owner (contextOf gives the call's context)
    /// leaves the line into taken, front first; the rest keep their order.
    /// </summary>
    internal static void TakeOwners<T>(JobLine<T> waiting, string owner, Func<T, CallContext> contextOf,
        List<KeyValuePair<string, T>> taken) =>
        waiting.TakeWhere(start => string.Equals(contextOf(start).Owner, owner, StringComparison.Ordinal), taken);

    /// <summary>The error the poll of a job dropped at its owner's abrupt end answers (JobDroppedView).</summary>
    internal static ErrorView SessionEnded() =>
        new ErrorView(SessionEndedCode,
            "The session that queued it ended abruptly before it started, so it was dropped; nothing was changed.");

    /// <summary>The error a dropped job's poll answers (JobDroppedView).</summary>
    internal static ErrorView Dropped() =>
        new ErrorView(DroppedCode, "Dropped from the queue before it started; nothing was changed.");
}
