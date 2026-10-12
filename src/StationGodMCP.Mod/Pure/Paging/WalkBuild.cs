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

/// <summary>
/// A walk's ordered ids built in slices over frames (protocol.md, Walks): the candidates are the list taken when the
/// walk starts (things built later are not in it), filtered one at a time, then sorted by a bottom-up merge sort
/// whose every step is bounded: insertion-sorted runs of SortRun keys, then merge passes that move at most MergeStep
/// keys per step. Run takes steps until the clock reaches the slice's end, at least one, so a slice overruns its end
/// by one step at most (one candidate's filter, one run's sort or MergeStep moves). The order is ListKey.Compare's, a
/// total order (reference ids differ), so the ids come out exactly as a one-shot sort of the same keys gives them.
/// A candidate gone by the time its turn comes is the filter's to refuse; one gone after it was kept is skipped
/// when its page is served. Main thread only; after the key arrays (taken at the start and at the first merge pass)
/// a step allocates nothing.
/// </summary>
internal sealed class WalkBuild<TThing> where TThing : class
{
    /// <summary>Keys insertion-sorted in one step: at most 120 comparisons.</summary>
    internal const int SortRun = 16;

    /// <summary>Keys a merge step moves.</summary>
    internal const int MergeStep = 256;

    private readonly IWalkFilter<TThing> _filter;
    private readonly ListOrder _order;
    private List<TThing>? _candidates;
    private ListKey[] _from;
    private ListKey[] _to;
    private int _count;
    private int _scanned;
    private int _sorted;
    private int _width;
    private int _mid;
    private int _hi;
    private int _left;
    private int _right;
    private int _out;
    private Phase _phase;

    internal WalkBuild(List<TThing> candidates, IWalkFilter<TThing> filter, ListOrder order)
    {
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _order = order;
        Candidates = candidates.Count;
        _from = new ListKey[candidates.Count];
        _to = _from;
    }

    private enum Phase
    {
        Filter,
        Sort,
        Merge,
        Done
    }

    /// <summary>The candidates taken at the start, matching or not.</summary>
    internal int Candidates { get; }

    /// <summary>The candidates kept so far.</summary>
    internal int Count => _count;

    internal bool Done => _phase == Phase.Done;

    /// <summary>Steps taken so far.</summary>
    internal long Steps { get; private set; }

    /// <summary>Takes steps until the clock reaches untilMs, at least one; true once the ids are in order.</summary>
    internal bool Run(IMonotonicClock clock, double untilMs)
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
    internal long[] Ids()
    {
        if (!Done)
        {
            throw new InvalidOperationException("The walk's ids are not in order yet.");
        }

        long[] ids = new long[_count];
        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] = _from[index].Id;
        }

        return ids;
    }

    private void Step()
    {
        Steps++;
        switch (_phase)
        {
            case Phase.Filter:
                Filter();
                break;
            case Phase.Sort:
                SortRunOf();
                break;
            case Phase.Merge:
                Merge();
                break;
        }
    }

    private void Filter()
    {
        List<TThing> candidates = _candidates!;
        if (_scanned < candidates.Count)
        {
            if (_filter.TryKey(candidates[_scanned], out ListKey key))
            {
                _from[_count++] = key;
            }

            _scanned++;
        }

        if (_scanned >= candidates.Count)
        {
            _candidates = null;
            _phase = _count > 1 ? Phase.Sort : Phase.Done;
        }
    }

    private void SortRunOf()
    {
        int end = Math.Min(_sorted + SortRun, _count);
        for (int index = _sorted + 1; index < end; index++)
        {
            ListKey key = _from[index];
            int place = index - 1;
            while (place >= _sorted && ListKey.Compare(_order, _from[place], key) > 0)
            {
                _from[place + 1] = _from[place];
                place--;
            }

            _from[place + 1] = key;
        }

        _sorted = end;
        if (_sorted < _count)
        {
            return;
        }

        if (_count <= SortRun)
        {
            _phase = Phase.Done;
            return;
        }

        _to = new ListKey[_count];
        _width = SortRun;
        Pair(0);
        _phase = Phase.Merge;
    }

    // One merge step: up to MergeStep keys from the pair of runs being merged (and the next pairs) into _to; a pass that
    // ends swaps the arrays and doubles the run width, and the pass whose width covers every key ends the sort.
    private void Merge()
    {
        for (int moved = 0; moved < MergeStep; moved++)
        {
            if (_out == _hi)
            {
                if (_hi >= _count)
                {
                    ListKey[] merged = _to;
                    _to = _from;
                    _from = merged;
                    _width *= 2;
                    if (_width >= _count)
                    {
                        _phase = Phase.Done;
                        return;
                    }

                    Pair(0);
                }
                else
                {
                    Pair(_hi);
                }
            }

            if (_left < _mid && (_right >= _hi || ListKey.Compare(_order, _from[_left], _from[_right]) <= 0))
            {
                _to[_out++] = _from[_left++];
            }
            else
            {
                _to[_out++] = _from[_right++];
            }
        }
    }

    private void Pair(int lo)
    {
        _mid = Math.Min(lo + _width, _count);
        _hi = Math.Min(lo + 2 * _width, _count);
        _left = lo;
        _right = _mid;
        _out = lo;
    }
}
