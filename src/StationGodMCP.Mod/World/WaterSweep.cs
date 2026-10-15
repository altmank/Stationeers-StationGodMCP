#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The atmospheres holding water, refreshed (cost design A2; review D, R-D-15): no change point tells water arriving,
/// so a background sweep reads the game's atmosphere pool and then its pipe networks PoolAStep entries a frame, and each
/// sweep that ends replaces the set with the atmospheres it found holding any water, polluted water or steam. find
/// water reads the latest set (proportional to its members, never the world's atmospheres) with the game time its sweep
/// began (as_of_s); each member's water is read live when asked. The sweep runs while find water has been asked within
/// IdleS and stops after; the first call after that, or before a first sweep has ended, reads the pools itself.
/// </summary>
[ReplyFact(FactRule.Refreshed, "which atmospheres and pipe networks hold water")]
internal static class WaterSweep
{
    /// <summary>Pool entries read a frame.</summary>
    internal const int PoolAStep = 256;

    // The sweep stops this long (real seconds) after the last find water.
    private const double IdleS = 600.0;

    private static readonly List<Atmosphere> Sweeping = new List<Atmosphere>();
    private static readonly List<PipeNetwork> SweepingNetworks = new List<PipeNetwork>();
    private static List<Atmosphere>? _latest;
    private static double _latestAsOfS;
    private static double _sweepStartS;
    private static bool _networks;
    private static int _from;
    private static bool _running;
    private static long _askedAt;

    /// <summary>
    /// The latest swept set and the game time its sweep began; false (and the sweep started) when none has ended since
    /// find water was last asked. Main thread.
    /// </summary>
    internal static bool TryLatest(out IReadOnlyList<Atmosphere> atmospheres, out double asOfS)
    {
        _askedAt = Stopwatch.GetTimestamp();
        if (!_running)
        {
            Start();
        }

        atmospheres = _latest ?? (IReadOnlyList<Atmosphere>)System.Array.Empty<Atmosphere>();
        asOfS = _latestAsOfS;
        return _latest != null;
    }

    /// <summary>Each server frame: one slice of the sweep while find water is being asked.</summary>
    internal static void Tick()
    {
        if (!_running || GameManager.GameState != GameState.Running)
        {
            return;
        }

        if ((Stopwatch.GetTimestamp() - _askedAt) / (double)Stopwatch.Frequency > IdleS)
        {
            Clear();
            return;
        }

        using ProfScope scope = Prof.Scope(ProfId.WorldFacts);
        bool ended;
        if (!_networks)
        {
            if (World.TryAtmospheres(_from, PoolAStep, static atmosphere => HoldsWater(atmosphere), Sweeping,
                    out ended))
            {
                _from += PoolAStep;
                if (ended)
                {
                    _networks = true;
                    _from = 0;
                }
            }

            return;
        }

        if (World.TryPipeNetworks(_from, PoolAStep, static network => HoldsWater(network.Atmosphere), SweepingNetworks,
                out ended))
        {
            _from += PoolAStep;
            if (ended)
            {
                Ended();
            }
        }
    }

    /// <summary>A world left, or the sweep idle: no set, no sweep.</summary>
    internal static void Clear()
    {
        _running = false;
        _latest = null;
        Sweeping.Clear();
        SweepingNetworks.Clear();
    }

    /// <summary>Water, polluted water or steam above nothing, in an atmosphere not being destroyed.</summary>
    internal static bool HoldsWater(Atmosphere? atmosphere)
    {
        if (atmosphere == null || atmosphere.BeingDestroyed)
        {
            return false;
        }

        GasMixture mixture = atmosphere.GasMixture;
        return mixture.Water.Quantity.ToDouble() + mixture.PollutedWater.Quantity.ToDouble() +
            mixture.Steam.Quantity.ToDouble() > 0.0;
    }

    private static void Start()
    {
        _running = true;
        _networks = false;
        _from = 0;
        _sweepStartS = UnityEngine.Time.time;
        Sweeping.Clear();
        SweepingNetworks.Clear();
    }

    // A sweep's atmospheres, each once (a pool read in parts may list one twice, a network's may be listed already),
    // become the latest set; the next sweep begins.
    private static void Ended()
    {
        HashSet<Atmosphere> listed = new HashSet<Atmosphere>();
        List<Atmosphere> latest = new List<Atmosphere>(Sweeping.Count + SweepingNetworks.Count);
        foreach (Atmosphere atmosphere in Sweeping)
        {
            if (listed.Add(atmosphere))
            {
                latest.Add(atmosphere);
            }
        }

        foreach (PipeNetwork network in SweepingNetworks)
        {
            if (network.Atmosphere != null && listed.Add(network.Atmosphere))
            {
                latest.Add(network.Atmosphere);
            }
        }

        _latest = latest;
        _latestAsOfS = _sweepStartS;
        Start();
    }
}
