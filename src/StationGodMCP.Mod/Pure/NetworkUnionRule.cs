#nullable enable

namespace StationGodMCP.Pure;

/// <summary>Which of two pipe networks a job's merge keeps.</summary>
internal enum NetworkUnion
{
    /// <summary>The replaced piece's network takes the new pieces' network in and keeps its id.</summary>
    IntoKept,

    /// <summary>The new pieces' network takes the replaced piece's network in.</summary>
    IntoTheirs,
}

/// <summary>
/// When a job builds new pieces over an old one (a changed run cell, a split long straight's singles) and the new
/// pieces stand in another network than the old piece, the job merges the two on the host. No client is told of that
/// merge: a client puts each new piece into the network its host id names, unless it has connected neighbours there,
/// in which case it takes their network (Pipe.DeserializeOnJoin: StructureNetwork.Merge of ConnectedNetworks). Keeping
/// the old network is safe only when the new pieces' network holds nothing but the new pieces: the client then puts
/// them where the host did. When it also holds pieces that stood before, the client leaves those where they were and
/// puts the new pieces with them, so the host must keep that network too; otherwise the client loses the old network
/// with its last piece while the host goes on sending its state.
/// </summary>
internal static class NetworkUnionRule
{
    internal static NetworkUnion For(bool theirsHoldsOnlyNewPieces) =>
        theirsHoldsOnlyNewPieces ? NetworkUnion.IntoKept : NetworkUnion.IntoTheirs;
}
