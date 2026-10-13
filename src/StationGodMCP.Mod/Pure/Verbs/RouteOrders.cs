#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>A port a route keeps free (reserve_ports): a device, or a thing with ends, and the end's index.</summary>
internal sealed class RoutePort
{
    internal RoutePort(ThingId id, int port)
    {
        Id = id;
        Port = port;
    }

    public ThingId Id { get; }

    public int Port { get; }
}

/// <summary>
/// plan route's call, checked without the game: the kind and grade; the ends (start and end, a reroute of an old run
/// by its pieces or the two things it runs between, or a trunk every start branches from); the search (style,
/// axis_order, min_bends, max_length, margin, avoid, avoid_networks, reserve_cells and reserve_ports); what the build
/// it answers joins and lifts (join, join_to, join_trunk, allow, allow_bridge, split_long, root); the things planned as
/// if gone (assume_removed); who pays (materials); and how the build's report is answered (include, rows, summarize).
/// Positions, ends and handles stay as given: only the game resolves them.
/// </summary>
internal sealed class RouteOrder
{
    internal const int MaximumAvoidedNetworks = 256;
    internal const int MaximumRemovals = 1024;

    private const int MaximumPort = 64;

    private RouteOrder()
    {
    }

    public string Kind { get; private set; } = "cable";

    public string? Grade { get; private set; }

    /// <summary>One or more starts as given: {at}, {id}, {id, port}, {id, ports} or a list of them.</summary>
    public JToken? Start { get; private set; }

    /// <summary>The target as given: as one start, or {network}; null with a reroute or a trunk.</summary>
    public JToken? End { get; private set; }

    /// <summary>reroute {id: [...]}: the old run's pieces; null otherwise.</summary>
    public IReadOnlyList<ThingId>? ReroutePieces { get; private set; }

    /// <summary>reroute {between: [end, end]}: the two things the old run joins; null otherwise.</summary>
    public IReadOnlyList<RerouteEndArg>? RerouteBetween { get; private set; }

    /// <summary>The trunk's waypoints or cells as given (TrunkByCells says which); null without a trunk.</summary>
    public JToken? Trunk { get; private set; }

    public bool TrunkByCells { get; private set; }

    public RouteStyle Style { get; private set; } = RouteStyle.Parse(null)!;

    public AxisOrder AxisOrder { get; private set; }

    public bool MinBends { get; private set; }

    public int MaxLength { get; private set; }

    public double MarginM { get; private set; }

    public JoinMode Join { get; private set; }

    /// <summary>join as build takes it: ends, none or all.</summary>
    public string JoinWord { get; private set; } = "ends";

    /// <summary>allow as given, for build's options.</summary>
    public IReadOnlyList<string>? Allow { get; private set; }

    public bool AllowDoorKeepOut { get; private set; }

    public bool AllowSplit { get; private set; }

    public bool SplitLong { get; private set; } = true;

    public bool AvoidRoomInterior { get; private set; }

    public bool AvoidWalkways { get; private set; }

    /// <summary>avoid_networks true: keep beside no network but the ends' own.</summary>
    public bool AvoidAllNetworks { get; private set; }

    /// <summary>avoid_networks as a list: the networks to keep beside none of.</summary>
    public IReadOnlyList<NetworkTarget> AvoidNetworks { get; private set; } = new NetworkTarget[0];

    public IReadOnlyList<NetworkTarget> AllowBridge { get; private set; } = new NetworkTarget[0];

    /// <summary>allow_bridge as given, for build's options.</summary>
    public JToken? AllowBridgeGiven { get; private set; }

    public ThingId? Root { get; private set; }

    public NetworkTarget? JoinTo { get; private set; }

    public bool JoinTrunk { get; private set; }

    public JToken? ReserveCells { get; private set; }

    public IReadOnlyList<RoutePort> ReservePorts { get; private set; } = new RoutePort[0];

    public IReadOnlyList<ThingId> AssumeRemoved { get; private set; } = new ThingId[0];

    public MaterialsSource Materials { get; private set; } = MaterialsSource.Player;

    public Fields Fields { get; private set; } = Fields.None;

    public bool Summarize { get; private set; }

    public int Rows { get; private set; }

    /// <summary>Whether the call keeps any cell free for another run.</summary>
    public bool Reserves => ReserveCells != null || ReservePorts.Count > 0;

