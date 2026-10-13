#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect prefab (catalogue inspect.json): what a buildable prefab is before it stands anywhere (PrefabDescriptions):
/// how the cursor places and turns it, its cells, boxes, ports, top and controls; include small_cells adds each cell's
/// offset. prefab is a whole prefab name or a hash. Read only.
/// </summary>
internal static class InspectPrefab
{
    private const string SmallCells = "small_cells";

    internal static VerbOutcome Run(InspectPrefabOptions options)
    {
        PrefabRef? reference = options.Selector.Prefab?.Patterns[0] switch
        {
            PrefabPattern.Hash hash => new PrefabRef.Hashed(hash.Value),
            PrefabPattern.Whole whole => new PrefabRef.Named(whole.Name),
            _ => null
        };
        return reference == null
            ? new VerbOutcome.Refused(OptionProblem.Refusal(InspectPrefabOptions.Spec.Label,
                new[] { new OptionProblem("prefab", "is a whole prefab name or a hash, not parts with *") }))
            : Answer.Of(PrefabDescriptions.Describe(reference, options.Fields.Includes(SmallCells)));
    }
}
