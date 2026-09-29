#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The game's small-grid placement rule, made here because the server runs none when a structure is built
/// (MultiConstructor.Construct and Constructor.SpawnConstruct check nothing; the cursor's SmallGrid.CanConstruct does,
/// CODE), and because a run must treat the pieces it replaces as gone, which the cursor cannot. The piece classes' own
/// rules first: an end entering an umbilical the way it faces (Cable, Pipe, Chute), a pipe under a pipe-mounted device
/// or into an in-line tank's slot (Pipe). Then for each cell the piece would take (GridBounds.GetLocalSmallGrid): a cell owned by a rocket refuses it;
/// every occupant (Cable, Device, Pipe, Chute, Rail, Other) collides when the two SmallCollisionType masks share a bit
/// (cables 2, pipes 1, devices and chutes most bits, SmallGridBlock), or when an end of one sits within 0.1 m of an
/// end of the other (SmallGrid.IsPipeEndCollision: a pipe and a cable along the same axis in one cell). Frames, walls
/// and other large-grid structures are never checked for pieces that are not DualRegister (cables and pipes are not).
/// </summary>
internal static class PlacementCheck
{
    private const float EndCollisionSquared = 0.010000001f;

    /// <summary>Why the prefab may not stand there; null when it may. Things in ignore are treated as gone.</summary>
    internal static string? Refusal(Structure prefab, Vector3 position, Quaternion rotation, HashSet<long> ignore)
    {
        SmallGrid? piece = prefab as SmallGrid;
        if (piece == null || prefab.GridBounds == null || !prefab.GridBounds.IsValid())
        {
            return $"{prefab.PrefabName} cannot be read";
        }

        List<Vector3>? ends = PieceShapes.EndPositions(prefab, position, rotation);
        GridController world = GridController.World;
        string? own = piece is Cable || piece is Pipe || piece is Chute
            ? UmbilicalRefusal(prefab, rotation, ends) ?? (piece is Pipe pipe ? PipeRefusal(pipe, position, rotation,
                ignore) : null)
            : null;
        if (own != null)
        {
            return own;
        }

        foreach (Grid3 grid in (Grid3[])prefab.GridBounds.GetLocalSmallGrid(position, rotation))
        {
            SmallCell? cell = world.GetSmallCell(grid);
            string? refusal = cell != null ? CellRefusal(cell, piece, ends, ignore) : null;
            if (refusal != null)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>
    /// Why no piece with the collision mask may stand in the cell whatever its turn (a rocket's cell, an occupant
    /// sharing a mask bit); null when some turn may. The family's own slot is not looked at (the layout joins it).
    /// </summary>
    internal static string? CellBlocked(SmallCell? cell, SmallGridBlock mask, RunKind kind, HashSet<long> ignore)
    {
        if (cell == null)
        {
            return null;
        }

        if (cell.Owner != null)
        {
            return "the cell belongs to a rocket";
        }

        SmallGrid? own = kind.SlotOf(cell);
        foreach (SmallGrid occupant in Occupants(cell, ignore))
        {
            if (occupant != own && (occupant.SmallCollisionType & mask) != SmallGridBlock.None)
            {
                return $"{Names.Of(occupant)} ({occupant.PrefabName} {occupant.ReferenceId}) stands there";
            }
        }

        return null;
    }

    /// <summary>
    /// The axes along which a piece in the cell may not have ends, because an occupant that does not collide by mask
    /// has an end there along that axis (bit 0 x, 1 y, 2 z).
    /// </summary>
    internal static int BlockedAxes(SmallCell? cell, SmallGridBlock mask, RunKind kind, HashSet<long> ignore)
    {
        if (cell == null)
        {
            return 0;
        }

        int axes = 0;
        SmallGrid? own = kind.SlotOf(cell);
        Vector3 centre = PieceShapes.CentreOf(PieceShapes.Cell(cell.SmallGrid));
        foreach (SmallGrid occupant in Occupants(cell, ignore))
        {
            if (occupant == own || (occupant.SmallCollisionType & mask) != SmallGridBlock.None ||
                occupant.OpenEnds == null)
            {
                continue;
            }

            foreach (Connection end in occupant.OpenEnds)
            {
                if (end?.Transform == null)
                {
                    continue;
                }

                Vector3 offset = end.Transform.position - centre;
                if (offset.sqrMagnitude > 0.25f)
                {
                    continue;
                }

                float x = Mathf.Abs(offset.x);
                float y = Mathf.Abs(offset.y);
                float z = Mathf.Abs(offset.z);
                axes |= x >= y && x >= z ? 1 : y >= z ? 2 : 4;
            }
        }

        return axes;
    }

    // Pipe.CanConstruct's own rules: a device mounted on a pipe in its origin cell takes only a straight pipe along it
    // and of its content (PlayerPlacementRule.PipeUnderMountedDevice), and no cell may hold a pipe-slot thing that is
    // not plain piping (an in-line tank, a passive vent: "Cannot merge with"). The run planners refuse such a cell
    // before this is asked (the tank or vent is a pipe they cannot change: cannot_change, no_kit or long_piece); this
    // stays as the backstop for every other caller (check_replaceable's neighbours, piece restores).
    private static string? PipeRefusal(Pipe pipe, Vector3 position, Quaternion rotation, HashSet<long> ignore)
    {
        GridController world = GridController.World;
        if (world.GetSmallCell(position)?.Device is DevicePipeMounted mounted && !mounted.IsBeingDestroyed &&
            !ignore.Contains(mounted.ReferenceId))
        {
            string? under = PlayerPlacementRule.PipeUnderMountedDevice(pipe.IsStraight,
                AxisOf(rotation * Vector3.forward), AxisOf(mounted.ThingTransform.forward),
                mounted.contentType == pipe.PipeContentType,
                $"{Names.Of(mounted)} ({mounted.PrefabName} {mounted.ReferenceId})");
            if (under != null)
            {
                return under;
            }
        }

        foreach (Grid3 grid in (Grid3[])pipe.GridBounds.GetLocalSmallGrid(position, rotation))
        {
            Pipe? other = world.GetSmallCell(grid)?.Pipe;
            if (other != null && !(other is Piping) && !other.IsBeingDestroyed && !ignore.Contains(other.ReferenceId))
            {
                return $"a pipe cannot merge with {Names.Of(other)} ({other.PrefabName} {other.ReferenceId})";
            }
        }

        return null;
    }

    // The axis a direction mostly runs along: 0 x, 1 y, 2 z (RocketMath.CompareVectors of the absolute forwards).
    private static int AxisOf(Vector3 direction)
    {
        float x = Mathf.Abs(direction.x);
        float y = Mathf.Abs(direction.y);
        float z = Mathf.Abs(direction.z);
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    // Cable, Pipe and Chute.CanConstruct: an end sitting in an umbilical's cell and pointing the umbilical's way is
    // refused (SmallGrid.IsConnectingToUmbilical: dot of the two forwards above 0.5).
    private static string? UmbilicalRefusal(Structure prefab, Quaternion rotation, List<Vector3>? ends)
    {
        List<Vector3>? forwards = PieceShapes.EndForwards(prefab, rotation);
        if (ends == null || forwards == null)
        {
            return null;
        }

        GridController world = GridController.World;
        for (int index = 0; index < ends.Count; index++)
        {
            Grid3 grid = world.WorldToLocalGrid(ends[index], SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
            if (world.GetSmallCell(grid)?.Device is IUmbilical umbilical &&
                Vector3.Dot(forwards[index], umbilical.AsThing.Transform.forward) > 0.5f)
            {
                return $"an end would enter {Names.Of(umbilical.AsThing)} ({umbilical.AsThing.ReferenceId}) the " +
                       "way it faces, which the game refuses (only the umbilical's own connector joins it)";
            }
        }

        return null;
    }

    private static string? CellRefusal(SmallCell cell, SmallGrid piece, List<Vector3>? ends, HashSet<long> ignore)
    {
        if (cell.Owner != null)
        {
            return "the cell belongs to a rocket";
        }

        foreach (SmallGrid occupant in Occupants(cell, ignore))
        {
            if (Collides(piece, ends, occupant))
            {
                return $"{Names.Of(occupant)} ({occupant.PrefabName} {occupant.ReferenceId}) is in the way";
            }
        }

        return null;
    }

    // SmallGrid._IsCollision and Cable._IsCollision, with the new piece's ends where it would stand.
    private static bool Collides(SmallGrid piece, List<Vector3>? ends, SmallGrid occupant)
    {
        if (piece is Cable cable && occupant is Cable other)
        {
            return other.CableType != cable.CableType || other.BlockMergeWithOtherCables ||
                   cable.BlockMergeWithOtherCables;
        }

        if ((occupant.SmallCollisionType & piece.SmallCollisionType) != SmallGridBlock.None)
        {
            return true;
        }

        if (ends == null || occupant.OpenEnds == null)
        {
            return ends == null;
        }

        foreach (Vector3 end in ends)
        {
            foreach (Connection theirs in occupant.OpenEnds)
            {
                if (theirs?.Transform != null &&
                    (end - theirs.Transform.position).sqrMagnitude < EndCollisionSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static List<SmallGrid> Occupants(SmallCell cell, HashSet<long> ignore)
    {
        SmallGrid?[] slots = { cell.Cable, cell.Device, cell.Pipe, cell.Chute, cell.Rail as SmallGrid, cell.Other };
        List<SmallGrid> occupants = new List<SmallGrid>(2);
        foreach (SmallGrid? slot in slots)
        {
            if (slot != null && !slot.IsBeingDestroyed && !ignore.Contains(slot.ReferenceId) &&
                !occupants.Contains(slot))
            {
                occupants.Add(slot);
            }
        }

        return occupants;
    }
}
