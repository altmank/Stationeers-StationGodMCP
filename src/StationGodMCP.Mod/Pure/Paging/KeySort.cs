#nullable enable

using System;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// A list's keys sorted in bounded steps (a walk's build, WalkBuild and ScanBuild): insertion-sorted runs of SortRun
/// keys, then bottom-up merge passes that move at most MergeStep keys per step. The order is a total order (reference
/// ids differ), so the keys come out exactly as a one-shot sort gives them. After the second array (taken at the first
/// merge pass) a step allocates nothing. Main thread only.
/// </summary>
internal sealed class KeySort
{
    /// <summary>Keys insertion-sorted in one step: at most 120 comparisons.</summary>
    internal const int SortRun = 16;

    /// <summary>Keys a merge step moves.</summary>
    internal const int MergeStep = 256;

    private readonly Comparison<ListKey> _compare;
    private int _count;
    private ListKey[] _from;
    private ListKey[] _to;
    private int _sorted;
    private int _width;
    private int _mid;
    private int _hi;
    private int _left;
    private int _right;
    private int _out;
    private bool _merging;

    /// <summary>Sorts the first count keys of keys (the array is sorted in place or swapped for its merged copy).</summary>
    internal KeySort(ListKey[] keys, int count, Comparison<ListKey> compare)
        : this(keys, compare)
    {
        Start(count);
    }

    /// <summary>
    /// A sort of keys whose count is told later (Start), everything it needs taken now, so its steps allocate nothing:
    /// a walk's build makes it before its filter fills keys.
    /// </summary>
    internal KeySort(ListKey[] keys, Comparison<ListKey> compare)
    {
        _from = keys ?? throw new ArgumentNullException(nameof(keys));
        _to = keys.Length > SortRun ? new ListKey[keys.Length] : keys;
        _compare = compare ?? throw new ArgumentNullException(nameof(compare));
    }

    /// <summary>The first count keys are in; the sort's steps may begin.</summary>
    internal void Start(int count)
    {
        _count = count;
        Done = count <= 1;
        Started = true;
    }

    internal bool Started { get; private set; }

    internal bool Done { get; private set; }

    /// <summary>The keys in order, once Done (the first count of them).</summary>
    internal ListKey[] Keys => Done ? _from : throw new InvalidOperationException("The keys are not in order yet.");

    internal int Count => _count;

    /// <summary>The order of a paged list (ListKey.Compare's).</summary>
    internal static Comparison<ListKey> Of(ListOrder order) => (a, b) => ListKey.Compare(order, a, b);

    /// <summary>One bounded step: one run's insertion sort, or MergeStep moves.</summary>
    internal void Step()
    {
        if (Done || !Started)
        {
            return;
        }

        if (_merging)
        {
            Merge();
        }
        else
        {
            SortRunOf();
        }
    }

    private void SortRunOf()
    {
        int end = Math.Min(_sorted + SortRun, _count);
        for (int index = _sorted + 1; index < end; index++)
        {
            ListKey key = _from[index];
            int place = index - 1;
            while (place >= _sorted && _compare(_from[place], key) > 0)
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
            Done = true;
            return;
        }

        _width = SortRun;
        Pair(0);
        _merging = true;
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
                        Done = true;
                        return;
                    }

                    Pair(0);
                }
                else
                {
                    Pair(_hi);
                }
            }

            if (_left < _mid && (_right >= _hi || _compare(_from[_left], _from[_right]) <= 0))
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
