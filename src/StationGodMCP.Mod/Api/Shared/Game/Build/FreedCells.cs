#nullable enable

using System.Reflection;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// remove_structure gives a filled cell back as the last step of hand deconstruction does (FreedCellRule). The game
/// (Structure.AttackWith) sets CurrentBuildStateIndex to -1, then StructureDestroyed and OnServer.Destroy; once Unity
/// destroys it, OnDeregistered runs WorldChangeChecks, which, as the structure now lets air pass, queues
/// AtmosphericEventInstance.StructureReleaseGrid for each of its cells. A player's next step comes ticks later, so each
/// release is applied before the next structure goes: a cell is judged with the cells removed before it already holding
/// air and the ones removed after it still filled. That judgement (StructureReleaseGrid) makes the cell's atmosphere
/// empty unless an open neighbour has no atmosphere of its own and none has a room, which fills it with planet air.
/// Here the same: the build state goes to -1 through the game's setter (air state of the grid updated), the game's own
/// WorldChangeChecks queues the release with the structure still standing, and the queue is applied at once while the
/// job holds the tick. OnDeregistered later queues the release again, which then finds the atmosphere and does nothing.
/// </summary>
internal static class FreedCells
{
    internal static void Release(Structure piece)
    {
        if (!FreedCellRule.Releases(piece.StructureCollisionType == CollisionType.BlockGrid, !piece.CanAirPass))
        {
            return;
        }

        // Resolved before anything changes: a game without the method refuses the piece untouched (game_changed).
        MethodInfo worldChangeChecks = GameMembers.StructureWorldChangeChecks.Info;
        piece.CurrentBuildStateIndex = -1;
        worldChangeChecks.Invoke(piece, null);
        if (PipeGasQueue.CanRun())
        {
            AtmosphericsThread.Run(PipeGasQueue.ApplyQueued);
        }
    }
}
