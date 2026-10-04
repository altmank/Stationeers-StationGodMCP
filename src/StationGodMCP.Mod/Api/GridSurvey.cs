#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using Objects.Structures;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
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
/// them (ChuteFlow) and what rides in them. sections keeps only the parts named (SurveySections), network_ids only the
/// pieces of those networks and the devices with a port on one (SurveyNetworkFilter), and compact leaves each cell's
/// small and support strings and the legend out. Read only.
/// </summary>
internal static class GridSurveyApi
{
    private const int DefaultLimit = ReplyDefaults.GridSurveyCells;
    private const int MaximumLimit = 125;
    private const long MaximumCells = 20000;

    internal const string Legend =
        "Each cell is a 2 m cell at its centre (odd metres). small: its 64 small-grid cells (0.5 m) at -1, -0.5, 0 " +
        "and +0.5 m from the centre along each axis (index 0 to 3; index 0 lies on the cell's minimum face plane, " +
        "shared with the neighbour), character index x + 4y + 16z. '.' empty, 'c' cable, 'p' pipe, 'b' cable and " +
        "pipe, 'h' chute, 'd' device, 'o' another small-grid thing (a mounted item, a rail), 'r' a rocket's empty cell " +
        "(its fuselage decides which kind of piece it takes). " +
        "Frames and walls never block cables or pipes; a device, chute or 'o' blocks both; a pipe blocks a cable " +
        "(and a cable a pipe) only along the axis its ends lie on. A chute needs a cell with no cable, pipe, device, " +
        "chute or 'o'. support: the same 64 cells by what holds a piece there up: 'i' inside a frame (every 2 m cell " +
        "the small cell touches holds a frame: hidden in the frame's body), 'e' a frame edge or corner, 'f' on a " +
        "frame's face (a frame's top face is the minimum plane of the cell above it), 'w' on a wall's plane, 'a' air " +
        "(plan_*_route frames_first avoids 'a' cells); over those, 'x' a door's keep-out (its face and the " +
        "configured band either side inside its rectangle: the planners never route there without " +
        "allow_door_keepout; the floor slab under a threshold is not in it) and 'g' a window's face (routes pay " +
        "extra, crosses_window). A door's face is no wall support. network_visibility counts each listed network's cells the " +
        "same way (inside, frame_surface, wall, air) and lists the floating (air) ones.";

    internal static GridSurveyView Handle(Args args)
    {
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        SurveySections sections = SurveySections.Parse(args);
        bool compact = args.OptionalBool("compact") ?? false;
        bool includeNetworks = args.OptionalBool("include_networks") ?? true;
        bool includeRefund = args.OptionalBool("include_refund") ?? false;
        SurveyNetworkFilter filter = NetworkFilter(args);
        SurveyKinds kinds = SurveyKinds.Parse(args);
        List<GridCell> cells = Cells(args);
        Slice<GridCell> slice = Slice<GridCell>.Of(cells, page);
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        List<SurveyCellView> views = new List<SurveyCellView>(slice.Items.Count);
        if (sections.Includes(SurveySection.Cells))
        {
            foreach (GridCell cell in slice.Items)
            {
                views.Add(CellView(facts, cell, compact));
            }
        }

        SurveyContents contents = Contents(facts, slice.Items, sections, new SurveyFilter(filter, kinds), includeNetworks,
            includeRefund);
        string? legend = sections.Includes(SurveySection.Cells) && !compact ? Legend : null;
        page.Note("cells", slice.Items.Count, cells.Count);
        return new GridSurveyView(Slice<SurveyCellView>.Page(views, page, cells.Count), contents, sections, legend);
    }

