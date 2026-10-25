#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The ends of a route as plan route's start and end give them: {at: position} (that small cell), {id} of one of the
/// kind's pieces (its cell; leaving through one of its open ends is free, through another makes it a junction; a long
/// straight as a target offers every cell of it, and build splits it), {id, port} of a device (the cell a piece
/// joining that port stands in; port may be left out when the device has one port of the kind), and as a target
/// {network}: every cell of every piece of that network. {id, port} of a thing of the kind with its own ends that is
/// neither a piece nor a device (an in-line tank, a passive vent; 1.3.5): the cell beyond its free end (port names the
/// end when it has several free). Starts may be several (up to MaximumStarts): a list of ends, or {id, ports: [...]}
/// for several ports of one device. Pieces being removed (a reroute, assume_removed) count as gone.
/// </summary>
internal static class RouteEnds
{
    internal const double JunctionCost = 3.0;
    internal const int MaximumStarts = RouteTrees.MaximumStarts;

    /// <summary>One or more starts: an end, an array of ends, or a device with ports: [...].</summary>
    [Profiled]
    internal static List<RouteEndpoint> Starts(JToken? token, string name, RunKind kind, int type,
        HashSet<long> ignore)
    {
        List<RouteEndpoint> starts = new List<RouteEndpoint>();
        if (token is JArray array)
        {
            if (array.Count == 0 || array.Count > MaximumStarts)
            {
                throw ApiErrors.InvalidArgument($"{name} lists 1 to {MaximumStarts} ends.");
            }

            for (int index = 0; index < array.Count; index++)
            {
                starts.AddRange(Starts(array[index], $"{name}[{index}]", kind, type, ignore));
            }

            return starts;
        }

        if (token is JObject item && item["ports"] is JArray ports)
        {
            if (ports.Count == 0 || ports.Count > MaximumStarts || item["port"] != null)
            {
                throw ApiErrors.InvalidArgument(
                    $"{name}.ports lists 1 to {MaximumStarts} port indexes (and " +
                    "replaces port).");
            }

            for (int index = 0; index < ports.Count; index++)
            {
                JObject one = new JObject
                {
                    ["id"] = item["id"]?.DeepClone(),
                    ["port"] = ports[index].DeepClone()
                };
                starts.Add(Resolve(one, $"{name}.ports[{index}]", kind, type, ignore, false));
            }

            return starts;
        }

        starts.Add(Resolve(token, name, kind, type, ignore, false));
        return starts;
    }

    /// <summary>A target: any end form, {network}, or a long straight (every cell of it).</summary>
    [Profiled]
    internal static RouteEndpoint Target(JToken? token, string name, RunKind kind, int type, HashSet<long> ignore)
    {
        if (token is JObject item && item["network"] is { } given)
        {
            NetworkTarget network = NetworkTarget.Of(given) ?? throw ApiErrors.InvalidArgument(
                $"{name}.network must be a network's id, a piece or device on it, or {{id, port}}.");
            return OfNetwork(kind, NetworkHandles.Resolve(network, $"{name}.network", kind.Family), name, ignore);
        }

        return Resolve(token, name, kind, type, ignore, true);
    }

