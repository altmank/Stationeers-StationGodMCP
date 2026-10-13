#nullable enable

using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// How many things a deposit takes when at_most is left out (D-A3-10): a vault 256, a silo 64. A filter that keeps
/// more is cut there and the cut is noted (Truncations) with the most the store can take in one move: at_most's
/// 1000 for a vault, the silo's 600 entries (SiloRules.Capacity) for a silo.
/// </summary>
internal static class DepositLimits
{
    internal const int Vault = ReplyDefaults.VaultDepositItems;
    internal const int Silo = ReplyDefaults.SiloDepositItems;
    internal const int VaultMost = 1000;
    internal const int SiloMost = SiloRules.Capacity;

    internal static void NoteCut(int taken, int matched, int most) =>
        Truncations.Capped("items.results", taken, matched, "at_most", most);
}
