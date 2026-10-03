#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// Sorts a call into its lane (scheduling.md, The prediction). Cost world, plan and job is heavy whatever it took
/// before. Any other cost is predicted from the method's last Window calls at that cost: their main-thread time per
/// item (total time over total items) times this call's items, so a 5-item read_devices stays light while a 128-item
/// one moves to the heavy lane on its own. A method with no history yet is light: its first call is what it learns
/// from. Main thread only.
/// </summary>
internal sealed class CostPredictor
{
    /// <summary>Calls each moving average spans.</summary>
    internal const int Window = 32;

    private static readonly int CostClasses = Enum.GetValues(typeof(CostClass)).Length;

    private readonly Dictionary<string, SampleWindow?[]> _byMethod =
        new Dictionary<string, SampleWindow?[]>(StringComparer.Ordinal);

    /// <summary>Whether the cost class is heavy by nature, so measurement never moves it to the light lane.</summary>
    internal static bool IsHeavyByClass(CostClass cost) =>
        cost == CostClass.World || cost == CostClass.Plan || cost == CostClass.Job;

    internal Lane LaneFor(CallProfile profile, double heavyThresholdMs)
    {
        if (IsHeavyByClass(profile.Cost))
        {
            return Lane.Heavy;
        }

        return WindowOf(profile, create: false) is SampleWindow window &&
               window.PerItemMs * profile.Items > heavyThresholdMs
            ? Lane.Heavy
            : Lane.Light;
    }

    /// <summary>The predicted main-thread milliseconds of this call; null for heavy-by-class costs and before any history.</summary>
    internal double? PredictMs(CallProfile profile) =>
        !IsHeavyByClass(profile.Cost) && WindowOf(profile, create: false) is SampleWindow window
            ? window.PerItemMs * profile.Items
            : null;

    /// <summary>One finished call's main-thread time (handler and serialising).</summary>
    internal void Observe(CallProfile profile, double elapsedMs)
    {
        if (IsHeavyByClass(profile.Cost) || double.IsNaN(elapsedMs) || elapsedMs < 0.0)
        {
            return;
        }

        WindowOf(profile, create: true)!.Add(elapsedMs, profile.Items);
    }

    private SampleWindow? WindowOf(CallProfile profile, bool create)
    {
        if (!_byMethod.TryGetValue(profile.Method, out SampleWindow?[]? windows))
        {
            if (!create)
            {
                return null;
            }

            windows = new SampleWindow?[CostClasses];
            _byMethod.Add(profile.Method, windows);
        }

        int slot = (int)profile.Cost;
        if (windows[slot] == null && create)
        {
            windows[slot] = new SampleWindow();
        }

        return windows[slot];
    }

    /// <summary>The last Window calls' times and item counts, with running sums so each update is constant time.</summary>
    private sealed class SampleWindow
    {
        private readonly double[] _ms = new double[Window];
        private readonly int[] _items = new int[Window];
        private int _count;
        private int _next;
        private double _sumMs;
        private long _sumItems;

        internal double PerItemMs => _sumItems > 0 ? Math.Max(_sumMs, 0.0) / _sumItems : 0.0;

        internal void Add(double ms, int items)
        {
            if (_count == Window)
            {
                _sumMs -= _ms[_next];
                _sumItems -= _items[_next];
            }
            else
            {
                _count++;
            }

            _ms[_next] = ms;
            _items[_next] = items;
            _sumMs += ms;
            _sumItems += items;
            _next = (_next + 1) % Window;
        }
    }
}
