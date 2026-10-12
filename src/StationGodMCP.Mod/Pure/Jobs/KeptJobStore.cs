#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Jobs;

/// <summary>
/// One store of what the mod keeps per job (KeptJobs: the snapshots, or the final statuses), bounded by memory: each
/// entry comes with its estimated bytes (KeptJobSizes), and storing one evicts until the store is within BudgetBytes.
/// Eviction goes oldest first (by first storing) and passes over pinned jobs (JobPins); only when the pinned jobs alone
/// pass the budget does the oldest pinned one go, so memory always wins. The NewestKept newest entries are never
/// evicted, whatever their size: one huge job cannot push out the job stored just before it, and the store may then
/// pass its budget by those entries alone. The last EvictedKept evicted ids are remembered (WasEvicted), so an undo
/// can tell a job the mod evicted from one it never had; an id evicted before those is forgotten like one never seen.
/// Main thread only.
/// </summary>
internal sealed class KeptJobStore<T> where T : class
{
    /// <summary>The newest entries, never evicted: enough for an undo's own removal, placement and piece runs.</summary>
    internal const int DefaultNewestKept = 4;

    /// <summary>Evicted ids remembered for undo_expired (about 60 bytes each).</summary>
    internal const int EvictedKept = 1024;

    private readonly Dictionary<string, LinkedListNode<Entry>> _byJob =
        new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);

    private readonly LinkedList<Entry> _order = new LinkedList<Entry>();
    private readonly string?[] _evicted = new string?[EvictedKept];
    private readonly JobPins _pins;
    private int _evictedNext;

    internal KeptJobStore(long budgetBytes, JobPins pins, int newestKept = DefaultNewestKept)
    {
        BudgetBytes = budgetBytes > 0 ? budgetBytes : throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        NewestKept = newestKept > 0 ? newestKept : throw new ArgumentOutOfRangeException(nameof(newestKept));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
    }

    internal long BudgetBytes { get; }

    internal int NewestKept { get; }

    /// <summary>The estimated bytes of every entry held.</summary>
    internal long Bytes { get; private set; }

    internal int Count => _byJob.Count;

    /// <summary>Told each job id this store evicts, as it goes.</summary>
    internal event Action<string>? Evicted;

    /// <summary>
    /// Keeps value for the job (in place of what was kept for it, at the same age), then evicts until the store is
    /// within its budget or only its newest entries are left.
    /// </summary>
    internal void Put(string jobId, T value, long bytes)
    {
        if (_byJob.TryGetValue(jobId, out LinkedListNode<Entry> node))
        {
            Bytes += bytes - node.Value.Bytes;
            node.Value.Value = value;
            node.Value.Bytes = bytes;
        }
        else
        {
            _byJob.Add(jobId, _order.AddLast(new Entry(jobId, value, bytes)));
            Bytes += bytes;
        }

        while (Bytes > BudgetBytes)
        {
            LinkedListNode<Entry>? victim = Oldest(passPinned: true) ?? Oldest(passPinned: false);
            if (victim == null)
            {
                return;
            }

            Evict(victim);
        }
    }

    /// <summary>What is kept for the job; null when nothing is.</summary>
    internal T? Of(string jobId) => _byJob.TryGetValue(jobId, out LinkedListNode<Entry> node) ? node.Value.Value : null;

    internal bool Contains(string jobId) => _byJob.ContainsKey(jobId);

    /// <summary>Whether the job is among the last EvictedKept this store evicted.</summary>
    internal bool WasEvicted(string jobId) => Array.IndexOf(_evicted, jobId) >= 0;

    /// <summary>The entries whose job is pinned now.</summary>
    internal int PinnedCount()
    {
        int pinned = 0;
        foreach (Entry entry in _order)
        {
            if (_pins.IsPinned(entry.JobId))
            {
                pinned++;
            }
        }

        return pinned;
    }

    /// <summary>The world was left: everything goes, the evicted record too, and no one is told.</summary>
    internal void Clear()
    {
        _byJob.Clear();
        _order.Clear();
        Array.Clear(_evicted, 0, _evicted.Length);
        _evictedNext = 0;
        Bytes = 0;
    }

    // The oldest entry outside the newest NewestKept, pinned ones passed over when passPinned; null when none.
    private LinkedListNode<Entry>? Oldest(bool passPinned)
    {
        LinkedListNode<Entry>? node = _order.First;
        for (int older = _order.Count - NewestKept; node != null && older > 0; older--)
        {
            if (!passPinned || !_pins.IsPinned(node.Value.JobId))
            {
                return node;
            }

            node = node.Next;
        }

        return null;
    }

    private void Evict(LinkedListNode<Entry> node)
    {
        string jobId = node.Value.JobId;
        _order.Remove(node);
        _byJob.Remove(jobId);
        Bytes -= node.Value.Bytes;
        _evicted[_evictedNext] = jobId;
        _evictedNext = (_evictedNext + 1) % EvictedKept;
        Evicted?.Invoke(jobId);
    }

    private sealed class Entry
    {
        internal Entry(string jobId, T value, long bytes)
        {
            JobId = jobId;
            Value = value;
            Bytes = bytes;
        }

        internal string JobId { get; }

        internal T Value { get; set; }

        internal long Bytes { get; set; }
    }
}