    private static SurveyNetworkFilter NetworkFilter(Args args)
    {
        if (!args.Has(SurveyNetworkFilter.Argument))
        {
            return SurveyNetworkFilter.Every;
        }

        JArray array = args.Array(SurveyNetworkFilter.Argument, SurveyNetworkFilter.MaximumNetworks);
        HashSet<long> networks = new HashSet<long>();
        for (int index = 0; index < array.Count; index++)
        {
            networks.Add(NetworkNamed(array[index], $"{SurveyNetworkFilter.Argument}[{index}]").Value);
        }

        return SurveyNetworkFilter.Only(networks);
    }

    // A cable, pipe or chute network's id, or a piece standing for its network (recorded in resolved_networks).
    private static ThingId NetworkNamed(JToken token, string name)
    {
        if (!ThingId.TryRead(token, out ThingId id))
        {
            throw ApiErrors.InvalidArgument(
                $"{name} must be a network id or the reference id of a cable, pipe or chute piece on it.");
        }

        if (GameLookup.TryFindThing(id, out Thing thing))
        {
            IReferencable network = (thing is SmallGrid piece ? NetworkOf(piece) : null) ??
                                    throw ApiErrors.InvalidArgument(
                                        $"{name}: {Names.Of(thing)} ({thing.PrefabName}) is no cable, pipe or chute " +
                                        "piece on a network; name the network or a piece on it.");
            ThingId resolved = new ThingId(network.ReferenceId);
            ResolvedNetworks.Record(name, new NetworkHandle.ById(id), resolved);
            return resolved;
        }

        bool known = Referencable.Find<CableNetwork>(id.Value) != null ||
                     Referencable.Find<PipeNetwork>(id.Value) != null ||
                     Referencable.Find<ChuteNetwork>(id.Value) != null;
        return known
            ? id
            : throw ApiErrors.Refused("network_not_found",
                $"{name}: {id} names no thing and no cable, pipe or chute network. A network's id changes after " +
                "almost every edit; name a piece on it instead.");
    }

    private static IReferencable? NetworkOf(SmallGrid piece) => piece switch
    {
        Cable cable => cable.CableNetwork,
        Pipe pipe => pipe.PipeNetwork,
        Chute chute => chute.ChuteNetwork,
        _ => null
    };

    private static ThingId? NetworkIdOf(SmallGrid piece) =>
        NetworkOf(piece) is IReferencable network ? new ThingId(network.ReferenceId) : null;

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

    private static SurveyCellView CellView(GridFacts facts, GridCell cell, bool compact)
    {
        Frame? frame = facts.FrameAt(cell);
        Room? room = facts.RoomAt(cell);
        List<SurveyWallView> walls = new List<SurveyWallView>();
        foreach (GridStep face in GridStep.All)
        {
            foreach (Structure wall in facts.FaceStructures(cell, face))
            {
                walls.Add(new SurveyWallView(face.Name, GameLookup.ViewOf(wall), !wall.CanAirPass,
                    Openings.KindOf(wall).ToString().ToLowerInvariant()));
            }
        }

        SurveyFrameView? frameView = frame != null
            ? new SurveyFrameView(GameLookup.ViewOf(frame), frame.CurrentBuildStateIndex,
                frame.BuildStates?.Count ?? 0, !frame.CanAirPass, !frame.CanGravityPass)
            : null;
        return new SurveyCellView(GameLookup.ViewOf(PieceShapes.CentreOf(cell)),
            room != null ? room.RoomId.ToString(CultureInfo.InvariantCulture) : null, frameView, walls,
            compact ? null : SmallCellCode.Encode(cell, facts.Occupancy),
            compact ? null : OpeningZones.Overlay(CellSupports.Encode(cell, facts.Large), ZonesOf(facts, cell)));
    }

    private static List<OpeningZone> ZonesOf(GridFacts facts, GridCell large)
    {
        List<OpeningZone> zones = new List<OpeningZone>(SmallCellCode.PerCell);
        for (int index = 0; index < SmallCellCode.PerCell; index++)
        {
            zones.Add(facts.Opening(SmallCellCode.SmallAt(large, index)));
        }

        return zones;
    }

