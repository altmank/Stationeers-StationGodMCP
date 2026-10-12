#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure.Jobs;

/// <summary>
/// Which jobs are pinned, and to which (owner, stream) (CallContext): a job started or queued by a call is pinned to
/// that call's context, stream 0 included, and its snapshot and final status (KeptJobs) are then passed over by
/// eviction until the pin is released: by release of that stream, by the owner's end, or by a world change. A job
/// started outside a call (CallContext.None) is not pinned. A pin is forgotten once its job has left both stores.
/// Main thread only.
/// </summary>
internal sealed class JobPins
{
    private readonly Dictionary<string, CallContext> _byJob =
        new Dictionary<string, CallContext>(StringComparer.Ordinal);
    private readonly Dictionary<CallContext, List<string>> _byContext = new Dictionary<CallContext, List<string>>();

    /// <summary>Jobs pinned.</summary>
    internal int Count => _byJob.Count;

    /// <summary>Pins the job to context; a job already pinned keeps its first pin, and None pins nothing.</summary>
    internal void Pin(string jobId, CallContext context)
    {
        if (context.IsNone || _byJob.ContainsKey(jobId))
        {
            return;
        }

        _byJob.Add(jobId, context);
        if (!_byContext.TryGetValue(context, out List<string> jobs))
        {
            jobs = new List<string>();
            _byContext.Add(context, jobs);
        }

        jobs.Add(jobId);
    }

    internal bool IsPinned(string jobId) => _byJob.ContainsKey(jobId);

    /// <summary>release: drops the pins of the owner's stream; returns how many.</summary>
    internal int Release(string owner, long stream)
    {
        CallContext context = new CallContext(owner, stream);
        if (!_byContext.TryGetValue(context, out List<string> jobs))
        {
            return 0;
        }

        _byContext.Remove(context);
        foreach (string jobId in jobs)
        {
            _byJob.Remove(jobId);
        }

        return jobs.Count;
    }

    /// <summary>The owner's state ended: drops the pins of every stream of it; returns how many.</summary>
    internal int ReleaseOwner(string owner)
    {
        List<long>? streams = null;
        foreach (CallContext context in _byContext.Keys)
        {
            if (string.Equals(context.Owner, owner, StringComparison.Ordinal))
            {
                (streams ??= new List<long>()).Add(context.Stream);
            }
        }

        int released = 0;
        if (streams != null)
        {
            foreach (long stream in streams)
            {
                released += Release(owner, stream);
            }
        }

        return released;
    }

    /// <summary>The job has left every store: its pin, if any, goes.</summary>
    internal void Forget(string jobId)
    {
        if (!_byJob.TryGetValue(jobId, out CallContext context))
        {
            return;
        }

        _byJob.Remove(jobId);
        List<string> jobs = _byContext[context];
        jobs.Remove(jobId);
        if (jobs.Count == 0)
        {
            _byContext.Remove(context);
        }
    }

    internal void Clear()
    {
        _byJob.Clear();
        _byContext.Clear();
    }
}
