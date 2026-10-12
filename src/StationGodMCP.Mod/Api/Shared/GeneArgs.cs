#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared;

/// <summary>plant_genes' genes argument: gene name to value.</summary>
internal static class GeneArgs
{
    /// <summary>
    /// The genes as given when every value is a number; otherwise the whole call is refused before anything is
    /// written, null included (the sidecar's schema refuses a string, so both reach the same refusal).
    /// </summary>
    internal static JObject RequireNumbers(JObject genes)
    {
        foreach (JProperty property in genes.Properties())
        {
            if (property.Value.Type != JTokenType.Integer && property.Value.Type != JTokenType.Float)
            {
                throw ApiErrors.InvalidArgument($"Gene {property.Name}: the value must be a number.");
            }
        }

        return genes;
    }

    /// <summary>Why one gene's value is not written: not finite, or outside the game's range without force.</summary>
    internal static GeneValueRefusal ValueRefusal(double value, bool force)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return GeneValueRefusal.NotFinite;
        }

        return !force && (value < GeneView.Minimum || value > GeneView.Maximum)
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
