#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// Reads acknowledge_gas_lost: left out is null; an empty or blank id is refused, not taken as left out. Null for a
/// method that does not take it (the cable and chute forms of the shared run, removal and swap readers).
/// </summary>
internal static class GasHoldArgs
{
    internal static string? Acknowledgement(Args args)
    {
        if (!args.Declares(GasHoldVerdict.AcknowledgeArgument))
        {
            return null;
        }

        string? acknowledge = args.OptionalString(GasHoldVerdict.AcknowledgeArgument);
        string? refusal = GasHoldRule.BlankRefusal(acknowledge);
        return refusal == null ? acknowledge : throw ApiErrors.InvalidArgument(refusal);
    }

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
