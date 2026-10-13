#nullable enable

using System;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The jobs inspect job leaves to the MCP server (or sg): its list of runs with notices (no id), script runs (script-N)
/// and bulk writes (write-N). Refused in the hub-only form (D13), every such id at once.
/// </summary>
internal static class HubJobs
{
    private const string Taken = "taken by the MCP server (or sg), not by the game";

    internal static VerbError? Refusal(WhatSpec what, PerIdList? ids)
    {
        if (ids == null)
        {
            return OptionProblem.Refusal(what.Label,
                new[] { new OptionProblem("id", "left out (the runs with notices): " + Taken) });
        }

        System.Collections.Generic.List<OptionProblem> problems = new System.Collections.Generic.List<OptionProblem>();
        for (int index = 0; index < ids.Ids.Count; index++)
        {
            string id = ids.Ids[index].Trim();
            if (id.StartsWith("script-", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("write-", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new OptionProblem(ids.GivenAsList ? $"id[{index}]" : "id", $"{id}: {Taken}"));
            }
        }

        return problems.Count > 0 ? OptionProblem.Refusal(what.Label, problems) : null;
    }
}
