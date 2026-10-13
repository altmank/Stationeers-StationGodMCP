#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The one target selector (PLAN 2.0, shared forms): ids, and the filters and places that pick targets: near with
/// radius, within, box, room, network, prefab and name. Each is null when the call gives none or the what does not
/// take it. Every filter but ids is a spread (catalogue x-target role spread, decision M6).
/// </summary>
internal sealed class Selector
{
    internal Selector(PerIdList? ids, NearPoint? near, double? radius, string? within, BoxCorners? box, string? room,
        NetworkTarget? network, PrefabFilter? prefab, string? name)
    {
        Ids = ids;
        Near = near;
        Radius = radius;
        Within = within;
        Box = box;
        Room = room;
        Network = network;
        Prefab = prefab;
        Name = name;
    }

    public PerIdList? Ids { get; }

    public NearPoint? Near { get; }

    public double? Radius { get; }

    public string? Within { get; }

    public BoxCorners? Box { get; }

    public string? Room { get; }

    public NetworkTarget? Network { get; }

    public PrefabFilter? Prefab { get; }

    public string? Name { get; }

    internal static Selector None { get; } = new Selector(null, null, null, null, null, null, null, null, null);

    /// <summary>
    /// network as a list (where the what takes several, as survey area does): every network named, in order; null
    /// when the call gives none or gives one (Network).
    /// </summary>
    internal IReadOnlyList<NetworkTarget>? Networks { get; private set; }

    internal static Selector Bind(OptionReader options)
    {
        IReadOnlyList<NetworkTarget>? networks = NetworkTarget.BindList(options);
        return new Selector(
            PerIdList.Bind(options),
            NearPoint.Bind(options),
            options.Number("radius"),
            Trimmed(options.Text("within")),
            BoxCorners.Bind(options),
            Trimmed(options.Text("room")),
            networks == null ? NetworkTarget.Bind(options) : null,
            PrefabFilter.Bind(options),
            Trimmed(options.Text("name"))) { Networks = networks };
    }

    /// <summary>True when the call picks targets by a filter or place rather than (or beside) ids.</summary>
    internal bool HasSpread =>
        Near != null || Within != null || Box != null || Room != null || Network != null || Prefab != null || Name != null;

    /// <summary>
    /// The area near and radius (a sphere around the centre near resolves to) or box give; anywhere when neither is
    /// given. centre is the point near names, which only the game can find for player, looking_at or a thing.
    /// </summary>
    internal PointArea AreaAround(Vec3? centre)
    {
        if (Box != null)
        {
            return PointArea.Box(Box.First, Box.Second);
        }

        Vec3? point = Near is NearPoint.At at ? at.Point : centre;
        return point is { } found && Radius is { } radius ? PointArea.Near(found, radius) : PointArea.Anywhere;
    }

    /// <summary>
    /// What is wrong with the area options as a what that takes near with radius, or box, reads them: near and radius
    /// go together, at most maximumRadius metres around anything but the player, and box is the other area, not both.
    /// </summary>
    internal List<OptionProblem> AreaProblems(double maximumRadius)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        if (Near != null && Radius == null)
        {
            problems.Add(new OptionProblem("near", "needs radius, in metres"));
        }

        if (Radius != null && Near == null)
        {
            problems.Add(new OptionProblem("radius", "needs near"));
        }

        if (Near != null && Box != null)
        {
            problems.Add(new OptionProblem("box", "goes without near and radius"));
        }

        if (Near is not (null or NearPoint.Player) && Radius > maximumRadius)
        {
            problems.Add(new OptionProblem("radius",
                "is at most " + maximumRadius.ToString(CultureInfo.InvariantCulture) + " around a point"));
        }

        return problems;
    }

    /// <summary>
    /// What is wrong with near and radius as a what that measures only from the player reads them: near "player" goes
    /// with radius, in metres; any other near is a problem.
    /// </summary>
    internal List<OptionProblem> PlayerRadiusProblems()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        if (Near is not (null or NearPoint.Player))
        {
            problems.Add(new OptionProblem("near", "is \"player\" only here"));
        }
        else if (Near != null && Radius == null)
        {
            problems.Add(new OptionProblem("near", "needs radius, in metres"));
        }

        if (Radius != null && Near == null)
        {
            problems.Add(new OptionProblem("radius", "needs near \"player\""));
        }

        return problems;
    }

    /// <summary>The metres around the player near "player" with radius keeps; null for any other selector.</summary>
    internal double? AroundPlayerM => Near is NearPoint.Player ? Radius : null;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
}

