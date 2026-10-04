#nullable enable

using StationGodMCP.Pure;

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
}
