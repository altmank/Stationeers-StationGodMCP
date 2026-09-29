#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The networks around an edit before and after it, read from the game and forecast on plain values
/// (NetworkForecaster): the links the edit makes and ends, every network it touches, each network it would leave
/// with its guard numbers, and for the check after the run, a piece of each network before that stays and where
/// every device port should end up.
/// </summary>
internal sealed class RunForecast
{
    internal RunForecast(Forecast result, Dictionary<long, int> componentOf, RunNetworkContext context)
    {
        Result = result;
        ComponentOf = componentOf;
        Context = context;
    }

    internal Forecast Result { get; }

    /// <summary>Each node (piece, whole network, new piece) to its ForecastNetwork index.</summary>
    internal Dictionary<long, int> ComponentOf { get; }

    internal RunNetworkContext Context { get; }

    /// <summary>The game's links now among the things around the edit that touch a changed or removed piece.</summary>
    internal HashSet<Link> GameBefore { get; set; } = new HashSet<Link>();

    /// <summary>Where the model's reading of those links differs from the game's (must be empty).</summary>
    internal LinkDiff ModelCheck { get; set; } = new LinkDiff(new List<Link>(), new List<Link>());

    /// <summary>The links predicted after the edit that touch a new or changed piece.</summary>
    internal HashSet<Link> After { get; set; } = new HashSet<Link>();

    internal List<long> Unreadable { get; } = new List<long>();

    /// <summary>Each device port the edit may affect, with the device.</summary>
    internal List<KeyValuePair<Device, ForecastPort>> Ports { get; } = new List<KeyValuePair<Device, ForecastPort>>();

    /// <summary>For each network before the edit, a piece of it that stays unchanged (for the check after).</summary>
    internal Dictionary<long, SmallGrid> Representatives { get; } = new Dictionary<long, SmallGrid>();

    /// <summary>Each forecast network's guard, by index.</summary>
    internal Dictionary<int, KindGuard> Guards { get; } = new Dictionary<int, KindGuard>();

    /// <summary>New links that join two things already joined another way (RunLoops): each closes a loop.</summary>
    internal List<Link> Loops { get; set; } = new List<Link>();

    /// <summary>The node a thing stands for in the forecast graph (its whole network, or itself).</summary>
    internal Dictionary<long, long> NodeOf { get; } = new Dictionary<long, long>();
}

/// <summary>Builds the RunForecast of a plan whose cells and removals are known.</summary>
internal static class RunForecastBuilder
{
    internal static RunForecast Build(RunPlan plan)
    {
        RunKind kind = plan.Request.Kind;
        UpgradeFamily family = kind.Family;
        HashSet<long> removed = plan.RemovedIds();
        Dictionary<long, PieceModel> planned = new Dictionary<long, PieceModel>();
        List<PieceModel> reach = new List<PieceModel>();
        foreach (PlannedCell cell in plan.Cells)
        {
            planned[cell.ForecastId] = cell.Model;
            reach.Add(cell.Model);
        }

        foreach (PlannedRemoval removal in plan.Removals)
        {
            reach.Add(removal.Live);
        }

        // Networks losing pieces are modelled piece by piece; every other network stays one node.
        HashSet<long> modelled = new HashSet<long>();
        RunNetworkContext context = new RunNetworkContext();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            context.Removed[removal.Piece.ReferenceId] = removal.Piece;
            if (removal.Network != null)
            {
                modelled.Add(removal.Network.ReferenceId);
                context.NetworksBefore[removal.Network.ReferenceId] = removal.Network;
            }
        }

        Dictionary<long, SmallGrid> things = LinkSurvey.Neighbourhood(reach);
        foreach (long network in modelled)
        {
            foreach (SmallGrid member in family.NetworkMembers(new ThingId(network)))
            {
                things[member.ReferenceId] = member;
            }
        }

        NetworkEdit edit = new NetworkEdit();
        RunForecast forecastParts = new RunForecast(new Forecast(), new Dictionary<long, int>(), context);
        Dictionary<long, PieceModel> after = new Dictionary<long, PieceModel>();
        List<PieceModel> liveModels = new List<PieceModel>();
        HashSet<long> focusBefore = new HashSet<long>(removed);
        foreach (PlannedCell cell in plan.Cells)
        {
            if (cell.IsChange)
            {
                focusBefore.Add(cell.Existing!.ReferenceId);
            }
        }

        foreach (SmallGrid thing in things.Values)
        {
            plan.Things[thing.ReferenceId] = thing;
            PieceModel live = PieceShapes.Live(thing);
            liveModels.Add(live);
            if (!removed.Contains(thing.ReferenceId))
            {
                after[thing.ReferenceId] = planned.TryGetValue(thing.ReferenceId, out PieceModel change)
                    ? change
                    : live;
            }
        }

