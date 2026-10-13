#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find water (catalogue find.json): every canister, tank, device and pipe network holding at least min_mol of water,
/// polluted water and steam together, largest first (WaterSources); {sources, count, totals, min_mol, local_player}.
/// Read only.
/// </summary>
internal static class FindWater
{
    internal static VerbOutcome Run(FindWaterOptions options)
    {
        double minimum = options.MinMol ?? 0.0;
        WaterFound found = WaterSources.Collect(minimum);
        return new VerbOutcome.Answered(ReplyFields.Of(("sources", found.Sources), ("count", found.Sources.Count),
            ("totals", found.Totals), ("min_mol", minimum), ("local_player", found.LocalPlayer)));
    }
}