    internal static RouteEndpoint Resolve(JToken? token, string name, RunKind kind, int type, HashSet<long> ignore,
        bool target)
    {
        if (!(token is JObject item))
        {
            throw ApiErrors.InvalidArgument($"{name} must be {{at: [x, y, z]}}, {{id}}, {{id, port}} or {{ghost, port}}.");
        }

        if (item["ghost"] != null && item["ghost"]!.Type != JTokenType.Null)
        {
            return OfGhost(kind, item, type, name, ignore);
        }

        if (item["at"] != null && item["at"]!.Type != JTokenType.Null)
        {
            GridCell cell = RunArgs.CellOf(RunArgs.PositionOf(item["at"]!, $"{name}.at"));
            GridStep? toward = TowardOf(item, name);
            SmallCell? small = GridController.World.GetSmallCell(PieceShapes.Grid(cell));
            SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
            if (piece != null && !piece.IsBeingDestroyed && !ignore.Contains(piece.ReferenceId))
            {
                RouteEndpoint joined = OfCells(kind, piece, new List<GridCell> { cell }, LeavesOnlyOpen(kind, target),
                    name);
                return toward.HasValue ? new RouteEndpoint(joined.Ends, joined.Networks, toward) : joined;
            }

            return new RouteEndpoint(RouteEnd.Open(cell), new List<long>(), toward, turnsInto: true);
        }

        if (!ThingId.TryRead(item["id"], out ThingId id))
        {
            throw ApiErrors.InvalidArgument($"{name} must be {{at: [x, y, z]}}, {{id}} or {{id, port}}: id is a " +
                "reference id as a decimal string.");
        }

        int? port = PortOf(item, name);
        Thing thing = GameLookup.RequireThing(id);
        if (thing is SmallGrid own && kind.Family.IsPiece(own))
        {
            if (ignore.Contains(own.ReferenceId))
            {
                throw ApiErrors.InvalidArgument($"{name} names a piece being removed (the reroute's " +
                    $"old run or assume_removed).");
            }

            PieceModel model = PieceShapes.Live(own);
            if (model.Cells.Count > 1 && !target)
            {
                throw ApiErrors.Refused("long_piece",
                    $"{own.PrefabName} {own.ReferenceId} is a long straight; start from one of its cells with " +
                    $"{name}.at, or make it the target (end), where the route " +
                    "may join any of its cells.");
            }

            return OfCells(kind, own, model.Cells, LeavesOnlyOpen(kind, target), name);
        }

        if (thing is Device device)
        {
            return OfDevice(kind, device, port, type, name, ignore, target);
        }

        if (thing is SmallGrid member && kind.Family.IsMember(member))
        {
            if (ignore.Contains(member.ReferenceId))
            {
                throw ApiErrors.InvalidArgument($"{name} names a thing being removed " +
                    $"(assume_removed).");
            }

            return OfFreeEnd(kind, member, port, type, name, ignore);
        }

        throw ApiErrors.InvalidArgument(
            $"{name}: {Names.Of(thing)} ({thing.PrefabName}) is neither a {kind.Noun} piece, a " +
            $"device, nor a {kind.Noun} thing with ends (an in-line tank, a passive vent).");
    }

    // {at}'s toward: the way the end piece's end points from that cell, as a port's toward (a ghost's or a planned
    // device's, from show ghost or find ghost), so the run's last piece turns into it; null when left out.
    private static GridStep? TowardOf(JObject item, string name)
    {
        JToken? given = item["toward"];
        if (given == null || given.Type == JTokenType.Null)
        {
            return null;
        }

        return given.Type == JTokenType.String && GridStep.TryParse(given.Value<string>(), out GridStep step)
            ? step
            : throw ApiErrors.InvalidArgument($"{name}.toward must be an axis: +x, -x, +y, -y, +z or -z.");
    }

