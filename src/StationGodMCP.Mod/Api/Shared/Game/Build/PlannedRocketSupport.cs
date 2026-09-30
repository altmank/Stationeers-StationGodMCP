#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Objects.Electrical;
using Objects.Rockets;
using Objects.Structures;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// A rocket is built bottom up in one request: the launch mount on its frames, the engine fuselage on the mount, the
/// fuselage on that, the internals in the fuselage's cells, the umbilical in its tower. The preflight sees only what
/// stands, so each of these would be refused for the earlier placement it stands on. Where the game's refusal is
/// exactly the one an earlier placement of the request clears, that placement is named as the support and the job's
/// own check before each build (PlacePlanner.Recheck, once the earlier ones stand) decides, as for a frame below. Each
/// rule is read structurally from the class's CanConstruct, whose first check it is:
/// <list type="bullet">
/// <item>LaunchMount: fewer than 4 support frames under its pillars (LaunchMount.cs:198-204, CountSupportFrames
/// 229-245), and the earlier placements add enough.</item>
/// <item>EngineFuselage: no fully built ISupportsRocketConstruction in the cell below (EngineFuselage.cs:44-49); an
/// earlier placement puts one there at its last build state.</item>
/// <item>Fuselage, NoseCone: no Fuselage or EngineFuselage in the cell below (Fuselage.cs:42-48, NoseCone.cs:40-46).</item>
/// <item>A small-grid piece refused by a rocket rule (SmallGrid.cs:668-712, RocketEngineBase.cs:751-761): an earlier
/// fuselage piece's internal cells (position + offset * 0.5, not turned: RocketNetwork.GetInternalCell) cover each of
/// its cells no rocket owns now, with a type that fits it.</item>
/// <item>A male umbilical: no Rocket Tower in its 2 m cell (RocketGasUmbilicalMale.cs:143-157 and the power, chute and
/// crew ones).</item>
/// <item>RocketTower: nothing that allows mounting in the cell below (RocketTower.cs:7-15).</item>
/// </list>
/// </summary>
internal static class PlannedRocketSupport
{
    /// <summary>The earlier placement the refusal waits for, and what the game wants of it; null when none clears it.</summary>
    internal static PlannedPlacement? Find(PlacePlan plan, PlannedPlacement placement, Vector3 position,
        string refusal, out string? need)
    {
        need = null;
        Structure prefab = placement.Prefab!;
        Quaternion rotation = placement.Rotation;
        GridCell below = LargeCells.Containing(Bodies.V(position + Vector3.down * 2f));
        switch (prefab)
        {
            case LaunchMount mount:
                need = "a support frame under each of its 4 pillars";
                return Pillars(plan, placement, mount, position, rotation);
            case EngineFuselage _:
                need = "a fully built launch mount below it";
                return StandsBelow(position, IsCompletedMount)
                    ? null
                    : Earlier(plan, placement, earlier => InCell(earlier, below) &&
                                                          earlier.Prefab is ISupportsRocketConstruction &&
                                                          earlier.State == earlier.Prefab.BuildStates.Count - 1);
            case Fuselage _:
            case NoseCone _:
                need = "a fuselage or engine fuselage below it";
                return StandsBelow(position, structure => structure is Fuselage || structure is EngineFuselage)
                    ? null
                    : Earlier(plan, placement, earlier => InCell(earlier, below) &&
                                                          (earlier.Prefab is Fuselage ||
                                                           earlier.Prefab is EngineFuselage));
            case RocketTower tower:
                need = "a frame or launch tower below it";
                GridCell under = LargeCells.Containing(Bodies.V(position - rotation * Vector3.up * tower.GridSize));
                return MountableAt(position - rotation * Vector3.up * tower.GridSize)
                    ? null
                    : Earlier(plan, placement, earlier => InCell(earlier, under) && earlier.Prefab!.AllowMounting &&
                                                          !(earlier.Prefab is SmallGrid));
            case RocketGasUmbilicalMale _:
            case RocketPowerUmbilicalMale _:
            case RocketChuteUmbilicalMale _:
            case RocketCrewUmbilical _:
                need = "a Rocket Tower in its 2 m cell";
                GridCell own = LargeCells.Containing(Bodies.V(position));
                return TowerIn(position)
                    ? null
                    : Earlier(plan, placement, earlier => InCell(earlier, own) && earlier.Prefab is RocketTower);
            case SmallGrid piece when IsRocketRefusal(piece, placement.Cursor!, refusal):
                need = "a fuselage piece whose internal cells take it";
                return Hull(plan, placement, piece, position, rotation);
            default:
                return null;
        }
    }

    // The game's rocket refusals of a small-grid piece, as the cursor check words them.
    private static bool IsRocketRefusal(SmallGrid piece, Structure cursor, string refusal) =>
        refusal == CursorCheck.Text(GameStrings.CannotPlaceOutsideRocket.DisplayString, string.Empty) ||
        refusal == CursorCheck.Text(GameStrings.PlacementConnectingNeedsFuselage.DisplayString, string.Empty) ||
        (piece is IRocketEngine &&
         refusal == CursorCheck.Text(GameStrings.RocketEnginePlacementRule.AsString(cursor.ToTooltip()), string.Empty));

