#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Pure.Paging;

/// <summary>A walk's filter: whether a candidate is in the walk, and its place in the walk's order.</summary>
internal interface IWalkFilter<in TThing>
{
    bool TryKey(TThing thing, out ListKey key);
}

/// <summary>A walk's build as a page over frames uses it, whatever its candidates are (WalkBuild).</summary>
internal interface IWalkBuild
{
    bool Done { get; }

    /// <summary>The candidates taken at the start, matching or not.</summary>
    int Candidates { get; }

    bool Run(IMonotonicClock clock, double untilMs);

    long[] Ids();
}

/// <summary>
/// A walk's ordered ids built in slices over frames (protocol.md, Walks): the candidates are the list taken when the
/// walk starts (things built later are not in it), filtered one at a time, then sorted in bounded steps (KeySort). Run
/// takes steps until the clock reaches the slice's end, at least one, so a slice overruns its end by one step at most
/// (one candidate's filter, one run's sort or one merge step). The order is ListKey.Compare's, a total order (reference
/// ids differ), so the ids come out exactly as a one-shot sort of the same keys gives them. A candidate gone by the
/// time its turn comes is the filter's to refuse; one gone after it was kept is skipped when its page is served. Main
/// thread only; after the key arrays a step allocates nothing.
/// </summary>
internal sealed class WalkBuild<TThing> : IWalkBuild where TThing : class
{
    private readonly IWalkFilter<TThing> _filter;
    private readonly ListKey[] _keys;
    private readonly KeySort _sort;
    private List<TThing>? _candidates;
    private int _count;
    private int _scanned;

    internal WalkBuild(List<TThing> candidates, IWalkFilter<TThing> filter, ListOrder order)
    {
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        Candidates = candidates.Count;
        _keys = new ListKey[candidates.Count];
        _sort = new KeySort(_keys, KeySort.Of(order));
    }

    /// <summary>The candidates taken at the start, matching or not.</summary>
    public int Candidates { get; }

    /// <summary>The candidates kept so far.</summary>
    internal int Count => _count;

    public bool Done => _sort.Started && _sort.Done;

    /// <summary>Steps taken so far.</summary>
    internal long Steps { get; private set; }

    /// <summary>Takes steps until the clock reaches untilMs, at least one; true once the ids are in order.</summary>
    public bool Run(IMonotonicClock clock, double untilMs)
    {
        while (!Done)
        {
            Step();
            if (clock.NowMs >= untilMs)
            {
                break;
            }
        }

        return Done;
    }

    /// <summary>The walk's reference ids in order, once Done.</summary>
    public long[] Ids()
    {
        if (!Done)
        {
            throw new InvalidOperationException("The walk's ids are not in order yet.");
        }

        return KeyIds.Of(_sort.Keys, _count);
    }

    private void Step()
    {
        Steps++;
        if (_sort.Started)
        {
            _sort.Step();
            return;
        }

        List<TThing> candidates = _candidates!;
        if (_scanned < candidates.Count)
        {
            if (_filter.TryKey(candidates[_scanned], out ListKey key))
            {
                _keys[_count++] = key;
            }

            _scanned++;
        }

        if (_scanned >= candidates.Count)
        {
            _candidates = null;
            _sort.Start(_count);
        }
    }
}

/// <summary>The reference ids of the first count keys, in their order.</summary>
internal static class KeyIds
{
    internal static long[] Of(ListKey[] keys, int count)
    {
        long[] ids = new long[count];
        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] = keys[index].Id;
        }

        return ids;
    }
}

/// <summary>
/// A walk whose ordered ids are known at once (a list sorted in one go as its call starts): nothing is left to build,
/// so its page (FramedPage) goes straight to describing its things.
/// </summary>
internal sealed class ReadyWalk : IWalkBuild
{
    private readonly long[] _ids;

    internal ReadyWalk(long[] ids, int candidates)
    {
        _ids = ids;
        Candidates = candidates;
    }

    public bool Done => true;

    public int Candidates { get; }

    public bool Run(IMonotonicClock clock, double untilMs) => true;

    public long[] Ids() => _ids;
}