        foreach (KeyValuePair<long, PieceModel> entry in planned)
        {
            after[entry.Key] = entry.Value;
        }

        // The game's links now around changed and removed pieces, and the model's reading of them.
        forecastParts.GameBefore = LinkSurvey.GameLinks(things, focusBefore, forecastParts.Unreadable);
        forecastParts.ModelCheck = Connectivity.Compare(forecastParts.GameBefore,
            Connectivity.LinksTouching(liveModels, focusBefore));

        // Graph nodes: pieces of modelled networks, new pieces, and whole networks of everything else.
        foreach (SmallGrid thing in things.Values)
        {
            if (removed.Contains(thing.ReferenceId) || !family.IsMember(thing))
            {
                continue;
            }

            NodeFor(thing, family, modelled, edit, context, forecastParts, planned.ContainsKey(thing.ReferenceId));
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            if (!cell.IsChange)
            {
                edit.AddPiece(cell.ForecastId, null);
                forecastParts.NodeOf[cell.ForecastId] = cell.ForecastId;
                context.NewPieces[cell.ForecastId] = cell.Choice.Prefab;
            }
        }

        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (removal.Network != null)
            {
                edit.Remove(removal.Piece.ReferenceId, removal.Network.ReferenceId);
            }
        }

        // Links after: everything touching a new, changed or modelled piece.
        HashSet<long> focusAfter = new HashSet<long>(planned.Keys);
        foreach (KeyValuePair<long, long> node in forecastParts.NodeOf)
        {
            if (node.Key == node.Value)
            {
                focusAfter.Add(node.Key);
            }
        }

        List<PieceModel> afterModels = new List<PieceModel>(after.Values);
        HashSet<Link> links = Connectivity.LinksTouching(afterModels, focusAfter);
        forecastParts.After = Connectivity.LinksTouching(afterModels, new HashSet<long>(planned.Keys));
        foreach (Link link in links)
        {
            if (forecastParts.NodeOf.TryGetValue(link.From, out long from) &&
                forecastParts.NodeOf.TryGetValue(link.To, out long to))
            {
                edit.Link(from, to);
            }
        }

        forecastParts.Loops = RunLoops.Closing(links, forecastParts.NodeOf, forecastParts.GameBefore,
            planned.Keys);
        AddPorts(plan, kind, things, after, edit, forecastParts, removed);
        Forecast result = NetworkForecaster.Of(edit, out Dictionary<long, int> componentOf);
        RunForecast forecast = new RunForecast(result, componentOf, context)
        {
            GameBefore = forecastParts.GameBefore,
            ModelCheck = forecastParts.ModelCheck,
            After = forecastParts.After,
            Loops = forecastParts.Loops
        };
        forecast.Unreadable.AddRange(forecastParts.Unreadable);
        forecast.Ports.AddRange(forecastParts.Ports);
        foreach (KeyValuePair<long, long> node in forecastParts.NodeOf)
        {
            forecast.NodeOf[node.Key] = node.Value;
        }

        foreach (KeyValuePair<long, SmallGrid> representative in forecastParts.Representatives)
        {
            forecast.Representatives[representative.Key] = representative.Value;
        }

        foreach (ForecastNetwork network in result.Networks)
        {
            forecast.Guards[network.Index] = kind.GuardOf(network, context);
        }

        return forecast;
    }

    // A member of a modelled network is its own node; any other member stands for its whole network.
    private static void NodeFor(SmallGrid thing, UpgradeFamily family, HashSet<long> modelled, NetworkEdit edit,
        RunNetworkContext context, RunForecast parts, bool changed)
    {
        IReferencable? network = family.NetworkOf(thing);
        if (network == null)
        {
            edit.AddPiece(thing.ReferenceId, null);
            parts.NodeOf[thing.ReferenceId] = thing.ReferenceId;
            return;
        }

        context.NetworksBefore[network.ReferenceId] = network;
        if (modelled.Contains(network.ReferenceId))
        {
            edit.AddPiece(thing.ReferenceId, network.ReferenceId);
            parts.NodeOf[thing.ReferenceId] = thing.ReferenceId;
        }
        else
        {
            edit.AddNetwork(network.ReferenceId);
            parts.NodeOf[thing.ReferenceId] = network.ReferenceId;
        }

        if (!changed && !parts.Representatives.ContainsKey(network.ReferenceId))
        {
            parts.Representatives[network.ReferenceId] = thing;
        }
    }

    // Every port of the run's type of each device next to the edit or on a network the edit touches (modelled or
    // joined whole, so networks_after lists every device a merged network holds): the network it is on now (the
    // game's IsConnected), and the node joined to it after (the model in its cell with an end facing it).
    private static void AddPorts(RunPlan plan, RunKind kind, Dictionary<long, SmallGrid> things,
        Dictionary<long, PieceModel> after, NetworkEdit edit, RunForecast parts, HashSet<long> removed)
    {
        Dictionary<long, Device> devices = new Dictionary<long, Device>();
        foreach (SmallGrid thing in things.Values)
        {
            if (thing is Device device && !kind.Family.IsMember(device) && !kind.Family.IsMountedOn(device))
            {
                devices[device.ReferenceId] = device;
            }
        }

        foreach (IReferencable network in new List<IReferencable>(parts.Context.NetworksBefore.Values))
        {
            foreach (Device device in kind.DevicesOf(network))
            {
                devices[device.ReferenceId] = device;
            }
        }

        Dictionary<GridCell, PieceModel> byCell = new Dictionary<GridCell, PieceModel>();
        foreach (PieceModel model in after.Values)
        {
            if (!things.TryGetValue(model.Id, out SmallGrid thing) || kind.Family.IsMember(thing))
            {
                foreach (GridCell cell in model.Cells)
                {
                    byCell[cell] = model;
                }
            }
        }

        int type = plan.Request.Build != null ? kind.EndType(plan.Request.Build.Grade) : kind.AnyEndType;
        GridController world = GridController.World;
        foreach (Device device in devices.Values)
        {
            if (device.OpenEnds == null)
            {
                continue;
            }

            plan.Things[device.ReferenceId] = device;
            for (int index = 0; index < device.OpenEnds.Count; index++)
            {
                Connection end = device.OpenEnds[index];
                if (end?.Transform == null || ((int)end.ConnectionType & type) == 0)
                {
                    continue;
                }

                GridCell local = PieceShapes.Cell(end.GetLocalGrid());
                GridCell facing = PieceShapes.Cell(end.GetFacingGrid());
                long? before = NetworkBefore(kind, world.GetSmallCell(end.GetLocalGrid()), end, removed, parts);
                long? attached = AttachedAfter(byCell, local, facing, (int)end.ConnectionType, parts, kind, edit,
                    removed);
                if (before == null && attached == null)
                {
                    continue;
                }

                ForecastPort port = new ForecastPort(device.ReferenceId, index, kind.Bridges(end), before, attached);
                edit.Ports.Add(port);
                parts.Ports.Add(new KeyValuePair<Device, ForecastPort>(device, port));
            }
        }
    }

    private static long? NetworkBefore(RunKind kind, SmallCell? cell, Connection end, HashSet<long> removed,
        RunForecast parts)
    {
        SmallGrid? piece = cell != null ? kind.SlotOf(cell) : null;
        if (piece == null || piece.IsBeingDestroyed || !piece.IsConnected(end))
        {
            return null;
        }

        IReferencable? network = kind.Family.NetworkOf(piece);
        if (network != null)
        {
            parts.Context.NetworksBefore[network.ReferenceId] = network;
        }

        return network?.ReferenceId;
    }

    // The model standing in the port's cell after the edit, when one of its ends of a shared type faces the device.
    private static long? AttachedAfter(Dictionary<GridCell, PieceModel> byCell, GridCell local, GridCell facing,
        int type, RunForecast parts, RunKind kind, NetworkEdit edit, HashSet<long> removed)
    {
        if (!byCell.TryGetValue(local, out PieceModel model))
        {
            SmallCell? cell = GridController.World.GetSmallCell(PieceShapes.Grid(local));
            SmallGrid? piece = cell != null ? kind.SlotOf(cell) : null;
            if (piece == null || piece.IsBeingDestroyed || removed.Contains(piece.ReferenceId))
            {
                return null;
            }

            model = PieceShapes.Live(piece);
            if (!parts.NodeOf.ContainsKey(piece.ReferenceId))
            {
                IReferencable? network = kind.Family.NetworkOf(piece);
                if (network == null)
                {
                    return null;
                }

                parts.Context.NetworksBefore[network.ReferenceId] = network;
                parts.NodeOf[piece.ReferenceId] = network.ReferenceId;
                edit.AddNetwork(network.ReferenceId);
            }
        }

        foreach (PieceEnd end in model.Ends)
        {
            if ((end.Type & type) != 0 && end.Local.Equals(facing))
            {
                return parts.NodeOf.TryGetValue(model.Id, out long node) ? node : (long?)null;
            }
        }

        return null;
    }
}
