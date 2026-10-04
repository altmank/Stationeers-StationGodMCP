#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// A structure's build state: current of last (the last is complete; below 0 is the broken state), and what the next
/// state takes.
/// </summary>
internal sealed class BuildStateView
{
    internal BuildStateView(int current, int last, List<BuildNeedView>? next)
    {
        Current = current;
        Last = last;
        Complete = current >= last;
        Next = next;
    }

    public int Current { get; }

    public int Last { get; }

    public bool Complete { get; }

    /// <summary>What building it one state further takes; left out when complete or broken.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<BuildNeedView>? Next { get; }
}

/// <summary>An item a build step takes (quantity), or a tool it uses and keeps (tool true, no quantity).</summary>
internal sealed class BuildNeedView
{
    internal BuildNeedView(string prefabName, string displayName, int? quantity, bool tool)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        Quantity = quantity;
        Tool = tool;
    }

    public string PrefabName { get; }

    public string DisplayName { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Quantity { get; }

    public bool Tool { get; }
}

/// <summary>advance_build_state: the step asked for, what it takes and what the sources hold, and after a real run the state.</summary>
internal sealed class AdvanceBuildStateView
{
    internal AdvanceBuildStateView(bool dryRun, ThingView thing, int fromState, int toState, int lastState,
        List<BuildCostView> cost, List<BuildNeedView> tools, List<string> problems, BuildStateView? buildState)
    {
        DryRun = dryRun;
        Thing = thing;
        FromState = fromState;
        ToState = toState;
        LastState = lastState;
        Cost = cost;
        Tools = tools;
        Problems = problems;
        Ready = problems.Count == 0;
        BuildState = buildState;
    }

    public bool DryRun { get; }

    public ThingView Thing { get; }

    public int FromState { get; }

    public int ToState { get; }

    public int LastState { get; }

    public List<BuildCostView> Cost { get; }

    /// <summary>The tools those states use; not taken or checked.</summary>
    public List<BuildNeedView> Tools { get; }

    public bool Ready { get; }

    public List<string> Problems { get; }

    /// <summary>A real run: the build state read back after the step.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public BuildStateView? BuildState { get; }
}

/// <summary>One material a build step takes, how many the sources hold (null when free) and who pays.</summary>
internal sealed class BuildCostView
{
    internal BuildCostView(string prefabName, string displayName, int quantity, int? available,
        List<PaidByView>? paidBy)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        Quantity = quantity;
        Available = available;
        PaidBy = paidBy;
    }

    public string PrefabName { get; }

    public string DisplayName { get; }

    public int Quantity { get; }

    public int? Available { get; }

    /// <summary>With more than one source: what each pays.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PaidByView>? PaidBy { get; }
}
