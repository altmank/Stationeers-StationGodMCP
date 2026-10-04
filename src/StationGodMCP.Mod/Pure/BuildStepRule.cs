#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Raising a placed structure through its build states as a player's construction does (Structure.AttackWith): each
/// step to state n takes state n's entries (ToolEntry and ToolEntry2 with their quantities), tools used but not
/// consumed; a damaged structure cannot be built on, a broken one (state below 0) has no next state, and the last
/// state is complete.
/// </summary>
internal static class BuildStepRule
{
    /// <summary>What states current+1 to target take, per item, tools left out (MaterialRule's counting).</summary>
    internal static List<ItemCount> Cost(IReadOnlyList<IReadOnlyList<BuildEntry>> states, int current, int target)
    {
        List<IReadOnlyList<BuildEntry>> steps = new List<IReadOnlyList<BuildEntry>>(states.Count);
        for (int state = 0; state < states.Count; state++)
        {
            steps.Add(state > current && state <= target ? states[state] : new List<BuildEntry>());
        }

        return MaterialRule.Totals(steps, target);
    }

    /// <summary>The tools (by item key) states current+1 to target use, each once.</summary>
    internal static List<int> Tools(IReadOnlyList<IReadOnlyList<BuildEntry>> states, int current, int target)
    {
        List<int> tools = new List<int>();
        for (int state = current + 1; state <= target && state < states.Count; state++)
        {
            foreach (BuildEntry entry in states[state])
            {
                if (entry.IsTool && !tools.Contains(entry.Item))
                {
                    tools.Add(entry.Item);
                }
            }
        }

        return tools;
    }

    /// <summary>Why the structure cannot go from current to target; None when it can.</summary>
    internal static BuildStepRefusal Refusal(int current, int stateCount, int target, bool damaged) =>
        stateCount < 2 ? BuildStepRefusal.SingleState
        : current < 0 ? BuildStepRefusal.Broken
        : current >= stateCount - 1 ? BuildStepRefusal.Complete
        : target <= current || target > stateCount - 1 ? BuildStepRefusal.TargetOutOfRange
        : damaged ? BuildStepRefusal.Damaged
        : BuildStepRefusal.None;
}

/// <summary>Why a build step is refused, in the order the rule checks.</summary>
internal enum BuildStepRefusal
{
    None,
    SingleState,
    Broken,
    Complete,
    TargetOutOfRange,
    Damaged
}
