#nullable enable

using Newtonsoft.Json.Linq;

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
}
