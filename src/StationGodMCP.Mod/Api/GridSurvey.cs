#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Structures;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// grid_survey: the 2 m cells of a box (min and max corners in metres) or of a room (room_id), one page at a time:
/// each cell's frame, the face structures on its six faces, its room, and its 64 small cells as one string
/// (SmallCellCode) and by what supports them (CellSupports); then the cables, pipes, chutes and devices standing in the page's cells, device ports with the
/// cell a piece joins them from, and the networks of the pieces listed; chute pieces with the way items move through
/// them (ChuteFlow) and what rides in them. Read only.
/// </summary>
internal static class GridSurveyApi
{
    private const int DefaultLimit = 27;
    private const int MaximumLimit = 125;
    private const long MaximumCells = 20000;

    internal const string Legend =
        "Each cell is a 2 m cell at its centre (odd metres). small: its 64 small-grid cells (0.5 m) at -1, -0.5, 0 " +
        "and +0.5 m from the centre along each axis (index 0 to 3; index 0 lies on the cell's minimum face plane, " +
        "shared with the neighbour), character index x + 4y + 16z. '.' empty, 'c' cable, 'p' pipe, 'b' cable and " +
        "pipe, 'h' chute, 'd' device, 'o' another small-grid thing (a mounted item, a rail), 'r' a rocket's cell. " +
        "Frames and walls never block cables or pipes; a device, chute or 'o' blocks both; a pipe blocks a cable " +
        "(and a cable a pipe) only along the axis its ends lie on. A chute needs a cell with no cable, pipe, device, " +
        "chute or 'o'. support: the same 64 cells by what holds a piece there up: 'e' a frame edge or corner, 'f' on " +
        "or inside a frame (a frame's top face is the minimum plane of the cell above it), 'w' on a wall's plane, " +
        "'a' air (plan_*_route frames_first avoids 'a' cells).";

    internal static GridSurveyView Handle(Args args)
    {
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        List<GridCell> cells = Cells(args);
        Slice<GridCell> slice = Slice<GridCell>.Of(cells, page);
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        List<SurveyCellView> views = new List<SurveyCellView>(slice.Items.Count);
        foreach (GridCell cell in slice.Items)
        {
            views.Add(CellView(facts, cell));
        }

        SurveyContents contents = Contents(facts, slice.Items, args.OptionalBool("include_networks") ?? true);
        return new GridSurveyView(Slice<SurveyCellView>.Page(views, page, cells.Count), contents, Legend);
    }

    private static List<GridCell> Cells(Args args)
    {
        if (args.Has("room_id") == (args.Has("min") || args.Has("max")))
        {
            throw ApiErrors.InvalidArgument("Pass min and max (a box, in metres) or room_id (from rooms).");
        }

        if (args.Has("room_id"))
        {
            if (!ThingId.TryRead(args.Optional("room_id"), out ThingId id))
            {
                throw ApiErrors.InvalidArgument("Argument 'room_id' must be a room id as rooms reports it.");
            }

            Room room = StructureAirRecord.FindRoom(id.Value) ??
                        throw ApiErrors.Refused("room_not_found", $"No room has id {id}.");
            List<GridCell> cells = new List<GridCell>();
            foreach (WorldGrid grid in new List<WorldGrid>(room.Grids))
            {
                cells.Add(PieceShapes.Cell(grid.Value));
            }

            cells.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
            return cells;
        }

        GridCell min = RunArgs.CellOf(RunArgs.PositionOf(args.Optional("min")!, "min"));
        GridCell max = RunArgs.CellOf(RunArgs.PositionOf(args.Optional("max") ??
                                                          throw ApiErrors.InvalidArgument("Pass max too."), "max"));
        long count = SmallCellCode.CountIn(min, max);
        if (count > MaximumCells)
        {
            throw ApiErrors.InvalidArgument($"The box holds {count} 2 m cells; at most {MaximumCells}.");
        }

        return SmallCellCode.LargeCellsIn(min, max);
    }

