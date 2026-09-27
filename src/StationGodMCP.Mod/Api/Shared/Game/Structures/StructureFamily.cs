#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// What one kind of large structure means to a swap: which things are its pieces, which of those a swap may touch,
/// which pieces a room names, what a piece's replacement defaults to, which cells and rooms a swap affects, and the
/// checks only this kind needs. WallFamily and FrameFamily.
/// </summary>
internal abstract class StructureFamily
{
    internal abstract string Tool { get; }

    /// <summary>wall or frame, for messages and codes (not_a_wall).</summary>
    internal abstract string Noun { get; }

    internal abstract bool TargetRequired { get; }

    /// <summary>The classes a swap takes and builds, for messages.</summary>
    internal abstract string PlainClasses { get; }

    /// <summary>A piece of this kind at all (special subclasses too, which are kept as special_piece).</summary>
    internal abstract bool IsMember(Thing thing);

    /// <summary>Exactly the classes a swap may take away or build.</summary>
    internal abstract bool IsPlain(Structure structure);

    /// <summary>Adds the room's pieces of this kind (once each, by reference id).</summary>
    internal abstract void CollectInRoom(Room room, List<Structure> into, HashSet<long> seen);

    /// <summary>The target when the request names none; null when the family needs one named.</summary>
    internal abstract Structure? DefaultTarget(Structure old);

    /// <summary>Every cell whose room or air the swap can touch.</summary>
    internal abstract List<GridPoint> AffectedCells(PlannedStructureSwap swap);

    /// <summary>The checks only this family needs; they add problems to the plan or detail to the swap.</summary>
    internal abstract void Assess(PlannedStructureSwap swap, StructureSwapPlan plan);

    protected static void AddOnce(Structure structure, List<Structure> into, HashSet<long> seen)
    {
        if (structure != null && !structure.IsCursor && seen.Add(structure.ReferenceId))
        {
            into.Add(structure);
        }
    }
}
