#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// network_snapshot without logic_types: a device that reads gases reads one Ratio logic type per gas and per port
/// (RatioOxygen, RatioNitrogenInput2, RatioLiquidOzoneOutput, ...), well over a hundred on a filtration unit, most of
/// them 0. A gas or liquid ratio that reads exactly 0 is left out unless include_zero_ratios; Ratio itself (a fill or
/// charge ratio) and the other *Ratio types (CompletionRatio, HorizontalRatio) always stay.
/// </summary>
internal static class ZeroRatios
{
    private const string Prefix = "Ratio";

    /// <summary>Whether a value of this logic type is a gas or liquid ratio reading 0.</summary>
    internal static bool Skips(string? logicType, double value) =>
        value == 0 && logicType != null && logicType.Length > Prefix.Length &&
        logicType.StartsWith(Prefix, StringComparison.Ordinal);
}