    private static SurveyCellView CellView(GridFacts facts, GridCell cell)
    {
        Frame? frame = facts.FrameAt(cell);
        Room? room = facts.RoomAt(cell);
        List<SurveyWallView> walls = new List<SurveyWallView>();
        foreach (GridStep face in GridStep.All)
        {
            foreach (Structure wall in facts.FaceStructures(cell, face))
            {
                walls.Add(new SurveyWallView(face.Name, GameLookup.ViewOf(wall), !wall.CanAirPass));
            }
        }

        SurveyFrameView? frameView = frame != null
            ? new SurveyFrameView(GameLookup.ViewOf(frame), frame.CurrentBuildStateIndex,
                frame.BuildStates?.Count ?? 0, !frame.CanAirPass, !frame.CanGravityPass)
            : null;
        return new SurveyCellView(GameLookup.ViewOf(PieceShapes.CentreOf(cell)),
            room != null ? room.RoomId.ToString(CultureInfo.InvariantCulture) : null, frameView, walls,
            SmallCellCode.Encode(cell, facts.Occupancy), CellSupports.Encode(cell, facts.Support));
    }

    private static SurveyContents Contents(GridFacts facts, List<GridCell> cells, bool includeNetworks)
    {
        Dictionary<long, SmallGrid> pieces = new Dictionary<long, SmallGrid>();
        Dictionary<long, Device> devices = new Dictionary<long, Device>();
        foreach (GridCell large in cells)
        {
            for (int index = 0; index < SmallCellCode.PerCell; index++)
            {
                SmallCell? small = facts.SmallAt(SmallCellCode.SmallAt(large, index));
                if (small == null)
                {
                    continue;
                }

                AddPiece(pieces, small.Cable);
                AddPiece(pieces, small.Pipe);
                AddPiece(pieces, small.Chute);
                if (small.Device != null && !small.Device.IsBeingDestroyed)
                {
                    devices[small.Device.ReferenceId] = small.Device;
                }
            }
        }

        List<long> ids = new List<long>(pieces.Keys);
        ids.Sort();
        List<SurveyPieceView> pieceViews = new List<SurveyPieceView>(ids.Count);
        Dictionary<long, IReferencable> networks = new Dictionary<long, IReferencable>();
        ChuteFlowResult? flow = ChuteFlowOf(pieces.Values);
        foreach (long id in ids)
        {
            pieceViews.Add(PieceView(pieces[id], networks, flow));
        }

        List<long> deviceIds = new List<long>(devices.Keys);
        deviceIds.Sort();
        List<SurveyDeviceView> deviceViews = new List<SurveyDeviceView>(deviceIds.Count);
        foreach (long id in deviceIds)
        {
            deviceViews.Add(DeviceView(devices[id]));
        }

        List<object> networkViews = new List<object>();
        if (includeNetworks)
        {
            List<long> networkIds = new List<long>(networks.Keys);
            networkIds.Sort();
            foreach (long id in networkIds)
            {
                networkViews.Add(networks[id] switch
                {
                    CableNetwork cables => RunNetworks.CableSummary(cables),
                    PipeNetwork pipes => RunNetworks.PipeSummary(pipes),
                    IReferencable chutes => new ChuteRunKind().Summary(chutes)
                });
            }
        }

        return new SurveyContents(pieceViews, deviceViews, networkViews);
    }

    private static void AddPiece(Dictionary<long, SmallGrid> pieces, SmallGrid? piece)
    {
        if (piece != null && !piece.IsBeingDestroyed)
        {
            pieces[piece.ReferenceId] = piece;
        }
    }

    // The flow through the chute pieces listed and the networks they are on; null when there are none or too many.
    private static ChuteFlowResult? ChuteFlowOf(IEnumerable<SmallGrid> pieces)
    {
        List<SmallGrid> chutes = new List<SmallGrid>();
        foreach (SmallGrid piece in pieces)
        {
            if (piece is Chute)
            {
                chutes.Add(piece);
            }
        }

        if (chutes.Count == 0)
        {
            return null;
        }

        ChuteSurroundings around = ChuteSurroundings.Of(chutes);
        return around.TooLarge
            ? null
            : ChuteFlow.Solve(new ChuteFlowGraph(new List<PieceModel>(around.Before.Values), around.Ports,
                new HashSet<long>(), new List<RunLeg>()));
    }

