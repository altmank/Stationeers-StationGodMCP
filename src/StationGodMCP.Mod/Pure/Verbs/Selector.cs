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
internal sealed record Selector(
    PerIdList? Ids,
    NearPoint? Near,
    double? Radius,
    string? Within,
    BoxCorners? Box,
    string? Room,
    NetworkTarget? Network,
    PrefabFilter? Prefab,
    string? Name)
{
    internal static Selector None { get; } = new Selector(null, null, null, null, null, null, null, null, null);

    internal static Selector Bind(OptionReader options) => new Selector(
        PerIdList.Bind(options),
        NearPoint.Bind(options),
        options.Number("radius"),
        Trimmed(options.Text("within")),
        BoxCorners.Bind(options),
        Trimmed(options.Text("room")),
        NetworkTarget.Bind(options),
        PrefabFilter.Bind(options),
        Trimmed(options.Text("name")));

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

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
}

/// <summary>near: a point, the player, the thing the crosshair is on, or a thing by id (its position).</summary>
internal abstract record NearPoint
{
    private NearPoint()
    {
    }

    internal sealed record Player : NearPoint;

    internal sealed record LookingAt : NearPoint;

    internal sealed record At(Vec3 Point) : NearPoint;

    internal sealed record Thing(string Id) : NearPoint;

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
internal sealed record BoxCorners(Vec3 First, Vec3 Second)
{
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
internal sealed record NetworkTarget(string Id, string? Port)
{
    internal static NetworkTarget? Bind(OptionReader options)
    {
        switch (options.Raw("network"))
        {
            case null:
                return null;
            case JValue { Type: JTokenType.String } text when ((string)text!).Trim().Length > 0:
                return new NetworkTarget(((string)text!).Trim(), null);
            case JObject pair when pair["id"] is JValue { Type: JTokenType.String } id && ((string)id!).Trim().Length > 0:
                JToken? port = pair["port"];
                return new NetworkTarget(((string)id!).Trim(),
                    port == null || port.Type == JTokenType.Null ? null : port.ToString().Trim());
        }

        options.Problem("network", "must be a network's id, a piece or device on it, or {id, port}");
        return null;
    }
}

/// <summary>
/// prefab: whole names, hashes, or parts with * ('*Battery*', '*Tank*Insulated*': each part, in any order), one or a
/// list. A thing passes when it matches any of them; names match in any case.
/// </summary>
internal sealed record PrefabFilter(IReadOnlyList<PrefabPattern> Patterns)
{
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
internal abstract record PrefabPattern
{
    private PrefabPattern()
    {
    }

    internal abstract bool Keeps(string? prefabName, int prefabHash);

    internal sealed record Hash(int Value) : PrefabPattern
    {
        internal override bool Keeps(string? prefabName, int prefabHash) => prefabHash == Value;
    }

    internal sealed record Whole(string Name) : PrefabPattern
    {
        internal override bool Keeps(string? prefabName, int prefabHash) =>
            string.Equals(prefabName, Name, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record Parts(IReadOnlyList<string> Pieces) : PrefabPattern
    {
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
