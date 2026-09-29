#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The run tools' arguments: positions in world metres ([x, y, z] or {x, y, z}) snapped to the small-grid cell they
/// fall in (Grid3(position, 0.5, 0.25), as the game places a piece), runs as waypoints or cells, a single piece with
/// its ends, joins, removals and the guards' allowances.
/// </summary>
internal static class RunArgs
{
    internal const int MaximumPoints = 256;
    internal const int MaximumExtraEnds = 64;
    internal const int DefaultListLimit = 200;

    internal static GridCell CellOf(Vector3 position) =>
        PieceShapes.Cell(GridController.World.WorldToLocalGrid(position, SmallGrid.SmallGridSize,
            SmallGrid.SmallGridOffset));

    /// <summary>
    /// No world reaches this far from the origin (a planet's terrain is a few km across); a position beyond it would
    /// overflow the grid's cell numbers.
    /// </summary>
    internal const float MaximumCoordinate = 100000f;

    internal static Vector3 PositionOf(JToken token, string name)
    {
        Vector3? position = null;
        if (token is JArray array && array.Count == 3 && Number(array[0], out float x) &&
            Number(array[1], out float y) && Number(array[2], out float z))
        {
            position = new Vector3(x, y, z);
        }
        else if (token is JObject item && item["x"] != null && Number(item["x"]!, out float ox) &&
                 item["y"] != null && Number(item["y"]!, out float oy) && item["z"] != null &&
                 Number(item["z"]!, out float oz))
        {
            position = new Vector3(ox, oy, oz);
        }

        if (!position.HasValue)
        {
            throw ApiErrors.InvalidArgument($"{name} must be a position: [x, y, z] or {{x, y, z}} in metres.");
        }

        Vector3 found = position.Value;
        if (Mathf.Abs(found.x) > MaximumCoordinate || Mathf.Abs(found.y) > MaximumCoordinate ||
            Mathf.Abs(found.z) > MaximumCoordinate)
        {
            throw ApiErrors.InvalidArgument(
                $"{name} ({found.x}, {found.y}, {found.z}) is outside the world: no coordinate may be more than " +
                $"{MaximumCoordinate:0} m from the origin.");
        }

        return found;
    }

    private static bool Number(JToken token, out float value)
    {
        value = 0f;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
        {
            return false;
        }

        double number = token.Value<double>();
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            return false;
        }

