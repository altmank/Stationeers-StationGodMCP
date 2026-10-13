#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find pad (catalogue find.json): every trader and rocket landing pad, measured by the game's own checks, which ships
/// fit and whether each contact in the sky could land now (LandingPads); {pads, count}. Read only.
/// </summary>
internal static class FindPad
{
    internal static VerbOutcome Run(FindPadOptions options)
    {
        List<LandingPadView> pads = LandingPads.All();
        return new VerbOutcome.Answered(ReplyFields.Of(("pads", pads), ("count", pads.Count)));
    }
}