/// <summary>near: a point, the player, the thing the crosshair is on, or a thing by id (its position).</summary>
internal abstract class NearPoint
{
    private NearPoint()
    {
    }

    internal sealed class Player : NearPoint
    {
    }

    internal sealed class LookingAt : NearPoint
    {
    }

    internal sealed class At : NearPoint
    {
        internal At(Vec3 point)
        {
            Point = point;
        }

        public Vec3 Point { get; }
    }

    internal sealed class Thing : NearPoint
    {
        internal Thing(string id)
        {
            Id = id;
        }

        public string Id { get; }
    }

    internal static NearPoint? Bind(OptionReader options)
    {
        JToken? given = options.Raw("near");
        switch (given)
        {
            case null:
                return null;
            case JArray point when Point.Of(point) is { } at:
                return new At(at);
            case JValue { Type: JTokenType.String } text:
                string word = ((string)text!).Trim();
                if (string.Equals(word, "player", StringComparison.OrdinalIgnoreCase))
                {
                    return new Player();
                }

                if (string.Equals(word, "looking_at", StringComparison.OrdinalIgnoreCase))
                {
                    return new LookingAt();
                }

                if (word.Length > 0)
                {
                    return new Thing(word);
                }

                break;
        }

        options.Problem("near", "must be [x, y, z], \"player\", \"looking_at\" or a thing's id");
        return null;
    }
}

/// <summary>box: two corners [[x, y, z], [x, y, z]], in either order.</summary>
internal sealed class BoxCorners
{
    internal BoxCorners(Vec3 first, Vec3 second)
    {
        First = first;
        Second = second;
    }

    public Vec3 First { get; }

    public Vec3 Second { get; }

    internal static BoxCorners? Bind(OptionReader options)
    {
        JToken? given = options.Raw("box");
        if (given == null)
        {
            return null;
        }

        if (given is JArray { Count: 2 } corners && corners[0] is JArray first && corners[1] is JArray second &&
            Point.Of(first) is { } a && Point.Of(second) is { } b)
        {
            return new BoxCorners(a, b);
        }

        options.Problem("box", "must be two corners [[x, y, z], [x, y, z]]");
        return null;
    }
}

/// <summary>network: a network's id, a piece or device on it, or {id, port} (the network on one of a device's ports).</summary>
internal sealed class NetworkTarget
{
    internal NetworkTarget(string id, string? port)
    {
        Id = id;
        Port = port;
    }

    public string Id { get; }

    public string? Port { get; }

    private const string Form = "must be a network's id, a piece or device on it, or {id, port}";

    internal static NetworkTarget? Bind(OptionReader options)
    {
        JToken? given = options.Raw("network");
        if (given == null)
        {
            return null;
        }

        NetworkTarget? target = Of(given);
        if (target == null)
        {
            options.Problem("network", Form);
        }

        return target;
    }

    /// <summary>network given as a list: each entry one network (Of); null when it is not a list.</summary>
    internal static IReadOnlyList<NetworkTarget>? BindList(OptionReader options)
    {
        if (options.Raw("network") is not JArray given)
        {
            return null;
        }

        List<NetworkTarget> targets = new List<NetworkTarget>(given.Count);
        for (int index = 0; index < given.Count; index++)
        {
            if (Of(given[index]) is { } target)
            {
                targets.Add(target);
            }
            else
            {
                options.Problem($"network[{index}]", Form);
            }
        }

        return targets;
    }

    /// <summary>
    /// One network as given: a network's id, a piece or device on it, or {id, port}; null for anything else.
    /// </summary>
    internal static NetworkTarget? Of(JToken given)
    {
        switch (given)
        {
            case JValue { Type: JTokenType.String } text when ((string)text!).Trim().Length > 0:
                return new NetworkTarget(((string)text!).Trim(), null);
            case JObject pair when pair["id"] is JValue { Type: JTokenType.String } id && ((string)id!).Trim().Length > 0:
                JToken? port = pair["port"];
                return new NetworkTarget(((string)id!).Trim(),
                    port == null || port.Type == JTokenType.Null ? null : port.ToString().Trim());
            default:
                return null;
        }
    }

