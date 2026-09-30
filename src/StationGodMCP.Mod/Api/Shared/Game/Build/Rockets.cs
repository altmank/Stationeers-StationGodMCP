#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Networks;
using Objects.Rockets;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// A rocket as the build tools meet it. The game builds rocket parts exactly as it builds anything else (the cursor's
/// CanConstruct, then Constructor.SpawnConstruct on the world grid, CreateStructureInstance.cs:35-43): a fuselage piece
/// joins or starts a RocketNetwork when it registers (StructureFuselage.OnRegistered, StructureFuselage.cs:75-89), which
/// takes ownership of its internal small cells with their RocketInternalCellType (RocketNetwork.AddSmallCellOwnership,
/// RocketNetwork.cs:126-146); a small-grid piece placed in such a cell is announced to the rocket
/// (GridController.cs:736 Owner.OnGridPlaced) and adopted when the rocket refreshes (RocketNetwork.RegenerateCollections
/// / TryAdopt: Internals, RocketData, engines, batteries, mass; RocketNetwork.cs:256-382). So the tools add no
/// registration of their own; they only ask the game's rules the cursor asks.
/// </summary>
internal static class Rockets
{
    /// <summary>The rocket network a standing structure is part of: a fuselage piece's own, an internal's; else null.</summary>
    internal static RocketNetwork? NetworkOf(Structure structure) => structure switch
    {
        StructureFuselage hull => hull.RocketNetwork,
        IRocketInternals { RocketNetwork: { } network } => network,
        _ => null
    };

    /// <summary>The rocket network that owns the small cell; null when none does.</summary>
    internal static RocketNetwork? OwnerOf(Grid3 grid) => GridController.World.GetSmallCell(grid)?.Owner as RocketNetwork;

    /// <summary>Why the rocket may not be built on or taken from now (RocketMotionRule); null when it may.</summary>
    internal static string? MovingRefusal(RocketNetwork? network)
    {
        Rocket? rocket = network?.Rocket;
        return rocket != null &&
               (rocket.RocketState == RocketState.Launching || rocket.RocketState == RocketState.Landing)
            ? RocketMotionRule.Refusal(rocket.DisplayName, rocket.RocketState.ToString())
            : null;
    }

    /// <summary>MovingRefusal for the rocket the structure is part of.</summary>
    internal static string? MovingRefusal(Structure structure) => MovingRefusal(NetworkOf(structure));

    /// <summary>
    /// MovingRefusal for a new placement: the rocket owning any of its small cells, and for a fuselage piece the rocket
    /// of the fuselage piece it would stand on (the 2 m cell below, as Fuselage.CanConstruct reads it).
    /// </summary>
    internal static string? MovingRefusalAt(Structure prefab, Vector3 position, Quaternion rotation)
    {
        foreach (Grid3 grid in CursorCheck.SmallCells(prefab, position, rotation))
        {
            string? refusal = MovingRefusal(OwnerOf(grid));
            if (refusal != null)
            {
                return refusal;
            }
        }

        if (!(prefab is StructureFuselage))
        {
            return null;
        }

        Structure? below = GridController.World.Get<Structure>(new WorldGrid(new Grid3(position) + Grid3.Down));
        return below is StructureFuselage hull ? MovingRefusal(hull.RocketNetwork) : null;
    }

    /// <summary>
    /// The RocketInternalCellType the rocket gave the small cell (the first fuselage piece to claim it keeps it,
    /// RocketNetwork.cs:132-145); None when the rocket does not own it.
    /// </summary>
    internal static RocketInternalCellType CellTypeOf(RocketNetwork network, Grid3 grid) =>
        GameMembers.RocketSmallCells.GetValue(network) is Dictionary<Grid3, RocketOccupiedCell> cells &&
        cells.TryGetValue(grid, out RocketOccupiedCell cell) && cell != null
            ? cell.CellType
            : RocketInternalCellType.None;

    /// <summary>
    /// SmallGrid.CanConstruct's rocket rules that come before its collision checks (SmallGrid.cs:668-682): a strictly
    /// internal piece only in a rocket's cell; a piece that may be fitted in a rocket (IRocketInternals: cables, pipes,
    /// chutes and many devices) not in a free cell right above or below a rocket's cell. Null when neither refuses.
    /// </summary>
    internal static string? PlacementRefusal(SmallGrid piece, Grid3 grid)
    {
        GridController world = GridController.World;
        SmallCell? cell = world.GetSmallCell(grid);
        if (piece is IRocketInternals { StrictlyInternal: true } && !(cell?.Owner is RocketNetwork))
        {
            return CursorCheck.Text(GameStrings.CannotPlaceOutsideRocket.DisplayString, "cannot place outside a rocket");
        }

        return cell?.Owner == null && piece is IRocketInternals && NextToRocket(world, grid)
            ? ConnectingNeedsFuselage
            : null;
    }

