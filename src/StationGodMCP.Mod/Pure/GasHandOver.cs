#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>A pipe network that could take a removed device's gas: its pressure with the gas added, and its rating.</summary>
internal sealed class GasReceiver
{
    internal GasReceiver(long network, double pressureAfterKpa, double? ratingKpa, bool takesLiquid, bool leftByJob)
    {
        Network = network;
        PressureAfterKpa = pressureAfterKpa;
        RatingKpa = ratingKpa;
        TakesLiquid = takesLiquid;
        LeftByJob = leftByJob;
    }

    internal long Network { get; }

    internal double PressureAfterKpa { get; }

    /// <summary>The weakest pipe's rating; null for a network without pipes.</summary>
    internal double? RatingKpa { get; }

    /// <summary>A liquid pipe network: liquid in a gas pipe network damages it.</summary>
    internal bool TakesLiquid { get; }

    /// <summary>The job removes every pipe of it, so nothing would hold the gas.</summary>
    internal bool LeftByJob { get; }
}

/// <summary>
/// remove_structure gas_to: which pipe network takes a device's gas before the device goes, or why none can. A network
/// qualifies when the job leaves it standing, it has pipes, it carries liquid when the gas holds liquid, and the
/// pressure with the gas added stays within its weakest pipe's rating (so the hand-over cannot burst it); the first
/// that qualifies, in the order given, is chosen.
/// </summary>
internal static class GasHandOver
{
    internal static GasReceiver? Choose(IReadOnlyList<GasReceiver> candidates, bool holdsLiquid, List<string> why)
    {
        foreach (GasReceiver candidate in candidates)
        {
            string name = candidate.Network.ToString(CultureInfo.InvariantCulture);
            if (candidate.LeftByJob)
            {
                why.Add($"pipe network {name} is removed by this job");
            }
            else if (!candidate.RatingKpa.HasValue)
            {
                why.Add($"pipe network {name} has no pipes");
            }
            else if (holdsLiquid && !candidate.TakesLiquid)
            {
                why.Add($"pipe network {name} carries gas and the device holds liquid");
            }
            else if (candidate.PressureAfterKpa > candidate.RatingKpa.Value)
            {
                why.Add(string.Format(CultureInfo.InvariantCulture,
                    "pipe network {0} would reach {1:0} kPa, over its weakest pipe's {2:0}", name,
                    candidate.PressureAfterKpa, candidate.RatingKpa.Value));
            }
            else
            {
                return candidate;
            }
        }

        if (candidates.Count == 0)
        {
            why.Add("it joins no pipe network");
        }

        return null;
    }
}