    // {ghost, port}: a board ghost's device port (find ghost lists them), as {id, port} of a built device would be: the
    // cell a piece joining it stands in and the end it needs into the device. Ports are read from the ghost's prefab
    // where it stands; a piece already in that cell is joined as at {at} does.
    private static RouteEndpoint OfGhost(RunKind kind, JObject item, int type, string name, HashSet<long> ignore)
    {
        JToken given = item["ghost"]!;
        if (given.Type != JTokenType.Integer)
        {
            throw ApiErrors.InvalidArgument($"{name}.ghost must be a ghost id (find ghost lists them).");
        }

        long id = given.Value<long>();
        BoardGhost ghost = GhostBoardStore.Board.Find(id) ?? throw ApiErrors.Refused("ghost_not_found",
            $"{name}: no ghost {id} on the board (find ghost lists them).");
        if (!(Prefab.Find(ghost.PrefabHash) is Structure prefab) || prefab == null)
        {
            throw ApiErrors.Refused("ghost_not_found",
                $"{name}: ghost {id}'s prefab {ghost.PrefabName} is not one this game holds.");
        }

        UnityEngine.Quaternion rotation =
            UnityEngine.Quaternion.Euler((float)ghost.Euler.X, (float)ghost.Euler.Y, (float)ghost.Euler.Z);
        UnityEngine.Vector3 position =
            new UnityEngine.Vector3((float)ghost.Position.X, (float)ghost.Position.Y, (float)ghost.Position.Z);
        PieceModel? model = PieceShapes.Placed(prefab, position, rotation, 0);
        List<PortCell> ports = model != null ? PortCells.Of(model.Ends, type) : new List<PortCell>();
        int? port = PortOf(item, name);
        List<int> indexes = ports.ConvertAll(static one => one.Index);
        int found = port.HasValue
            ? ports.FindIndex(one => one.Index == port.Value)
            : ports.Count == 1 ? 0 : -1;
        if (found < 0)
        {
            throw ApiErrors.InvalidArgument(port.HasValue
                ? $"{name}.port {port.Value} is not a {kind.Noun} port of ghost {id} ({ghost.PrefabName}); its ports " +
                  $"of that kind are [{string.Join(", ", indexes)}] (find ghost lists them)."
                : $"Ghost {id} ({ghost.PrefabName}) has {ports.Count} {kind.Noun} ports [{string.Join(", ", indexes)}]; " +
                  $"name one with {name}.port (find ghost lists them).");
        }

        PortCell chosen = ports[found];
        SmallCell? small = GridController.World.GetSmallCell(PieceShapes.Grid(chosen.Cell));
        SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
        if (piece != null && !piece.IsBeingDestroyed && !ignore.Contains(piece.ReferenceId))
        {
            RouteEndpoint joined = OfCells(kind, piece, new List<GridCell> { chosen.Cell });
            return new RouteEndpoint(joined.Ends, joined.Networks, chosen.Toward);
        }

        return new RouteEndpoint(RouteEnd.Open(chosen.Cell), new List<long>(), chosen.Toward, turnsInto: true);
    }

    // An end's port, an index 0 to 64; null when left out.
    private static int? PortOf(JObject item, string name)
    {
        JToken? port = item["port"];
        if (port == null || port.Type == JTokenType.Null)
        {
            return null;
        }

        return port.Type == JTokenType.Integer && port.Value<long>() is >= 0 and <= 64
            ? port.Value<int>()
            : throw ApiErrors.InvalidArgument($"{name}.port must be a port index, 0 to 64.");
    }