    /// <summary>
    /// SmallGrid.CanConstruct's last rocket rule (SmallGrid.cs:709-712): a rocket's cell takes the piece only when its
    /// type holds the piece's (RocketNetwork.IsCollision, asked of the game). Null when it does or no rocket owns it.
    /// </summary>
    internal static string? CollisionRefusal(SmallGrid piece, Grid3 grid)
    {
        SmallCell? cell = GridController.World.GetSmallCell(grid);
        return cell?.Owner != null && cell.Owner.IsCollision(piece, cell.SmallGrid) ? ConnectingNeedsFuselage : null;
    }

    /// <summary>
    /// The same rules for a whole kind of piece in a cell, whatever its turn (the route planners' blocked cells): a
    /// rocket's cell whose type lacks the kind's (RocketCellRule.Fits), or a free cell right above or below a rocket's
    /// cell (cables, pipes and chutes are all IRocketInternals). Null when a piece of the kind may stand there.
    /// </summary>
    internal static string? KindRefusal(Grid3 grid, RocketInternalCellType kindType)
    {
        GridController world = GridController.World;
        SmallCell? cell = world.GetSmallCell(grid);
        if (cell?.Owner is RocketNetwork network)
        {
            RocketInternalCellType cellType = CellTypeOf(network, grid);
            return RocketCellRule.Fits((int)cellType, (int)kindType)
                ? null
                : $"the rocket's cell here is a {cellType} cell, which takes no {kindType} " +
                  $"({ConnectingNeedsFuselage})";
        }

        return cell?.Owner == null && NextToRocket(world, grid) ? ConnectingNeedsFuselage : null;
    }

    // The small cells right above and below (SmallGrid.cs:674-681: y +- SmallGridSize * 10 in Grid3's decimetres).
    private static bool NextToRocket(GridController world, Grid3 grid)
    {
        Grid3 above = new Grid3(grid.x, grid.y + SmallGrid.SmallGridSize * 10f, grid.z);
        Grid3 below = new Grid3(grid.x, grid.y - SmallGrid.SmallGridSize * 10f, grid.z);
        return world.GetSmallCell(above)?.Owner != null || world.GetSmallCell(below)?.Owner != null;
    }

    private static string ConnectingNeedsFuselage =>
        CursorCheck.Text(GameStrings.PlacementConnectingNeedsFuselage.DisplayString,
            "placement that connects to a rocket needs to be inside a fuselage or via an umbilical");

    /// <summary>
    /// A fuselage piece's last deconstruction step (StructureFuselage.CanDeconstruct at build state 0,
    /// StructureFuselage.cs:294-317), asked whatever state it stands at because remove_structure takes it down in one
    /// go: a fuselage piece on top of it, then any small-grid piece in one of its internal cells, each unless the same
    /// request removes it. Null when neither stops it.
    /// </summary>
    internal static string? FuselageTakedownRefusal(StructureFuselage hull, HashSet<long> removed)
    {
        GridController world = GridController.World;
        Structure? above = hull.GridController.Get<Structure>(
            new WorldGrid(new Grid3(hull.ThingTransformPosition) + Grid3.Up));
        bool aboveStays = above is StructureFuselage && above != hull && !above.IsBeingDestroyed &&
                          !removed.Contains(above.ReferenceId);
        bool internalsStay = false;
        foreach (RocketInternalCellOffset offset in hull.InternalCellOffsets ?? new List<RocketInternalCellOffset>())
        {
            SmallCell? cell = world.GetSmallCell(new Grid3(hull.Transform.position + offset.Offset * 0.5f));
            if (cell != null && cell.IsValid() && Stays(removed, cell.Device, cell.Chute, cell.Pipe, cell.Cable,
                    cell.Other))
            {
                internalsStay = true;
                break;
            }
        }

        return FuselageTakedownRule.Judge(aboveStays, internalsStay) switch
        {
            FuselageTakedown.SupportsAnother => CursorCheck.Text(
                GameStrings.InvalidFuselageDeconstruct.AsString(above!.DisplayName),
                "it supports another fuselage piece"),
            FuselageTakedown.HoldsInternals => CursorCheck.Text(
                GameStrings.RocketFuselageHasInternals.DisplayString, "it holds internal components"),
            _ => null
        };
    }

    private static bool Stays(HashSet<long> removed, params SmallGrid?[] pieces)
    {
        foreach (SmallGrid? piece in pieces)
        {
            if (piece != null && !piece.IsBeingDestroyed && !removed.Contains(piece.ReferenceId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The fuselage piece a placement would replace (the cursor's StructureFuselage.CanMerge where it stands: a
    /// different fuselage piece of its own family in the cell, Fuselage.cs:12-25, EngineFuselage.cs:17-26,
    /// NoseCone.cs:14-23), found as the merge kit finds it (MultiMergeConstructor.cs:77); null for any other placement.
    /// Ask it with the cursor moved to the spot (CursorCheck.At).
    /// </summary>
    internal static StructureFuselage? Replaced(Structure cursor) =>
        cursor is StructureFuselage fuselage && fuselage.CanMerge()
            ? fuselage.GridController.Get<StructureFuselage>(new Grid3(fuselage.ThingTransformPosition),
                StructureElement.Center)
            : null;
}
