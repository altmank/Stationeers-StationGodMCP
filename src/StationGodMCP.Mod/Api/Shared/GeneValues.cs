#nullable enable

using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared;

/// <summary>The values write genes takes for a gene: finite, and within the game's range unless allowed out of it.</summary>
internal static class GeneValues
{
    /// <summary>Why one gene's value is not written: not finite, or outside the game's range unless allowOutOfRange.</summary>
    internal static GeneValueRefusal Refusal(double value, bool allowOutOfRange)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return GeneValueRefusal.NotFinite;
        }

        return !allowOutOfRange && (value < GeneView.Minimum || value > GeneView.Maximum)
            ? GeneValueRefusal.OutOfRange
            : GeneValueRefusal.None;
    }
}

/// <summary>Why a gene value is not written; None when it is.</summary>
internal enum GeneValueRefusal
{
    None,
    NotFinite,
    OutOfRange
}