    /// <summary>The target as a call gives it: the id, or {id, port} with the port as a number when it reads as one.</summary>
    internal JToken ToJson()
    {
        if (Port == null)
        {
            return new JValue(Id);
        }

        return new JObject
        {
            ["id"] = Id,
            ["port"] = int.TryParse(Port, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int port)
                ? new JValue(port)
                : new JValue(Port)
        };
    }
}

/// <summary>
/// prefab: whole names, hashes, or parts with * ('*Battery*', '*Tank*Insulated*': each part, in any order), one or a
/// list. A thing passes when it matches any of them; names match in any case.
/// </summary>
internal sealed class PrefabFilter
{
    internal PrefabFilter(IReadOnlyList<PrefabPattern> patterns)
    {
        Patterns = patterns;
    }

    public IReadOnlyList<PrefabPattern> Patterns { get; }

    internal static PrefabFilter? Bind(OptionReader options)
    {
        JToken? given = options.Raw("prefab");
        if (given == null)
        {
            return null;
        }

        List<PrefabPattern> patterns = new List<PrefabPattern>();
        bool list = given is JArray;
        int index = 0;
        foreach (JToken entry in list ? (IEnumerable<JToken>)given : new[] { given })
        {
            PrefabPattern? pattern = PrefabPattern.Of(entry);
            if (pattern == null)
            {
                options.Problem(list ? $"prefab[{index}]" : "prefab", "must be a prefab's name, its hash, or parts with *");
            }
            else
            {
                patterns.Add(pattern);
            }

            index++;
        }

        return patterns.Count == 0 ? null : new PrefabFilter(patterns);
    }

    internal bool Keeps(string? prefabName, int prefabHash)
    {
        foreach (PrefabPattern pattern in Patterns)
        {
            if (pattern.Keeps(prefabName, prefabHash))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One prefab pattern: a hash, a whole name, or the parts a name must hold.</summary>
internal abstract class PrefabPattern
{
    private PrefabPattern()
    {
    }

    internal abstract bool Keeps(string? prefabName, int prefabHash);

    internal sealed class Hash : PrefabPattern
    {
        internal Hash(int value)
        {
            Value = value;
        }

        public int Value { get; }

        internal override bool Keeps(string? prefabName, int prefabHash) => prefabHash == Value;
    }

    internal sealed class Whole : PrefabPattern
    {
        internal Whole(string name)
        {
            Name = name;
        }

        public string Name { get; }

        internal override bool Keeps(string? prefabName, int prefabHash) =>
            string.Equals(prefabName, Name, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class Parts : PrefabPattern
    {
        internal Parts(IReadOnlyList<string> pieces)
        {
            Pieces = pieces;
        }

        public IReadOnlyList<string> Pieces { get; }

        internal override bool Keeps(string? prefabName, int prefabHash)
        {
            if (prefabName == null)
            {
                return false;
            }

            foreach (string piece in Pieces)
            {
                if (prefabName.IndexOf(piece, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal static PrefabPattern? Of(JToken entry)
    {
        if (entry.Type == JTokenType.Integer)
        {
            long hash = (long)entry;
            return hash is >= int.MinValue and <= int.MaxValue ? new Hash((int)hash) : null;
        }

        if (entry.Type != JTokenType.String)
        {
            return null;
        }

        string text = ((string)entry!).Trim();
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
        {
            return new Hash(parsed);
        }

        if (text.IndexOf('*') < 0)
        {
            return text.Length == 0 ? null : new Whole(text);
        }

        List<string> pieces = new List<string>();
        foreach (string piece in text.Split('*'))
        {
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }
        }

        return pieces.Count == 0 ? null : new Parts(pieces);
    }
}

/// <summary>[x, y, z] as a point.</summary>
internal static class Point
{
    internal static Vec3? Of(JArray given)
    {
        if (given.Count != 3)
        {
            return null;
        }

        double? x = OptionReader.NumberOf(given[0]);
        double? y = OptionReader.NumberOf(given[1]);
        double? z = OptionReader.NumberOf(given[2]);
        return x != null && y != null && z != null ? new Vec3(x.Value, y.Value, z.Value) : null;
    }
}
