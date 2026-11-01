#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect prefab (catalogue inspect.json): what a prefab is before it stands anywhere (PrefabDescriptions.Inspect).
/// A buildable prefab gives how the cursor places and turns it, its cells, boxes, ports, top and controls; include
/// small_cells adds each cell's offset. Any other prefab gives its facts. A list or a pattern with * gives a row per
/// prefab, to compare a family. include data adds the prefab's game data and include recipes what makes it. Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "the prefab catalogues", Keeper = typeof(StationGodMCP.WorldModel.Prefabs))]
[ReplyFact(FactRule.Live, "the prefab's own values")]
internal static class InspectPrefab
{
    private const string SmallCells = "small_cells";

    private const string Data = "data";

    private const string Recipes = "recipes";

    [Profiled]
    internal static VerbOutcome Run(InspectPrefabOptions options) =>
        options.Selector.Prefab is { } filter
            ? Answer.Of(PrefabDescriptions.Inspect(filter, options.Fields.Includes(SmallCells),
                options.Fields.Includes(Data), options.Fields.Includes(Recipes)))
            : new VerbOutcome.Refused(OptionProblem.Refusal(InspectPrefabOptions.Spec.Label,
                new[] { new OptionProblem("prefab", "names no prefab") }));
}
