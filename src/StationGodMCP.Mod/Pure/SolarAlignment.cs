#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>solar_aim's arithmetic on the best facing the search found.</summary>
internal static class SolarAlignment
{
    /// <summary>The Vertical setting at which a panel's pitch pivot is level: Euler(V - 90, 0, 0).</summary>
    internal const float PitchZero = 90f;

    /// <summary>Degrees between the cells' facing and the sun, from their dot product (clamped to -1..1).</summary>
    internal static double OffDegrees(float dot) => Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))) * 180.0 / Math.PI;

    /// <summary>1 - 2 sin(off / 2), floored at 0: the share of full output that facing gives before shading.</summary>
    internal static double Alignment(double offDegrees) =>
        Math.Max(0.0, 1.0 - 2.0 * Math.Sin(offDegrees * Math.PI / 360.0));
}
