#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// check ignition (catalogue check.json): without an id, the air at the player and every burnable thing they carry
/// (CarriedIgnition), with include prefabs every prefab's ignition temperatures; with an id, the contents of a tank,
/// canister or pipe network and their autoignition margin (ContentsIgnition), a list one row each. Read only.
/// </summary>
internal static class CheckIgnition
{
    private const string Prefabs = "prefabs";

    internal static VerbOutcome Run(CheckIgnitionOptions options)
    {
        string label = CheckIgnitionOptions.Spec.Label;
        return options.Selector.Ids is { } ids
            ? IdAnswers.Things(ids, label, id => ContentsIgnition.Read(id, label))
            : Answer.Of(CarriedIgnition.Read(options.Fields.Includes(Prefabs)));
    }
}