    private static SurveyPieceView PieceView(SmallGrid piece, Dictionary<long, IReferencable> networks,
        ChuteFlowResult? flow)
    {
        PieceModel model = PieceShapes.Live(piece);
        List<PositionView>? cells = null;
        if (model.Cells.Count > 1)
        {
            cells = new List<PositionView>(model.Cells.Count);
            foreach (GridCell cell in model.Cells)
            {
                cells.Add(GameLookup.ViewOf(PieceShapes.CentreOf(cell)));
            }
        }

        string kind = piece is Cable ? "cable" : piece is Pipe ? "pipe" : "chute";
        IReferencable? network = piece is Cable cable ? cable.CableNetwork
            : piece is Pipe pipe ? pipe.PipeNetwork
            : piece is Chute chute ? chute.ChuteNetwork
            : null;
        if (network != null)
        {
            networks[network.ReferenceId] = network;
        }

        DynamicThing? item = ChuteFamily.ItemIn(piece);
        return new SurveyPieceView(GameLookup.ViewOf(piece), kind, GameLookup.ViewOf(piece.Position), cells,
            EndCleanup.DirectionsOf(model.Ends), network != null ? new ThingId(network.ReferenceId) : null,
            GradeOf(piece), piece is Chute && flow != null ? FlowView(model, flow) : null,
            item != null ? GameLookup.ViewOf(item) : null);
    }

    private static RunFlowView FlowView(PieceModel model, ChuteFlowResult flow)
    {
        List<string> into = new List<string>();
        List<string> outOf = new List<string>();
        for (int index = 0; index < model.Ends.Count; index++)
        {
            PieceEnd end = model.Ends[index];
            string name = GridStep.Between(end.Facing, end.Local)?.Name ?? "?";
            switch (flow.Of(model.Id, index))
            {
                case FlowDirection.In:
                    into.Add(name);
                    break;
                case FlowDirection.Out:
                    outOf.Add(name);
                    break;
            }
        }

        return new RunFlowView(into, outOf);
    }

    private static string? GradeOf(SmallGrid piece)
    {
        if (piece is Cable cable)
        {
            return CableFamily.NameOf(cable.CableType);
        }

        if (piece is Piping piping)
        {
            string content = piping.PipeContentType == Pipe.ContentType.Liquid ? "liquid" : "gas";
            return piping.PipeType switch
            {
                Piping.Type.Insulated => "insulated_" + content,
                Piping.Type.normal => content,
                _ => $"{piping.PipeType} {piping.PipeContentType}".ToLowerInvariant()
            };
        }

        return null;
    }

    private static SurveyDeviceView DeviceView(Device device)
    {
        List<SurveyPortView> ports = new List<SurveyPortView>();
        if (device.OpenEnds != null)
        {
            GridController world = GridController.World;
            for (int index = 0; index < device.OpenEnds.Count; index++)
            {
                Connection end = device.OpenEnds[index];
                if (end?.Transform == null ||
                    (end.ConnectionType & (NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                           NetworkType.Chute)) == NetworkType.None)
                {
                    continue;
                }

                GridCell local = PieceShapes.Cell(end.GetLocalGrid());
                GridStep? toward = GridStep.Between(local, PieceShapes.Cell(end.GetFacingGrid()));
                ports.Add(new SurveyPortView(index, GameLookup.ViewOf(PieceShapes.CentreOf(local)),
                    toward?.Name ?? "?", end.ConnectionType.ToString(), end.ConnectionRole.ToString(),
                    NetworkAt(world.GetSmallCell(end.GetLocalGrid()), end)));
            }
        }

        return new SurveyDeviceView(GameLookup.ViewOf(device), GameLookup.ViewOf(device.Position), ports);
    }

    private static ThingId? NetworkAt(SmallCell? cell, Connection end)
    {
        if (cell == null)
        {
            return null;
        }

        if (cell.Cable != null && !cell.Cable.IsBeingDestroyed && cell.Cable.IsConnected(end) &&
            cell.Cable.CableNetwork != null)
        {
            return new ThingId(cell.Cable.CableNetwork.ReferenceId);
        }

        if (cell.Pipe != null && !cell.Pipe.IsBeingDestroyed && cell.Pipe.IsConnected(end) &&
            cell.Pipe.PipeNetwork != null)
        {
            return new ThingId(cell.Pipe.PipeNetwork.ReferenceId);
        }

        return cell.Chute != null && !cell.Chute.IsBeingDestroyed && cell.Chute.IsConnected(end) &&
               cell.Chute.ChuteNetwork != null
            ? new ThingId(cell.Chute.ChuteNetwork.ReferenceId)
            : null;
    }
}
