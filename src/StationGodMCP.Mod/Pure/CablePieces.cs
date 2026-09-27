#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Which plain cable prefabs a cable coil places, from the prefab's own flags, kept free of game types (CODE).
///
/// A coil's one-cell pieces merge with other cables (Cable.BlockMergeWithOtherCables off). Its 3-, 5- and 10-long
/// straights block merging but are straight segments (Cable.IsStraight, StraightUnitLength above 0): the Cable Gun
/// (ItemCableGun, CableGun.CollectSegmentKinds) lays a run from exactly those constructables of the loaded coil,
/// longest first, each through MultiConstructor.Construct and charged its first build state's entry quantity. A plain
/// cable that blocks merging and is no straight segment is no coil piece. Whether a coil really lists a piece is
/// decided by the kit (Kit.Places), not here.
/// </summary>
internal static class CablePieces
{
    internal static bool IsCoilPiece(bool blocksMerge, bool isStraight, int straightUnitLength) =>
        !blocksMerge || (isStraight && straightUnitLength > 0);
}