        value = (float)number;
        return true;
    }

    internal static List<GridCell> Cells(Args args, string name)
    {
        JArray array = args.Array(name, MaximumPoints * 4);
        List<GridCell> cells = new List<GridCell>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            cells.Add(CellOf(PositionOf(array[index], $"{name}[{index}]")));
        }

        return cells;
    }

    internal const int MaximumPieces = 256;

    /// <summary>The run's cells from waypoints, cells, piece or pieces; null when none is given.</summary>
    internal static List<GridCell>? Run(Args args, List<ExtraEnd> extra)
    {
        int forms = (args.Has("waypoints") ? 1 : 0) + (args.Has("cells") ? 1 : 0) + (args.Has("piece") ? 1 : 0) +
                    (args.Has("pieces") ? 1 : 0);
        if (forms > 1)
        {
            throw ApiErrors.InvalidArgument("Pass one of waypoints, cells, piece or pieces.");
        }

        string? error;
        List<GridCell>? run;
        if (args.Has("waypoints"))
        {
            run = RunPath.FromWaypoints(Cells(args, "waypoints"), out error);
        }
        else if (args.Has("cells"))
        {
            run = RunPath.FromCells(Cells(args, "cells"), out error);
        }
        else if (args.Has("piece"))
        {
            run = Piece(args.OptionalObject("piece")!, "piece", extra, out error);
        }
        else if (args.Has("pieces"))
        {
            run = Pieces(args, extra, out error);
        }
        else
        {
            return null;
        }

        return run ?? throw ApiErrors.InvalidArgument(error ?? "The run is not valid.");
    }

    // pieces: [{at, ends}], each one piece as the piece form lays it.
    private static List<GridCell>? Pieces(Args args, List<ExtraEnd> extra, out string? error)
    {
        JArray items = args.Array("pieces", MaximumPieces);
        List<GridCell> cells = new List<GridCell>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            if (!(items[index] is JObject item))
            {
                error = $"pieces[{index}] must be {{at, ends}}.";
                return null;
            }

            List<GridCell>? one = Piece(item, $"pieces[{index}]", extra, out error);
            if (one == null)
            {
                return null;
            }

            cells.AddRange(one);
        }

        error = null;
        return cells;
    }

    private static List<GridCell>? Piece(JObject piece, string name, List<ExtraEnd> extra, out string? error)
    {
        Args fields = new Args(piece);
        GridCell cell = CellOf(PositionOf(piece["at"] ?? JValue.CreateNull(), $"{name}.at"));
        JArray ends = fields.Array("ends", 6);
        EndSet seen = EndSet.None;
        for (int index = 0; index < ends.Count; index++)
        {
            string? text = ends[index].Type == JTokenType.String ? (string?)ends[index] : null;
            if (!GridStep.TryParse(text, out GridStep step))
            {
                error = $"{name}.ends[{index}] must be one of +x, -x, +y, -y, +z, -z.";
                return null;
            }

            if (seen.Contains(step))
            {
                error = $"{name}.ends[{index}] repeats {step.Name}; name each end once.";
                return null;
            }

            seen = seen.With(step);
            extra.Add(new ExtraEnd(cell, step));
        }

        error = null;
        return new List<GridCell> { cell };
    }

    internal const int MaximumBranches = 16;

    /// <summary>
    /// The run with its branches ({waypoints} or {cells} each, and attach: the cell of the run or an earlier branch its
    /// last cell joins, by default the first found next to it).
    /// </summary>
    internal static RunShape Shape(Args args, List<GridCell> run)
    {
        if (args.Has("pieces"))
        {
            if (args.Has("branches"))
            {
                throw ApiErrors.InvalidArgument("branches go with waypoints or cells, not pieces.");
            }

            return RunShape.Pieces(run, out string? piecesError) ??
                   throw ApiErrors.InvalidArgument(piecesError ?? "The pieces are not valid.");
        }

        List<RunBranch> branches = new List<RunBranch>();
        if (args.Has("branches"))
        {
            if (args.Has("piece"))
            {
                throw ApiErrors.InvalidArgument("branches go with waypoints or cells, not piece.");
            }

            List<Args?> items = args.Objects("branches", MaximumBranches);
            for (int index = 0; index < items.Count; index++)
            {
                Args item = items[index] ??
                            throw ApiErrors.InvalidArgument($"branches[{index}] must be {{waypoints}} or {{cells}}.");
                if (item.Has("waypoints") == item.Has("cells"))
                {
                    throw ApiErrors.InvalidArgument($"branches[{index}] needs waypoints or cells (one of them).");
                }

                string? error;
                List<GridCell>? cells = item.Has("waypoints")
                    ? RunPath.FromWaypoints(Cells(item, "waypoints"), out error)
                    : RunPath.FromCells(Cells(item, "cells"), out error);
                JToken? attach = item.Optional("attach");
                branches.Add(new RunBranch(
                    cells ?? throw ApiErrors.InvalidArgument($"branches[{index}]: {error}"),
                    attach != null ? CellOf(PositionOf(attach, $"branches[{index}].attach")) : (GridCell?)null));
            }
        }

        return RunShape.Of(run, branches, out string? shapeError) ??
               throw ApiErrors.InvalidArgument(shapeError ?? "The branches are not valid.");
    }

    internal static List<ExtraEnd> ExtraEnds(Args args)
    {
        List<ExtraEnd> extra = new List<ExtraEnd>();
        if (!args.Has("extra_ends"))
        {
            return extra;
        }

        List<Args?> items = args.Objects("extra_ends", MaximumExtraEnds);
        for (int index = 0; index < items.Count; index++)
        {
            Args? item = items[index];
            JToken? at = item?.Optional("at");
            if (item == null || at == null || !GridStep.TryParse(item.OptionalString("toward"), out GridStep step))
            {
                throw ApiErrors.InvalidArgument(
                    $"extra_ends[{index}] must be {{at: [x, y, z], toward: \"+x\"|\"-x\"|\"+y\"|\"-y\"|\"+z\"|\"-z\"}}.");
            }

            extra.Add(new ExtraEnd(CellOf(PositionOf(at, $"extra_ends[{index}].at")), step));
        }

        return extra;
    }

    internal static JoinMode Join(Args args)
    {
        string join = (args.OptionalString("join") ?? "ends").Trim().ToLowerInvariant();
        return join switch
        {
            "ends" => JoinMode.Ends,
            "none" => JoinMode.None,
            "all" => JoinMode.All,
            _ => throw ApiErrors.InvalidArgument("join must be ends, none or all.")
        };
    }

    internal static Grade Grade(Args args, RunKind kind)
    {
        string? name = (args.OptionalString("grade") ?? kind.DefaultGrade)?.Trim().ToLowerInvariant();
        if (name == null)
        {
            throw ApiErrors.InvalidArgument(
                $"Argument 'grade' is required: one of {string.Join(", ", kind.GradeNames)}.");
        }

        return kind.GradeOf(name) ??
               throw ApiErrors.InvalidArgument($"grade must be one of {string.Join(", ", kind.GradeNames)}.");
    }

    /// <summary>
    /// The guards' allowances, the coil source, the list limit and the targets. allow_bridge entries and join_to are
    /// network handles (NetworkHandles): a network id, a piece or device id, or {reference_id, port}.
    /// </summary>
    internal static RunOptions Options(Args args, RunKind kind)
    {
        HashSet<long> bridge = NetworkHandles.ResolveAllowances(args, "allow_bridge", 64, kind.Family, kind.Bridges);
        ThingId? joinTo = args.Has("join_to") ? NetworkHandles.Resolve(args, "join_to", kind.Family) : (ThingId?)null;
        if (joinTo.HasValue)
        {
            // A handle naming nothing is taken as a network id: it must be one (network_not_found otherwise).
            kind.Family.NetworkMembers(joinTo.Value);
        }

        RunTargets targets = new RunTargets(args.OptionalThingId("root"), joinTo,
            args.OptionalBool("join_trunk") ?? false);
        return new RunOptions(new EditAllowance(bridge, args.OptionalBool("allow_split") ?? false),
            args.OptionalThingId("from_id"), args.OptionalBool("refund") ?? true,
            args.OptionalInt("limit", 1, RunPath.MaximumCells) ?? DefaultListLimit,
            args.OptionalBool("allow_split_long") ?? true, targets, args.OptionalBool("allow_door_keepout") ?? false);
    }
}