    /// <summary>The call's order, or null with every problem added.</summary>
    internal static RouteOrder? Of(PlanRouteOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        RouteOrder order = new RouteOrder
        {
            Kind = (options.Kind ?? "cable").Trim().ToLowerInvariant(),
            Grade = options.Grade?.Trim().ToLowerInvariant(),
            Start = options.Start,
            End = options.End,
            AxisOrder = AxisOf(options.AxisOrder),
            MinBends = options.MinBends ?? false,
            MaxLength = (int)(options.MaxLength ?? 400),
            MarginM = options.Margin ?? 6.0,
            JoinWord = (options.Join ?? "ends").Trim().ToLowerInvariant(),
            Allow = options.Allow,
            AllowDoorKeepOut = Has(options.Allow, "door_keepout"),
            AllowSplit = Has(options.Allow, "split"),
            SplitLong = options.SplitLong ?? true,
            AvoidRoomInterior = Has(options.Avoid, "room_interior"),
            AvoidWalkways = Has(options.Avoid, "walkways"),
            AllowBridge = Targets(options.AllowBridge, "allow_bridge", problems),
            AllowBridgeGiven = options.AllowBridge,
            JoinTrunk = options.JoinTrunk ?? false,
            ReserveCells = options.ReserveCells,
            ReservePorts = Ports(options.ReservePorts, problems),
            AssumeRemoved = Ids(options.AssumeRemoved, "assume_removed", problems),
            Materials = options.Materials,
            Fields = options.Fields,
            Summarize = options.Summarize ?? false,
            Rows = (int)(options.Rows ?? 5)
        };
        order.Join = order.JoinWord switch
        {
            "none" => JoinMode.None,
            "all" => JoinMode.All,
            _ => JoinMode.Ends
        };
        order.Style = RouteStyle.Parse(options.Style) ?? order.Style;
        order.ReadAvoidedNetworks(options.AvoidNetworks, problems);
        order.ReadRoot(options.Root, problems);
        order.ReadJoinTo(options.JoinTo, problems);
        order.ReadEnds(options, problems);
        return problems.Count == before ? order : null;
    }

    private void ReadEnds(PlanRouteOptions options, List<OptionProblem> problems)
    {
        if (options.Reroute != null)
        {
            if (Start != null || End != null)
            {
                problems.Add(new OptionProblem("reroute", "takes its ends from the old run; leave out start and end"));
            }

            ReadReroute(options.Reroute, problems);
        }

        if (options.Trunk != null)
        {
            if (End != null || options.Reroute != null)
            {
                problems.Add(new OptionProblem("trunk",
                    "replaces end and does not go with reroute: every start branches from the trunk, whose own ends " +
                    "join what they meet"));
            }

            if (Start == null)
            {
                problems.Add(new OptionProblem("trunk", "needs start: the drops (ports or cells) that branch from it"));
            }

            ReadTrunk(options.Trunk, problems);
        }

        if (options.Reroute == null && options.Trunk == null)
        {
            if (Start == null)
            {
                problems.Add(new OptionProblem("start",
                    "is required: {at}, {id} of a piece, {id, port} or {id, ports: [...]} of a device, or a list (or give reroute)"));
            }

            if (End == null)
            {
                problems.Add(new OptionProblem("end",
                    "is required: {at}, {id}, {id, port} or {network} (or give trunk or reroute)"));
            }
        }
    }

    private void ReadReroute(JToken reroute, List<OptionProblem> problems)
    {
        JToken? pieces = (reroute as JObject)?["id"];
        JToken? between = (reroute as JObject)?["between"];
        if ((pieces == null) == (between == null))
        {
            problems.Add(new OptionProblem("reroute",
                "needs id (the old run's pieces) or between: [end, end] (ids, or {id, port}), one of them"));
            return;
        }

        if (between != null)
        {
            RerouteBetween = RerouteArgs.Between(between, problems);
            return;
        }

        List<string> ids = new List<string>();
        foreach (JToken id in pieces is JArray list ? list : new JArray(pieces!))
        {
            ids.Add(id.Type == JTokenType.String ? (string)id! : id.ToString());
        }

        ReroutePieces = ids.Count is > 0 and <= MaximumRemovals
            ? Ids(ids, "reroute.id", problems)
            : Refused<ThingId>(problems, "reroute.id", $"lists 1 to {MaximumRemovals} pieces");
    }

