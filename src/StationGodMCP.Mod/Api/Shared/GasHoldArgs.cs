#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// Reads acknowledge_gas_lost as every verb that takes it does.
/// </summary>
internal static class GasHoldArgs
{
    /// <summary>A verb's acknowledge_gas_lost: an empty or blank id is a problem, not taken as left out.</summary>
    internal static void Check(string? acknowledge, List<OptionProblem> problems)
    {
        if (acknowledge != null && string.IsNullOrWhiteSpace(acknowledge))
        {
            problems.Add(new OptionProblem(GasHoldVerdict.AcknowledgeArgument, "is empty; pass the id of the job " +
                "whose gas check failed (run-N, upgrade-N, place-N, remove-N), or leave it out"));
        }
    }
}
