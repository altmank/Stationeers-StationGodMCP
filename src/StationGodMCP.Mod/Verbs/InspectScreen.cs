#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect screen (catalogue inspect.json): what a ScriptedScreens screen shows (ScreenReads): its surfaces, and one
/// surface's elements (surface; default the main screen's) kept by type, text and shown_only, paged (one id: with the
/// wire cursor a walk of the elements, KeyedPage); include style adds colours and fonts, image a PNG. The circuit
/// holder in the gateway's scope, by id or "looking_at". Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectScreen
{
    internal static VerbOutcome Run(InspectScreenOptions options)
    {
        int ids = options.Selector.Ids!.Ids.Count;
        if (KeyedPage.OneThing(options.Paging, ids) is { } problem)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(InspectScreenOptions.Spec.Label, new[] { problem }));
        }

        WalkRequest walk = KeyedPage.WalkOf(options.Paging, ids);
        DeviceScope scope = Devices.Scope(options.Gateway);
        ScreenQuery query = new ScreenQuery(options.Surface, options.Type, options.Text, options.ShownOnly == true,
            options.Fields.Includes("style"), options.Fields.Includes("image"));
        Paging paging = options.Paging;
        return IdAnswers.Things(options.Selector.Ids!, InspectScreenOptions.Spec.Label,
            id => ScreenReads.Read(CircuitHolders.Require(scope, id), query, walk, paging.Offset,
                paging.Limit ?? int.MaxValue));
    }
}