    // Every door on a face of the page's cells, once.
    private static List<SurveyDoorView> Doors(GridFacts facts, List<GridCell> cells)
    {
        Dictionary<long, Structure> doors = new Dictionary<long, Structure>();
        foreach (GridCell cell in cells)
        {
            foreach (GridStep face in GridStep.All)
            {
                foreach (Structure structure in facts.FaceStructures(cell, face))
                {
                    if (Openings.IsDoor(structure))
                    {
                        doors[structure.ReferenceId] = structure;
                    }
                }
            }
        }

        List<long> ids = new List<long>(doors.Keys);
        ids.Sort();
        List<SurveyDoorView> views = new List<SurveyDoorView>(ids.Count);
        foreach (long id in ids)
        {
            Structure door = doors[id];
            List<GridCell> faces = Openings.FacesOf(door);
            int axis = faces.Count > 0 ? FacePoints.AxisOf(faces[0]) : -1;
            string plane = axis < 0 ? "?" : FacePlane.Of(faces[0]).ToString();
            List<GridCell> ports = new List<GridCell>(Openings.PortCellsOf(door));
            views.Add(new SurveyDoorView(GameLookup.ViewOf(door),
                faces.ConvertAll(face => GameLookup.ViewOf(PieceShapes.CentreOf(face))), plane, facts.Band.Metres,
                ports.ConvertAll(port => GameLookup.ViewOf(PieceShapes.CentreOf(port)))));
        }

        return views;
    }

    private static SurveyContents Contents(GridFacts facts, List<GridCell> cells, SurveySections sections,
        SurveyFilter filter, bool includeNetworks, bool includeRefund)
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

                AddPiece(pieces, small.Cable, filter);
                AddPiece(pieces, small.Pipe, filter);
                AddPiece(pieces, small.Chute, filter);
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
        NetworkTallies tallies = new NetworkTallies(facts, includeRefund);
        bool tally = sections.Includes(SurveySection.NetworkVisibility);
        foreach (long id in ids)
        {
            SurveyPieceView view = PieceView(pieces[id], networks, flow, includeRefund);
            pieceViews.Add(view);
            if (tally)
            {
                tallies.Add(pieces[id], view);
            }
        }

        List<SurveyDeviceView> deviceViews = sections.Includes(SurveySection.Devices)
            ? DeviceViews(devices, filter)
            : new List<SurveyDeviceView>();
        List<object> networkViews = new List<object>();
        if (includeNetworks && sections.Includes(SurveySection.Networks))
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

