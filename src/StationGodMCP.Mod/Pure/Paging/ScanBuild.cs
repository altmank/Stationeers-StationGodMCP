#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// The keys a scan gathers (ScanBuild): a body adds each kept entry's key and counts each candidate it looked at.
/// </summary>
internal sealed class ScanKeys
{
    internal List<ListKey> Keys { get; } = new List<ListKey>();

    /// <summary>The candidates the scan looked at, kept or not.</summary>
    internal int Candidates { get; set; }

    internal void Add(ListKey key) => Keys.Add(key);
}

/// <summary>
/// A walk's ordered ids from a scan spread over frames (A3): a Work body walks its candidates one per step (a class of
/// the world index, a network's members, an area's cells), adding the key of each one it keeps, then the keys are
/// sorted in bounded steps (KeySort). Run takes steps until the clock reaches the slice's end, at least one, and stops
/// at the call's stop (WorkStop). The ids come out as a one-shot sort of the same keys gives them, or, for a list whose
/// scan already walks it in order (InScanOrder: an area's cells, a network's members), as the scan kept them. Main thread
/// only.
/// </summary>
internal sealed class ScanBuild : IWalkBuild
{
    private readonly Work<ScanKeys> _scan;
    private readonly Comparison<ListKey>? _compare;
    private KeySort? _sort;
    private int _count;

    private static readonly Comparison<ListKey> NoOrder = static (_, _) => 0;

    internal ScanBuild(Func<ScanKeys, IEnumerable<object?>> body, Comparison<ListKey> compare)
        : this(body, compare ?? throw new ArgumentNullException(nameof(compare)), true)
    {
    }

    internal ScanBuild(Func<ScanKeys, IEnumerable<object?>> body, ListOrder order)
        : this(body, KeySort.Of(order))
    {
    }

    private ScanBuild(Func<ScanKeys, IEnumerable<object?>> body, Comparison<ListKey>? compare, bool _)
    {
        if (body == null)
        {
            throw new ArgumentNullException(nameof(body));
        }

        _compare = compare;
        _scan = new Work<ScanKeys>(answer => Scanned(body, answer));
    }

    /// <summary>A build whose ids are the scan's keys in the order the scan kept them: no sort.</summary>
    internal static ScanBuild InScanOrder(Func<ScanKeys, IEnumerable<object?>> body) => new ScanBuild(body, null, true);

    public bool Done => _sort != null && _sort.Done;

    /// <summary>The candidates the scan looked at (0 until it has ended).</summary>
    public int Candidates => _scan.Done ? _scan.Value.Candidates : 0;

    public bool Run(IMonotonicClock clock, double untilMs)
    {
        while (!Done)
        {
            if (_sort == null)
            {
                if (!_scan.Step(clock, untilMs))
                {
                    return false;
                }

                List<ListKey> keys = _scan.Value.Keys;
                _sort = new KeySort(keys.ToArray(), _compare == null ? 0 : keys.Count, _compare ?? NoOrder);
                if (_compare == null)
                {
                    _count = keys.Count;
                }
            }
            else
            {
                _sort.Step();
            }

            if (clock.NowMs >= untilMs || WorkStop.Requested)
            {
                break;
            }
        }

        return Done;
    }

    public long[] Ids()
    {
        if (!Done)
        {
            throw new InvalidOperationException("The walk's ids are not in order yet.");
        }

        return KeyIds.Of(_sort!.Keys, _compare == null ? _count : _sort.Count);
    }

    private static IEnumerable<object?> Scanned(Func<ScanKeys, IEnumerable<object?>> body, Answer<ScanKeys> answer)
    {
        ScanKeys keys = new ScanKeys();
        foreach (object? step in body(keys))
        {
            yield return step;
        }

        answer.Value = keys;
    }
}
