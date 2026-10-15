#nullable enable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find water (catalogue find.json): every canister, tank, device and pipe network holding at least min_mol of water,
/// polluted water and steam together, largest first (WaterSources); {sources, count, total, offset, has_more, totals,
/// min_mol, local_player} (ListReply: the list whole, so offset 0, no limit and has_more false). Read only.
/// </summary>
internal static class FindWater
{
    internal static VerbOutcome Run(FindWaterOptions options)
    {
        double minimum = options.MinMol ?? 0.0;
        WaterFound found = WaterSources.Collect(minimum);
        JsonSerializer serializer = ApiJson.Serializer;
        return new VerbOutcome.Answered(ListReply.Of("sources", found.Sources, Paging.All,
            source => JToken.FromObject(source, serializer),
            ReplyFields.Of(("totals", found.Totals), ("min_mol", minimum), ("local_player", found.LocalPlayer))));
    }
}
