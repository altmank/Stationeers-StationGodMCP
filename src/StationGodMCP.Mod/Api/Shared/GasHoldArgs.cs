#nullable enable

using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>Reads acknowledge_gas_lost: left out is null; an empty or blank id is refused, not taken as left out.</summary>
internal static class GasHoldArgs
{
    internal static string? Acknowledgement(Args args)
    {
        string? acknowledge = args.OptionalString(GasHoldVerdict.AcknowledgeArgument);
        string? refusal = GasHoldRule.BlankRefusal(acknowledge);
        return refusal == null ? acknowledge : throw ApiErrors.InvalidArgument(refusal);
    }
}
