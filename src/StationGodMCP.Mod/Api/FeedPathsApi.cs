#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// feed_paths: from a root device on a network (an APC's output, a generator), the path the network takes to every
/// other device on it (FeedPaths: breadth first over the model's links among the network's pieces and devices), and
/// the rooms each path crosses. A piece is in the room of a 2 m cell it touches (its own first); a device in the room
/// of its grid cell, as rooms reports it. A device fed through a room that is neither the root's nor its own is a
/// daisy chain; a room its devices' feeds enter at more than one piece has several feeds. Read only.
/// </summary>
internal static class FeedPathsApi
{
    private const int MaximumMembers = 8192;

    internal static FeedPathsView Handle(Args args)
    {
        RunKind kind = (args.OptionalString("kind") ?? "cable").Trim().ToLowerInvariant() switch
        {
            "cable" => new CableRunKind(),
            "pipe" => new PipeRunKind(),
            "chute" => new ChuteRunKind(),
            _ => throw ApiErrors.InvalidArgument("kind must be cable, pipe or chute.")
        };
        Thing thing = GameLookup.RequireThing(args.ThingId("root"));
        if (!(thing is Device root))
        {
            throw ApiErrors.InvalidArgument($"root: {thing.DisplayName} ({thing.PrefabName}) is not a device.");
        }

        long network = NetworkOf(args, kind, root);
        List<SmallGrid> members = kind.Family.NetworkMembers(new ThingId(network));
        members.AddRange(kind.Family.NetworkDevices(new ThingId(network)));
        if (members.Count > MaximumMembers)
        {
            throw ApiErrors.Refused("too_many_pieces", $"Network {network} has {members.Count} members.");
        }

        GridFacts facts = new GridFacts(kind, SmallGridBlock.None, new HashSet<long>());
        RoomController? rooms = RoomController.World;
        Dictionary<long, FeedNode> nodes = new Dictionary<long, FeedNode>();
        Dictionary<long, SmallGrid> things = new Dictionary<long, SmallGrid>();
        List<PieceModel> models = new List<PieceModel>(members.Count + 1);
        foreach (SmallGrid member in members)
        {
            Add(member, kind, facts, rooms, nodes, things, models);
        }

        Add(root, kind, facts, rooms, nodes, things, models);
        HashSet<Link> links = Connectivity.LinksTouching(models, new HashSet<long>(nodes.Keys));
        FeedReport report = FeedPaths.Of(root.ReferenceId, nodes, links);
        return View(root, network, kind, report, nodes, things);
    }

    private static void Add(SmallGrid thing, RunKind kind, GridFacts facts, RoomController? rooms,
        Dictionary<long, FeedNode> nodes, Dictionary<long, SmallGrid> things, List<PieceModel> models)
    {
        if (nodes.ContainsKey(thing.ReferenceId) || thing.IsBeingDestroyed)
        {
            return;
        }

        PieceModel model = PieceShapes.Live(thing);
        bool piece = kind.Family.IsPiece(thing);
        long? room = piece ? PieceRoom(model, facts) : DeviceRoom(thing, rooms);
        nodes[thing.ReferenceId] = new FeedNode(thing.ReferenceId, !piece, room);
        things[thing.ReferenceId] = thing;
        models.Add(model);
    }

    // The room of the first 2 m cell the piece's first cell touches that is in one (its own cell first).
    private static long? PieceRoom(PieceModel model, GridFacts facts)
    {
        if (model.Cells.Count == 0)
        {
            return null;
        }

        foreach (GridCell large in CellSupports.Touched(model.Cells[0]))
        {
            Room? room = facts.RoomAt(large);
            if (room != null)
            {
                return room.RoomId;
            }
        }

        return null;
    }

    private static long? DeviceRoom(SmallGrid device, RoomController? rooms)
    {
        if (rooms == null || device.WorldGrid == WorldGrid.INVALID)
        {
            return null;
        }

        return rooms.GetRoom(device.WorldGrid.Value)?.RoomId;
    }

    // network_id, or the network the root's named port (or its only network of the kind) is on.
    private static long NetworkOf(Args args, RunKind kind, Device root)
    {
        List<long> networks = kind.Family.DeviceNetworks(root);
        if (args.Has("network_id"))
        {
            long wanted = NetworkHandles.Resolve(args, "network_id", kind.Family).Value;
            if (!networks.Contains(wanted))
            {
                throw ApiErrors.Refused("not_on_network",
                    $"{root.PrefabName} {root.ReferenceId} is not on network {wanted}; its {kind.Noun} networks: " +
                    $"[{string.Join(", ", networks)}].");
            }

            return wanted;
        }

        int? port = args.OptionalInt("port", 0, 64);
        if (port.HasValue)
        {
            return PortNetwork(kind, root, port.Value) ??
                   throw ApiErrors.Refused("port_not_joined",
                       $"Port {port.Value} of {root.PrefabName} {root.ReferenceId} joins no {kind.Noun} network.");
        }

        if (networks.Count == 0)
        {
            throw ApiErrors.Refused("not_on_network",
                $"{root.PrefabName} {root.ReferenceId} is on no {kind.Noun} network.");
        }

        if (networks.Count != 1)
        {
            throw ApiErrors.InvalidArgument(
                $"{root.PrefabName} {root.ReferenceId} is on {networks.Count} {kind.Noun} networks " +
                $"[{string.Join(", ", networks)}]; name one with network_id or port (connections lists them).");
        }

        return networks[0];
    }

    private static long? PortNetwork(RunKind kind, Device root, int port)
    {
        if (root.OpenEnds == null || port >= root.OpenEnds.Count || root.OpenEnds[port]?.Transform == null)
        {
            throw ApiErrors.InvalidArgument($"port {port} is not a port of {root.PrefabName} {root.ReferenceId}.");
        }

        Connection end = root.OpenEnds[port];
        SmallCell? cell = GridController.World.GetSmallCell(end.GetLocalGrid());
        SmallGrid? piece = cell != null ? kind.SlotOf(cell) : null;
        return piece != null && !piece.IsBeingDestroyed && piece.IsConnected(end)
            ? kind.Family.NetworkOf(piece)?.ReferenceId
            : null;
    }

    private static FeedPathsView View(Device root, long network, RunKind kind, FeedReport report,
        Dictionary<long, FeedNode> nodes, Dictionary<long, SmallGrid> things)
    {
        List<FeedDeviceView> devices = report.Paths.ConvertAll(path => new FeedDeviceView(
            GameLookup.ViewOf(things[path.Device]), RoomName(path.Room), path.Pieces.Count,
            path.Rooms.ConvertAll(RoomNameOf), path.Through.ConvertAll(RoomNameOf)));
        List<FeedRoomView> rooms = report.Rooms.ConvertAll(room => new FeedRoomView(RoomNameOf(room.Room),
            room.Devices.Count, room.Entries.ConvertAll(id => new ThingId(id)),
            room.Entries.ConvertAll(id => GameLookup.ViewOf(things[id].Position))));
        List<ThingView> unreached = report.Unreached.ConvertAll(id => GameLookup.ViewOf(things[id]));
        long? rootRoom = nodes[root.ReferenceId].Room;
        return new FeedPathsView(GameLookup.ViewOf(root), new ThingId(network), kind.Noun, RoomName(rootRoom),
            devices, rooms, unreached);
    }

    private static string? RoomName(long? room) =>
        room.HasValue ? room.Value.ToString(CultureInfo.InvariantCulture) : null;

    private static string RoomNameOf(long room) => room.ToString(CultureInfo.InvariantCulture);
}
