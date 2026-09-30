#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// What the family has around a run, read from the game into the layout's plain values: its pieces in the run's
/// cells and their neighbours (each as the connectivity model reads it, PieceShapes.Live), why a piece may not be
/// replaced, the devices there and their ends of the run's network type, and the run cells no piece of the kit may
/// take (PlacementCheck.CellBlocked). Things being removed by the same edit are treated as gone.
/// </summary>
internal static class RunSurvey
{
    internal static RunSurroundings Around(RunKind kind, Grade grade, IReadOnlyList<GridCell> run,
        IReadOnlyList<ExtraEnd> extra, SmallGridBlock mask, HashSet<long> ignore, Dictionary<long, SmallGrid> things)
    {
        RunSurroundings around = new RunSurroundings { SplitTool = kind.CleanTool };
        HashSet<GridCell> cells = new HashSet<GridCell>();
        foreach (GridCell cell in run)
        {
            cells.Add(cell);
            foreach (GridStep step in GridStep.All)
            {
                cells.Add(step.From(cell));
            }
        }

        foreach (ExtraEnd end in extra)
        {
            cells.Add(end.Step.From(end.Cell));
        }

        GridController world = GridController.World;
        HashSet<long> seen = new HashSet<long>();
        int type = kind.EndType(grade);
        foreach (GridCell cell in cells)
        {
            SmallCell? small = world.GetSmallCell(PieceShapes.Grid(cell));
            if (small == null)
            {
                continue;
            }

            SmallGrid? piece = kind.SlotOf(small);
            if (piece != null && !piece.IsBeingDestroyed && !ignore.Contains(piece.ReferenceId) &&
                seen.Add(piece.ReferenceId))
            {
                things[piece.ReferenceId] = piece;
                around.AddPiece(PieceShapes.Live(piece));
                string? reason = FixedReason(kind, piece);
                if (reason != null)
                {
                    around.Fixed[piece.ReferenceId] = reason;
                }
            }

            Device? device = small.Device;
            if (device != null && !device.IsBeingDestroyed && !ignore.Contains(device.ReferenceId) &&
                !kind.Family.IsMountedOn(device))
            {
                around.DeviceCells[cell] = device.ReferenceId;
                if (seen.Add(device.ReferenceId))
                {
                    things[device.ReferenceId] = device;
                    AddPorts(around, kind, device, type);
                }
            }
        }

        foreach (GridCell cell in run)
        {
            string? blocked = PlacementCheck.CellBlocked(PieceShapes.Grid(cell), mask, kind, ignore);
            if (blocked != null)
            {
                around.Blocked[cell] = blocked;
            }
        }

        return around;
    }

    /// <summary>The device's ends of the run's network type, as ports.</summary>
    internal static void AddPorts(RunSurroundings around, RunKind kind, Device device, int type)
    {
        if (device.OpenEnds == null)
        {
            return;
        }

        for (int index = 0; index < device.OpenEnds.Count; index++)
        {
            Connection end = device.OpenEnds[index];
            if (end?.Transform == null || ((int)end.ConnectionType & type) == 0)
            {
                continue;
            }

            around.Ports.Add(new DevicePort(device.ReferenceId, index,
                new PieceEnd(PieceShapes.Cell(end.GetLocalGrid()), PieceShapes.Cell(end.GetFacingGrid()),
                    (int)end.ConnectionType, (int)end.ConnectionRole), kind.Bridges(end)));
        }
    }

    // Why the piece may not be replaced by one with more ends; null when it may.
    internal static string? FixedReason(RunKind kind, SmallGrid piece)
    {
        if (kind.Family.GradeOf(piece) == null)
        {
            return $"{piece.PrefabName} is not a piece a coil or kit places";
        }

        if (!piece.IsStructureCompleted)
        {
            return $"{piece.PrefabName} is not fully built";
        }

        if (piece.Indestructable)
        {
            return $"{piece.PrefabName} is indestructible";
        }

        string? moving = Build.Rockets.MovingRefusal(piece);
        if (moving != null)
        {
            return $"{piece.PrefabName}: {moving}";
        }

        if (piece is Pipe burst && burst.IsBurst != Assets.Scripts.Networks.PipeBurst.None)
        {
            return $"{piece.PrefabName} is burst; repair it first";
        }

        SmallCell? cell = piece.SmallCell;
        Device? mounted = cell?.Device;
        return mounted != null && kind.Family.IsMountedOn(mounted)
            ? $"{mounted.PrefabName} {mounted.ReferenceId} is mounted on it"
            : kind.Holding(piece);
    }
}
