#nullable enable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find water (catalogue find.json): every canister, tank, device and pipe network holding at least min_mol of water,
/// polluted water and steam together, largest first (WaterSources); {sources, count, total, offset, has_more, totals,
/// min_mol, as_of, local_player} (ListReply: the list whole, so offset 0, no limit and has_more false; the writer
/// cuts it to 15 by default, x-default-limits). Which atmospheres hold water is a refreshed set (WaterSweep): as_of
/// is the game time it was read; each source's water is read now. Read only.
/// </summary>
[ReplyFact(FactRule.Refreshed, "which atmospheres and pipe networks hold water", Keeper = typeof(WorldModel.WaterSweep))]
[ReplyFact(FactRule.Live, "each source's water")]
internal static class FindWater
{
    internal static VerbOutcome Run(FindWaterOptions options)
    {
        double minimum = options.MinMol ?? 0.0;
        return WorkResult<WaterFound>.Of(answer => WaterSources.Finding(minimum, answer),
            found => ListReply.Of("sources", found.Sources, Paging.All, static source => source,
                ReplyFields.Of(("totals", found.Totals), ("min_mol", minimum), ("as_of", found.AsOfS),
                    ("local_player", found.LocalPlayer))));
    }
}
