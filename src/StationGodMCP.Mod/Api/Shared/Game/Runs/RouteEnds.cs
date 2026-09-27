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
/// target {network_id}: every cell of every piece of that network. Starts may be several (up to MaximumStarts): an array of ends, or
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
            return OfNetwork(kind, new Args(item).ThingId("network_id"), name, ignore);
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
                ? OfCells(kind, piece, new List<GridCell> { cell })
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

            return OfCells(kind, own, model.Cells);
        }

        if (thing is Device device)
        {
            return OfDevice(kind, device, fields.OptionalInt("port", 0, 64), type, name, ignore);
        }

        throw ApiErrors.InvalidArgument(
            $"{name}: {thing.DisplayName} ({thing.PrefabName}) is neither a {kind.Noun} piece nor a device.");
    }

    // Each cell of the piece: leaving through an open end there is free, any other way makes a junction.
    private static RouteEndpoint OfCells(RunKind kind, SmallGrid piece, IReadOnlyList<GridCell> cells)
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
            EndSet open = EndSet.None;
            foreach (GridStep step in EndSet.AtCell(model, cell).Steps())
            {
                if (!connected.Exists(end => end.Local.Equals(step.From(cell))))
                {
                    open = open.With(step);
                }
            }

            ends.Add(new RouteEnd(cell, open, JunctionCost));
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

    private static RouteEndpoint OfDevice(RunKind kind, Device device, int? port, int type, string name,
        HashSet<long> ignore)
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

        Connection chosen = device.OpenEnds![port ?? candidates[0]];
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