    // Every small cell the piece takes is owned by a rocket now or covered by an earlier fuselage piece's internal
    // cell whose type fits it; the last such piece is the support. None when a cell is covered by neither.
    private static PlannedPlacement? Hull(PlacePlan plan, PlannedPlacement placement, SmallGrid piece,
        Vector3 position, Quaternion rotation)
    {
        int pieceType = piece is IRocketInternals internals ? (int)internals.InternalCellType : 0;
        PlannedPlacement? support = null;
        foreach (Grid3 grid in CursorCheck.SmallCells(piece, position, rotation))
        {
            if (Rockets.OwnerOf(grid) != null)
            {
                continue;
            }

            PlannedPlacement? covering = null;
            foreach (PlannedPlacement earlier in EarlierOnes(plan, placement))
            {
                if (earlier.Prefab is StructureFuselage hull && earlier.Position is { } at &&
                    CellTypeAt(hull, at, grid) is { } cellType && RocketCellRule.Fits((int)cellType, pieceType))
                {
                    covering = earlier;
                }
            }

            if (covering == null)
            {
                return null;
            }

            support = covering;
        }

        return support;
    }

    // The type a fuselage piece standing at `at` would give the small cell (RocketNetwork.GetInternalCell,
    // RocketNetwork.cs:194-197); null when none of its internal cells is that one.
    private static RocketInternalCellType? CellTypeAt(StructureFuselage hull, Vector3 at, Grid3 grid)
    {
        foreach (RocketInternalCellOffset offset in hull.InternalCellOffsets ?? new List<RocketInternalCellOffset>())
        {
            if (new Grid3(at + offset.Offset * 0.5f) == grid)
            {
                return offset.CellType;
            }
        }

        return null;
    }

    // LaunchMount.CountSupportFrames with the planned turn: the four points around its pillars, each counted when a
    // standing structure there allows mounting, or an earlier placement puts one there. Null unless the standing ones
    // are too few and the earlier ones make up the difference.
    private static PlannedPlacement? Pillars(PlacePlan plan, PlannedPlacement placement, LaunchMount mount,
        Vector3 position, Quaternion rotation)
    {
        if (mount.IsOrbital)
        {
            return null;
        }

        Vector3 right = rotation * Vector3.right;
        Vector3 forward = rotation * Vector3.forward;
        Vector3 centre = position + forward * 0.99f + rotation * Vector3.up * 0.01f;
        float size = mount.GridSize;
        Vector3[] points = { centre - right * size, centre + right * size, centre - forward * size, centre + forward * size };
        int standing = 0;
        PlannedPlacement? last = null;
        int planned = 0;
        foreach (Vector3 point in points)
        {
            if (MountableAt(point))
            {
                standing++;
                continue;
            }

            GridCell cell = LargeCells.Containing(Bodies.V(point));
            PlannedPlacement? earlier = Earlier(plan, placement, other => InCell(other, cell) &&
                                                                         other.Prefab!.AllowMounting &&
                                                                         !(other.Prefab is SmallGrid));
            if (earlier != null)
            {
                planned++;
                last = earlier;
            }
        }

        return standing < 4 && standing + planned >= 4 ? last : null;
    }

    private static bool MountableAt(Vector3 point)
    {
        Structure? structure = GridController.World.Get<Structure>(point, StructureElement.Center);
        return structure != null && !structure.IsBeingDestroyed && structure.AllowMounting;
    }

    private static bool StandsBelow(Vector3 position, System.Func<Structure, bool> fits)
    {
        Structure? structure = GridController.World.Get<Structure>(new WorldGrid(new Grid3(position) + Grid3.Down));
        return structure != null && !structure.IsBeingDestroyed && fits(structure);
    }

    private static bool IsCompletedMount(Structure structure) =>
        structure is ISupportsRocketConstruction && structure.IsStructureCompleted;

    private static bool TowerIn(Vector3 position)
    {
        List<Structure>? all = GridController.World.GetCell(new WorldGrid(position))?.AllStructures;
        return all != null && all.Exists(structure => structure is RocketTower && !structure.IsBeingDestroyed);
    }

    private static bool InCell(PlannedPlacement placement, GridCell cell) =>
        placement.Position is { } at && LargeCells.Containing(Bodies.V(at)).Equals(cell);

    // The last earlier placement of the request that matches.
    private static PlannedPlacement? Earlier(PlacePlan plan, PlannedPlacement placement,
        System.Func<PlannedPlacement, bool> matches)
    {
        PlannedPlacement? found = null;
        foreach (PlannedPlacement earlier in EarlierOnes(plan, placement))
        {
            if (matches(earlier))
            {
                found = earlier;
            }
        }

        return found;
    }

    private static IEnumerable<PlannedPlacement> EarlierOnes(PlacePlan plan, PlannedPlacement placement)
    {
        foreach (PlannedPlacement earlier in plan.Placements)
        {
            if (ReferenceEquals(earlier, placement))
            {
                yield break;
            }

            if (earlier.Prefab != null && earlier.Position.HasValue)
            {
                yield return earlier;
            }
        }
    }
}