        List<SurveyDoorView> doors = sections.Includes(SurveySection.Doors)
            ? Doors(facts, cells)
            : new List<SurveyDoorView>();
        return new SurveyContents(pieceViews, deviceViews, networkViews, tallies.Views(), doors);
    }

    // The devices by id, those with a port on a network the filter names (every device when it names none).
    private static List<SurveyDeviceView> DeviceViews(Dictionary<long, Device> devices, SurveyFilter filter)
    {
        List<long> ids = new List<long>(devices.Keys);
        ids.Sort();
        List<SurveyDeviceView> views = new List<SurveyDeviceView>(ids.Count);
        foreach (long id in ids)
        {
            SurveyDeviceView view = DeviceView(devices[id]);
            if (filter.Networks.AdmitsAny(view.Ports.ConvertAll(static port => port.NetworkId)) &&
                filter.Kinds.AdmitsDevice(view.Ports.ConvertAll(static port => (string?)port.Type)))
            {
                views.Add(view);
            }
        }

        return views;
    }

    private static void AddPiece(Dictionary<long, SmallGrid> pieces, SmallGrid? piece, SurveyFilter filter)
    {
        if (piece != null && !piece.IsBeingDestroyed && filter.Networks.Admits(NetworkIdOf(piece)) &&
            filter.Kinds.AdmitsPiece(KindOf(piece)))
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
        ChuteFlowResult? flow, bool includeRefund)
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

        string kind = KindOf(piece);
        IReferencable? network = NetworkOf(piece);
        if (network != null)
        {
            networks[network.ReferenceId] = network;
        }

        DynamicThing? item = ChuteFamily.ItemIn(piece);
        return new SurveyPieceView(GameLookup.ViewOf(piece), kind, GameLookup.ViewOf(piece.Position), cells,
            EndCleanup.DirectionsOf(model.Ends), network != null ? new ThingId(network.ReferenceId) : null,
            GradeOf(piece), piece is Chute && flow != null ? FlowView(model, flow) : null,
            item != null ? GameLookup.ViewOf(item) : null,
            includeRefund ? RunReports.Amounts(BuildMaterials.RefundOf(piece)) : null);
    }

    /// <summary>The listed pieces per network: their cells by visibility, the air ones, and their refund.</summary>
    private sealed class NetworkTallies
    {
        private const int AirListed = 32;

        private readonly GridFacts _facts;
        private readonly bool _refund;
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, Tally> _tallies = new Dictionary<string, Tally>();

        internal NetworkTallies(GridFacts facts, bool refund)
        {
            _facts = facts;
            _refund = refund;
        }

        internal void Add(SmallGrid piece, SurveyPieceView view)
        {
            string key = view.Kind + ":" + (view.NetworkId?.ToString() ?? "none");
            if (!_tallies.TryGetValue(key, out Tally tally))
            {
                tally = new Tally(view.Kind, view.NetworkId);
                _tallies[key] = tally;
                _order.Add(key);
            }

            tally.Pieces++;
            foreach (GridCell cell in PieceShapes.Live(piece).Cells)
            {
                tally.Cells.Add(cell, _facts.Visibility(cell));
            }

            if (_refund)
            {
                tally.Refund.AddRange(BuildMaterials.RefundOf(piece));
            }
        }

        internal List<SurveyNetworkVisibilityView> Views() =>
            _order.ConvertAll(key => _tallies[key].View(_refund));

        private sealed class Tally
        {
            internal Tally(string kind, ThingId? network)
            {
                Kind = kind;
                Network = network;
            }

            internal string Kind { get; }

            internal ThingId? Network { get; }

            internal int Pieces { get; set; }

            internal VisibilityTally Cells { get; } = new VisibilityTally();

            internal List<ItemAmount> Refund { get; } = new List<ItemAmount>();

            internal SurveyNetworkVisibilityView View(bool refund)
            {
                List<GridCell> air = Cells.AirCells;
                List<PositionView>? airAt = air.Count > 0
                    ? air.GetRange(0, Math.Min(air.Count, AirListed))
                        .ConvertAll(cell => GameLookup.ViewOf(PieceShapes.CentreOf(cell)))
                    : null;
                return new SurveyNetworkVisibilityView(Network, Kind, Pieces,
                    new RouteVisibilityView(Cells.Inside, Cells.FrameSurface, Cells.Wall, Cells.Air), airAt,
                    refund ? RunReports.Amounts(Refund) : null);
            }
        }
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

        return new SurveyDeviceView(GameLookup.ViewOf(device), GameLookup.ViewOf(device.Position), ports,
            Orientations.Of(device), RocketReadings.UmbilicalOf(device));
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

    private static string KindOf(SmallGrid piece) => piece is Cable ? "cable" : piece is Pipe ? "pipe" : "chute";

    /// <summary>network_ids and kinds together: what a page's pieces and devices must pass.</summary>
    private readonly struct SurveyFilter
    {
        internal SurveyFilter(SurveyNetworkFilter networks, SurveyKinds kinds)
        {
            Networks = networks;
            Kinds = kinds;
        }

        internal SurveyNetworkFilter Networks { get; }

        internal SurveyKinds Kinds { get; }
    }
}
