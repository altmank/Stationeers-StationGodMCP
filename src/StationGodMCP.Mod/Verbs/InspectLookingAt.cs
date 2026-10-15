#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect looking_at (catalogue inspect.json): what the local player's crosshair is on and where the look ray lands
/// in brief (LookingAt), the ray cast distance metres; include adds the player, the interactable, the camera view, the
/// full hit and a structure's body, or all of them. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectLookingAt
{
    internal static VerbOutcome Run(InspectLookingAtOptions options)
    {
        LookParts parts = LookParts.None;
        foreach (string word in options.Fields.Include)
        {
            if (LookingAt.PartWords.TryGetValue(word.Trim(), out LookParts part))
            {
                parts |= part;
            }
        }

        return Answer.Of(LookingAt.Read(parts, options.Distance!.Value, InspectLookingAtOptions.Spec.Label));
    }
}