    private void ReadTrunk(JToken trunk, List<OptionProblem> problems)
    {
        JToken? waypoints = (trunk as JObject)?["waypoints"];
        JToken? cells = (trunk as JObject)?["cells"];
        if ((waypoints == null) == (cells == null))
        {
            problems.Add(new OptionProblem("trunk", "needs waypoints or cells (one of them)"));
            return;
        }

        Trunk = waypoints ?? cells;
        TrunkByCells = cells != null;
    }

    private void ReadAvoidedNetworks(JToken? avoid, List<OptionProblem> problems)
    {
        if (avoid == null)
        {
            return;
        }

        if (avoid.Type == JTokenType.Boolean)
        {
            AvoidAllNetworks = (bool)avoid;
            return;
        }

        AvoidNetworks = avoid is JArray { Count: > MaximumAvoidedNetworks }
            ? Refused<NetworkTarget>(problems, "avoid_networks", $"lists at most {MaximumAvoidedNetworks} networks")
            : Targets(avoid, "avoid_networks", problems);
    }

    private void ReadRoot(string? root, List<OptionProblem> problems)
    {
        if (root == null)
        {
            return;
        }

        if (ThingId.TryRead(new JValue(root), out ThingId id))
        {
            Root = id;
        }
        else
        {
            problems.Add(new OptionProblem("root", "must be a device's id"));
        }
    }

    private void ReadJoinTo(JToken? joinTo, List<OptionProblem> problems)
    {
        if (joinTo == null)
        {
            return;
        }

        JoinTo = NetworkTarget.Of(joinTo);
        if (JoinTo == null)
        {
            problems.Add(new OptionProblem("join_to", "must be a network's id, a piece or device on it, or {id, port}"));
        }
    }

    private static AxisOrder AxisOf(string? order) => (order ?? "any").Trim().ToLowerInvariant() switch
    {
        "vertical_first" => AxisOrder.VerticalFirst,
        "horizontal_first" => AxisOrder.HorizontalFirst,
        _ => AxisOrder.Any
    };

    private static bool Has(IReadOnlyList<string>? words, string word)
    {
        foreach (string given in words ?? new string[0])
        {
            if (string.Equals(given.Trim(), word, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Each entry a network handle: a network's id, a piece or device on it, or {id, port}.
    private static IReadOnlyList<NetworkTarget> Targets(JToken? given, string option, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return new NetworkTarget[0];
        }

        if (!(given is JArray list))
        {
            return Refused<NetworkTarget>(problems, option, "must be a list of networks");
        }

        List<NetworkTarget> targets = new List<NetworkTarget>(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            if (NetworkTarget.Of(list[index]) is { } target)
            {
                targets.Add(target);
            }
            else
            {
                problems.Add(new OptionProblem($"{option}[{index}]",
                    "must be a network's id, a piece or device on it, or {id, port}"));
            }
        }

        return targets;
    }

    // reserve_ports: each {id, port}, the port an end's index.
    private static IReadOnlyList<RoutePort> Ports(JToken? given, List<OptionProblem> problems)
    {
        List<RoutePort> ports = new List<RoutePort>();
        int index = 0;
        foreach (JToken entry in given as JArray ?? new JArray())
        {
            string path = $"reserve_ports[{index++}]";
            JToken? port = (entry as JObject)?["port"];
            if (!(entry is JObject item) || !ThingId.TryRead(item["id"], out ThingId id))
            {
                problems.Add(new OptionProblem(path, "must be {id, port}"));
            }
            else if (port == null || port.Type != JTokenType.Integer || port.Value<long>() is < 0 or > MaximumPort)
            {
                problems.Add(new OptionProblem(path + ".port",
                    $"is required, an end's index 0 to {MaximumPort} (inspect connections lists the ends)"));
            }
            else
            {
                ports.Add(new RoutePort(id, port.Value<int>()));
            }
        }

        return ports;
    }

    private static IReadOnlyList<ThingId> Ids(IReadOnlyList<string>? given, string option, List<OptionProblem> problems)
    {
        List<ThingId> ids = new List<ThingId>();
        int index = 0;
        foreach (string text in given ?? new string[0])
        {
            if (ThingId.TryRead(new JValue(text), out ThingId id))
            {
                ids.Add(id);
            }
            else
            {
                problems.Add(new OptionProblem($"{option}[{index}]", "must be an id"));
            }

            index++;
        }

        return ids;
    }

    private static T[] Refused<T>(List<OptionProblem> problems, string option, string problem)
    {
        problems.Add(new OptionProblem(option, problem));
        return new T[0];
    }
}