    /// <summary>
    /// The small cell a piece joining a thing's end stands in, for reserve_ports: a device's port (its end's own cell,
    /// as survey area lists it), or one end of a network thing such as an in-line tank (the cell beyond that end).
    /// </summary>
    internal static GridCell JoiningCell(Thing thing, int port, string name)
    {
        Connection? end = thing is SmallGrid owner && owner.OpenEnds != null && port < owner.OpenEnds.Count
            ? owner.OpenEnds[port]
            : null;
        if (end?.Transform == null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}: {Names.Of(thing)} ({thing.ReferenceId}) has no end {port} " +
                $"(inspect connections lists its ends).");
        }

        GridCell local = PieceShapes.Cell(end.GetLocalGrid());
        return thing is Device || !(thing is SmallGrid grid)
            ? local
            : EndCells.Of(PieceShapes.Live(grid), local, PieceShapes.Cell(end.GetFacingGrid())).Beyond;
    }

    // A cable, pipe or chute thing that is neither a piece the route tools lay nor a device (an in-line tank, a passive
    // vent: pipes with their own ends): the route meets it at the cell beyond its free end (the one named, or the only
    // one), and the piece there needs an end back toward it, as at a device port.
    private static RouteEndpoint OfFreeEnd(RunKind kind, SmallGrid member, int? port, int type, string name,
        HashSet<long> ignore)
    {
        PieceModel model = PieceShapes.Live(member);
        List<PieceModel> around = new List<PieceModel>();
        foreach (SmallGrid neighbour in LinkSurvey.Neighbourhood(new List<PieceModel> { model }).Values)
        {
            if (!ignore.Contains(neighbour.ReferenceId))
            {
                around.Add(PieceShapes.Live(neighbour));
            }
        }

        List<PieceEnd> connected = Connectivity.ConnectedEnds(model, around);
        List<(int Index, bool Free)> candidates = new List<(int Index, bool Free)>();
        List<Connection> ends = member.OpenEnds != null ? new List<Connection>(member.OpenEnds) : new List<Connection>();
        for (int index = 0; index < ends.Count; index++)
        {
            Connection end = ends[index];
            if (end?.Transform != null && ((int)end.ConnectionType & type) != 0)
            {
                GridCell local = PieceShapes.Cell(end.GetLocalGrid());
                GridCell facing = PieceShapes.Cell(end.GetFacingGrid());
                candidates.Add((index, !connected.Exists(joined => joined.Local.Equals(local) &&
                                                                    joined.Facing.Equals(facing))));
            }
        }

        int? chosen = EndChoice.Pick(candidates, port, out string? error);
        if (!chosen.HasValue)
        {
            throw ApiErrors.InvalidArgument($"{name}: {member.PrefabName} {member.ReferenceId}: " +
                $"{error} (inspect connections lists its ends).");
        }

        Connection picked = ends[chosen.Value];
        (GridCell own, GridCell cell) = EndCells.Of(model, PieceShapes.Cell(picked.GetLocalGrid()),
            PieceShapes.Cell(picked.GetFacingGrid()));
        GridStep? into = GridStep.Between(cell, own);
        IReferencable? network = kind.Family.NetworkOf(member);
        List<long> networks = network != null ? new List<long> { network.ReferenceId } : new List<long>();
        SmallCell? small = GridController.World.GetSmallCell(PieceShapes.Grid(cell));
        SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
        if (piece != null && piece != member && !piece.IsBeingDestroyed && !ignore.Contains(piece.ReferenceId))
        {
            RouteEndpoint joined = OfCells(kind, piece, new List<GridCell> { cell });
            List<long> both = new List<long>(joined.Networks);
            both.AddRange(networks.FindAll(id => !both.Contains(id)));
            return new RouteEndpoint(joined.Ends, both, into);
        }

        return new RouteEndpoint(RouteEnd.Open(cell), networks, into);
    }

    // A chute route starts where items leave the piece: through an open end, never a junction (a chute junction only
    // merges a line into another, so a route fed from one runs backwards: flow_conflict, flow_reversed).
    private static bool LeavesOnlyOpen(RunKind kind, bool target) => !target && kind is ChuteRunKind;

    // Each cell of the piece: leaving through an open end there is free, any other way makes a junction (for a chute
    // start: leaving only through an open end items can leave by).
    private static RouteEndpoint OfCells(RunKind kind, SmallGrid piece, IReadOnlyList<GridCell> cells,
        bool leavingOnly = false, string name = "start")
    {
        PieceModel model = PieceShapes.Live(piece);
        List<PieceModel> around = new List<PieceModel>();
        foreach (SmallGrid neighbour in LinkSurvey.Neighbourhood(new List<PieceModel> { model }).Values)
        {
            around.Add(PieceShapes.Live(neighbour));
        }

        List<PieceEnd> connected = Connectivity.ConnectedEnds(model, around);
        ChuteFlowResult? flow = leavingOnly ? LineFlow(piece) : null;
        List<RouteEnd> ends = new List<RouteEnd>(cells.Count);
        foreach (GridCell cell in cells)
        {
            EndSet open = RouteEnd.OpenAt(model, cell, connected, leavingOnly,
                flow != null ? index => flow.Of(piece.ReferenceId, index) : null);
            if (leavingOnly && open.Count == 0)
            {
                throw ApiErrors.InvalidArgument(
                    $"{name}: {piece.PrefabName} {piece.ReferenceId} has no open end items can " +
                    $"leave it by at {cell}; a route from it would need a junction there, which only works where a " +
                    "route merges into a line (end). Start from a chute with an open output end, or from a device's " +
                    "output port.");
            }

            ends.Add(leavingOnly ? RouteEnd.Leaving(cell, open) : new RouteEnd(cell, open, JunctionCost));
        }

        IReferencable? network = kind.Family.NetworkOf(piece);
        List<long> networks = network != null ? new List<long> { network.ReferenceId } : new List<long>();
        return new RouteEndpoint(ends, networks);
    }

    // The item flow of the piece's chute network as it stands (ChuteFlow), so a start never leaves through an open end
    // the line already sends items in by; null when the network is too large to solve.
    private static ChuteFlowResult? LineFlow(SmallGrid piece)
    {
        ChuteSurroundings around = ChuteSurroundings.Of(new List<SmallGrid> { piece });
        return around.TooLarge
            ? null
            : ChuteFlow.Solve(new ChuteFlowGraph(new List<PieceModel>(around.Before.Values), around.Ports,
                new HashSet<long>(), new List<RunLeg>()));
    }

    // Every cell of every piece of the network; joining one through an open end is free.
    private static RouteEndpoint OfNetwork(RunKind kind, ThingId network, string name, HashSet<long> ignore)
    {
        List<SmallGrid> members = kind.Family.NetworkMembers(network);
        if (members.Count > RunPlanner.MaximumRemovals * 4)
        {
            throw ApiErrors.Refused("too_many_pieces", $"Network {network} has {members.Count} pieces.");
        }

        List<RouteEnd> ends = new List<RouteEnd>();
        foreach (SmallGrid member in members)
        {
            if (kind.Family.IsPiece(member) && !ignore.Contains(member.ReferenceId))
            {
                ends.AddRange(OfCells(kind, member, PieceShapes.Live(member).Cells).Ends);
            }
        }

        if (ends.Count == 0)
        {
            throw ApiErrors.Refused("network_empty", $"{name}: network {network} has no {kind.Noun} " +
                "piece to join.");
        }

        return new RouteEndpoint(ends, new List<long> { network.Value });
    }

    // A chute route runs with the items: it may not end at a port that pushes items out, nor start at one that takes
    // them in (the place tool's flow_conflict, refused up front).
    private static RouteEndpoint OfDevice(RunKind kind, Device device, int? port, int type, string name,
        HashSet<long> ignore, bool target)
    {
        List<int> candidates = new List<int>();
        if (device.OpenEnds != null)
        {
            for (int index = 0; index < device.OpenEnds.Count; index++)
            {
                Connection end = device.OpenEnds[index];
                if (end?.Transform != null && ((int)end.ConnectionType & type) != 0)
                {
                    candidates.Add(index);
                }
            }
        }

        if (port.HasValue && !candidates.Contains(port.Value))
        {
            throw ApiErrors.InvalidArgument(
                $"{name}.port {port.Value} is not a {kind.Noun} port of " +
                $"{device.PrefabName}; its ports of that kind are [{string.Join(", ", candidates)}] " +
                $"(inspect connections lists them).");
        }

        if (!port.HasValue && candidates.Count != 1)
        {
            throw ApiErrors.InvalidArgument(
                $"{device.PrefabName} {device.ReferenceId} has {candidates.Count} {kind.Noun} ports " +
                $"[{string.Join(", ", candidates)}]; name one with {name}.port, " +
                $"or several with {name}.ports " +
                $"(inspect connections or survey area lists them).");
        }

        int chosenPort = port ?? candidates[0];
        Connection chosen = device.OpenEnds![chosenPort];
        string? wrongWay = kind is ChuteRunKind
            ? ChuteRoles.WrongWay(
                TwoWayChutePorts.RoleOf(device, (int)chosen.ConnectionType, (int)chosen.ConnectionRole), target)
            : null;
        if (wrongWay != null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}: port {chosenPort} of {device.PrefabName} {device.ReferenceId} {wrongWay}; " +
                "a chute route runs with the items, so it " +
                $"{(target ? "ends at an input port" : "starts at an output port")} (its chute ports: " +
                $"[{string.Join(", ", candidates)}]; inspect connections lists their roles).");
        }

        GridCell cell = PieceShapes.Cell(chosen.GetLocalGrid());
        GridStep? into = GridStep.Between(cell, PieceShapes.Cell(chosen.GetFacingGrid()));
        SmallCell? small = GridController.World.GetSmallCell(chosen.GetLocalGrid());
        SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
        if (piece != null && !piece.IsBeingDestroyed && !ignore.Contains(piece.ReferenceId))
        {
            RouteEndpoint joined = OfCells(kind, piece, new List<GridCell> { cell });
            return new RouteEndpoint(joined.Ends, joined.Networks, into);
        }

        return new RouteEndpoint(RouteEnd.Open(cell), new List<long>(), into);
    }
}
