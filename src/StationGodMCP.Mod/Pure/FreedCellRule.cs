#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Whether removing a structure gives its cell back to the air as the game's hand deconstruction does. Only a structure
/// that fills its cell (StructureCollisionType BlockGrid, e.g. a finished frame) and still blocks air there
/// (BuildState.BlockAir, Structure.CanAirPass false) holds a cell without an atmosphere of its own. The game's last
/// deconstruction step sets the build state to -1 before OnServer.Destroy (Structure.AttackWith), so the structure
/// leaves the grid letting air pass and its cell is released (AtmosphericEventInstance.StructureReleaseGrid: an
/// atmosphere of its own, empty unless an open neighbour is outdoors). Destroyed at its finished state, the cell gets a
/// blocking event instead, which does nothing in a cell without an atmosphere, and it stays without one: planet air.
/// A face structure (a wall) never holds the cell's air, a broken one already lets air pass (build state below 0).
/// </summary>
internal static class FreedCellRule
{
    internal static bool Releases(bool fillsCell, bool blocksAir) => fillsCell && blocksAir;
}
