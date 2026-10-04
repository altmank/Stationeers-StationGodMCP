#nullable enable

namespace StationGodMCP.Pure;

/// <summary>What a network piece's own OnDestroy does to the networks of the pieces next to it (CODE).</summary>
internal enum NeighbourRebuild
{
    /// <summary>
    /// Only when the destroyed piece still had a network: Cable.OnDestroy and Pipe.OnDestroy keep the network they
    /// read before CableNetwork/PipeNetwork.Remove and rebuild the neighbours from it only when it was not null.
    /// </summary>
    WhenItHadANetwork,

    /// <summary>
    /// Always: Chute.OnDestroy calls networkedChute.ChuteNetwork.RebuildNetworkServer on every connected chute whether
    /// or not it had a network itself, and runs base.OnDestroy (Structure: Thing.OnDestroy, GridController.Deregister)
    /// only after that loop.
    /// </summary>
    Always,
}

/// <summary>
/// Whether a piece a removal takes away leaves its network before it is destroyed. Leaving first keeps the network's
/// id and, for pipes, every mole in what is left; it is only safe where the piece's OnDestroy then leaves its
/// neighbours alone. Where OnDestroy rebuilds every neighbour's network unconditionally (chutes), two neighbouring
/// pieces removed in one frame would each find the other already out of its network: the rebuild dereferences that
/// null network and throws, the rest of OnDestroy never runs, and the destroyed piece stays registered in its grid
/// cell, so its live neighbours keep a link to it. Such a piece is destroyed as a player's deconstruction destroys it,
/// in its network.
/// </summary>
internal static class NetworkLeaveRule
{
    internal static bool LeavesFirst(NeighbourRebuild rebuild, bool networkKeptWhole) =>
        networkKeptWhole && rebuild == NeighbourRebuild.WhenItHadANetwork;

    /// <summary>
    /// Whether a clean or upgrade swap's old piece leaves its network before it is destroyed. With replacements it
    /// always does: leaving hands each device registered through it over to them. With none (a clean removal) it leaves
    /// only where OnDestroy leaves the neighbours alone, as for a run's removal of a network kept whole.
    /// </summary>
    internal static bool SwapLeavesFirst(NeighbourRebuild rebuild, bool hasReplacements) =>
        hasReplacements || LeavesFirst(rebuild, networkKeptWhole: true);
}
