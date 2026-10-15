#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find pad (catalogue find.json): every trader and rocket landing pad, measured by the game's own checks, which ships
/// fit and whether each contact in the sky could land now (LandingPads); {pads, count, total, offset, has_more}
/// (ListReply: the list whole, so offset 0, no limit and has_more false). Read only.
/// </summary>
internal static class FindPad
{
    internal static VerbOutcome Run(FindPadOptions options)
    {
        List<LandingPadView> pads = LandingPads.All();
        JsonSerializer serializer = ApiJson.Serializer;
        return new VerbOutcome.Answered(ListReply.Of("pads", pads, Paging.All,
            pad => pad));
    }
}
