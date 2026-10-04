#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A structure's build state as the game holds it (Structure.CurrentBuildStateIndex of BuildStates) and what its next
/// state takes (BuildStates[current + 1].Tool, the entries a player's construction uses).
/// </summary>
internal static class BuildStates
{
    /// <summary>A structure with two or more build states; null for anything else.</summary>
    internal static BuildStateView? Of(Thing thing)
    {
        if (!(thing is Structure structure) || structure.BuildStates == null || structure.BuildStates.Count < 2)
        {
            return null;
        }

        int current = structure.CurrentBuildStateIndex;
        int last = structure.BuildStates.Count - 1;
        List<BuildNeedView>? next = current >= 0 && current < last ? Needs(structure, current, current + 1) : null;
        return new BuildStateView(current, last, next);
    }

    /// <summary>
    /// What list replies carry: current and last only, left out (null) when the structure stands complete;
    /// describe_device and advance_build_state give what the next state takes.
    /// </summary>
    internal static BuildStateView? Unfinished(Thing thing) =>
        thing is Structure structure && structure.BuildStates != null && structure.BuildStates.Count > 1 &&
        structure.CurrentBuildStateIndex < structure.BuildStates.Count - 1
            ? new BuildStateView(structure.CurrentBuildStateIndex, structure.BuildStates.Count - 1, null)
            : null;

    /// <summary>What states current+1 to target take: materials with their quantities, then the tools they use.</summary>
    internal static List<BuildNeedView> Needs(Structure structure, int current, int target)
    {
        Dictionary<int, Item> items = new Dictionary<int, Item>();
        List<IReadOnlyList<BuildEntry>> states = BuildMaterials.EntriesOf(structure, items);
        List<BuildNeedView> needs = new List<BuildNeedView>();
        foreach (ItemCount count in BuildStepRule.Cost(states, current, target))
        {
            needs.Add(NeedOf(items[count.Item], count.Quantity, false));
        }

        foreach (int tool in BuildStepRule.Tools(states, current, target))
        {
            needs.Add(NeedOf(items[tool], null, true));
        }

        return needs;
    }

    private static BuildNeedView NeedOf(Item item, int? quantity, bool tool) =>
        new BuildNeedView(item.PrefabName, Names.Of(item), quantity, tool);
}
