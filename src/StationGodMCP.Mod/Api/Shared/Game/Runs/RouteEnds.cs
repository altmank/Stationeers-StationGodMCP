#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The ends of a route from the caller's form: {at: position} (that small cell), {reference_id} of one of the kind's
/// pieces (its cell; leaving through one of its open ends is free, through another makes it a junction; a long
/// straight as a target offers every cell of it, and the place tool splits it), {reference_id, port} of a device (the
/// cell a piece joining that port stands in; port may be left out when the device has one port of the kind), and as a
/// target {network_id}: every cell of every piece of that network. {reference_id, port} of a thing of the kind with its
/// own ends that is neither a piece nor a device (an in-line tank, a passive vent; 1.3.5): the cell beyond its free end
/// (port names the end when it has several free). Starts may be several (up to MaximumStarts): an array of ends, or
/// {reference_id, ports: [...]} for several ports of one device. Pieces being removed (a reroute, assume_removed) count as gone.
/// </summary>
internal static class RouteEnds
{
    internal const double JunctionCost = 3.0;
    internal const int MaximumStarts = RouteTrees.MaximumStarts;

    /// <summary>One or more starts: an end, an array of ends, or a device with ports: [...].</summary>
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
                    $"{name}.ports lists 1 to {MaximumStarts} port indexes (and replaces port).");
            }

            for (int index = 0; index < ports.Count; index++)
            {
                JObject one = new JObject
                {
                    ["reference_id"] = item["reference_id"]?.DeepClone(),
                    ["port"] = ports[index].DeepClone()
                };
                starts.Add(Resolve(one, $"{name}.ports[{index}]", kind, type, ignore, false));
            }

            return starts;
        }

        starts.Add(Resolve(token, name, kind, type, ignore, false));
        return starts;
    }

    /// <summary>A target: any end form, {network_id}, or a long straight (every cell of it).</summary>
    internal static RouteEndpoint Target(JToken? token, string name, RunKind kind, int type, HashSet<long> ignore)
    {
        if (token is JObject item && item["network_id"] != null)
        {
            return OfNetwork(kind, NetworkHandles.Resolve(item["network_id"]!, $"{name}.network_id", kind.Family),
                name, ignore);
        }

        return Resolve(token, name, kind, type, ignore, true);
    }

    internal static RouteEndpoint Resolve(JToken? token, string name, RunKind kind, int type, HashSet<long> ignore,
        bool target)
    {
        if (!(token is JObject item))
        {
            throw ApiErrors.InvalidArgument($"{name} must be {{at: [x, y, z]}} or {{reference_id, port}}.");
        }

        Args fields = new Args(item);
        if (fields.Has("at"))
        {
            GridCell cell = RunArgs.CellOf(RunArgs.PositionOf(item["at"]!, $"{name}.at"));
            SmallCell? small = GridController.World.GetSmallCell(PieceShapes.Grid(cell));
            SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
            return piece != null && !piece.IsBeingDestroyed && !ignore.Contains(piece.ReferenceId)
                ? OfCells(kind, piece, new List<GridCell> { cell }, LeavesOnlyOpen(kind, target), name)
                : new RouteEndpoint(RouteEnd.Open(cell), new List<long>());
        }

        Thing thing = GameLookup.RequireThing(fields.ThingId("reference_id"));
        if (thing is SmallGrid own && kind.Family.IsPiece(own))
        {
            if (ignore.Contains(own.ReferenceId))
            {
                throw ApiErrors.InvalidArgument($"{name} names a piece being removed (the reroute's old run or assume_removed).");
            }

            PieceModel model = PieceShapes.Live(own);
            if (model.Cells.Count > 1 && !target)
            {
                throw ApiErrors.Refused("long_piece",
                    $"{own.PrefabName} {own.ReferenceId} is a long straight; start from one of its cells with " +
                    $"{name}.at, or make it the target (to), where the route may join any of its cells.");
            }

            return OfCells(kind, own, model.Cells, LeavesOnlyOpen(kind, target), name);
        }

        if (thing is Device device)
        {
            return OfDevice(kind, device, fields.OptionalInt("port", 0, 64), type, name, ignore, target);
        }

        if (thing is SmallGrid member && kind.Family.IsMember(member))
        {
            if (ignore.Contains(member.ReferenceId))
            {
                throw ApiErrors.InvalidArgument($"{name} names a thing being removed (assume_removed).");
            }

            return OfFreeEnd(kind, member, fields.OptionalInt("port", 0, 64), type, name, ignore);
        }

        throw ApiErrors.InvalidArgument(
            $"{name}: {thing.DisplayName} ({thing.PrefabName}) is neither a {kind.Noun} piece, a device, nor a " +
            $"{kind.Noun} thing with ends (an in-line tank, a passive vent).");
    }

    /// <summary>
    /// The small cell a piece joining a thing's end stands in, for reserve_ports: a device's port (its end's own cell,
    /// as grid_survey lists it), or one end of a network thing such as an in-line tank (the cell beyond that end).
    /// </summary>
    internal static GridCell JoiningCell(Thing thing, int port, string name)
    {
        Connection? end = thing is SmallGrid owner && owner.OpenEnds != null && port < owner.OpenEnds.Count
            ? owner.OpenEnds[port]
            : null;
        if (end?.Transform == null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}: {thing.DisplayName} ({thing.ReferenceId}) has no end {port} (connections lists its ends).");
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
            throw ApiErrors.InvalidArgument($"{name}: {member.PrefabName} {member.ReferenceId}: {error} " +
                                            "(connections lists its ends).");
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
        bool leavingOnly = false, string name = "from")
    {
        PieceModel model = PieceShapes.Live(piece);
        List<PieceModel> around = new List<PieceModel>();
        foreach (SmallGrid neighbour in LinkSurvey.Neighbourhood(new List<PieceModel> { model }).Values)
        {
            around.Add(PieceShapes.Live(neighbour));
        }

        List<PieceEnd> connected = Connectivity.ConnectedEnds(model, around);
        List<RouteEnd> ends = new List<RouteEnd>(cells.Count);
        foreach (GridCell cell in cells)
        {
            EndSet open = RouteEnd.OpenAt(model, cell, connected, leavingOnly);
            if (leavingOnly && open.Count == 0)
            {
                throw ApiErrors.InvalidArgument(
                    $"{name}: {piece.PrefabName} {piece.ReferenceId} has no open end items can leave it by at {cell}; " +
                    "a route from it would need a junction there, which only works where a route merges into a line " +
                    "(to). Start from a chute with an open output end, or from a device's output port.");
            }

            ends.Add(leavingOnly ? RouteEnd.Leaving(cell, open) : new RouteEnd(cell, open, JunctionCost));
        }

        IReferencable? network = kind.Family.NetworkOf(piece);
        List<long> networks = network != null ? new List<long> { network.ReferenceId } : new List<long>();
        return new RouteEndpoint(ends, networks);
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
            throw ApiErrors.Refused("network_empty", $"{name}: network {network} has no {kind.Noun} piece to join.");
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
                $"{name}.port {port.Value} is not a {kind.Noun} port of {device.PrefabName}; its ports of that kind " +
                $"are [{string.Join(", ", candidates)}] (connections lists them).");
        }

        if (!port.HasValue && candidates.Count != 1)
        {
            throw ApiErrors.InvalidArgument(
                $"{device.PrefabName} {device.ReferenceId} has {candidates.Count} {kind.Noun} ports " +
                $"[{string.Join(", ", candidates)}]; name one with {name}.port, or several with {name}.ports " +
                "(connections or grid_survey lists them).");
        }

        int chosenPort = port ?? candidates[0];
        Connection chosen = device.OpenEnds![chosenPort];
        string? wrongWay = kind is ChuteRunKind ? ChuteRoles.WrongWay((int)chosen.ConnectionRole, target) : null;
        if (wrongWay != null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}: port {chosenPort} of {device.PrefabName} {device.ReferenceId} {wrongWay}; a chute route " +
                $"runs with the items, so it {(target ? "ends at an input port" : "starts at an output port")} " +
                $"(its chute ports: [{string.Join(", ", candidates)}]; connections lists their roles).");
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
