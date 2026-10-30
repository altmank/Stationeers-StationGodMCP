#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Json;

/// <summary>
/// Lattice's contract read from an agent's JSON (catalogue topic lattice): commands, selectors, drafts, styles and the
/// values under them. A union is its case's name ("everything") or an object with the one key naming the case and its
/// payload ({"ids": [1, 2]}); cells and offsets are metres, turns are rotation degrees [x, y, z] (or Lattice's turn
/// index), pieces and kits are prefab names (or hashes), structures are reference ids, keys are "owner/name" (Lattice's
/// own bare). Every problem is kept with its path; a read with problems gives null.
/// </summary>
internal sealed partial class LatticeIn
{
    private readonly L.IGhostSystem _system;
    private Dictionary<string, L.PrefabKey>? _pieces;
    private Dictionary<string, L.KitKey>? _kits;

    internal LatticeIn(L.IGhostSystem system)
    {
        _system = system;
    }

    internal List<string> Problems { get; } = new List<string>();

    /// <summary>The running contract's minor: what a value may use (SeeThroughLook.Through.Opacity from 0.11).</summary>
    internal int Minor => _system.Version.Minor;

    internal bool Failed => Problems.Count > 0;

    internal void Problem(string path, string text) => Problems.Add($"{path}: {text}");

    // ---- unions and plain values -------------------------------------------------------------------------------

    /// <summary>A union's case and payload: "name", or {"name": payload} with one key.</summary>
    internal (string Kind, JToken? Payload)? Case(JToken? token, string path, params string[] cases)
    {
        string? kind = null;
        JToken? payload = null;
        if (token is JValue { Type: JTokenType.String } word)
        {
            kind = (string)word!;
        }
        else if (token is JObject { Count: 1 } one)
        {
            foreach (KeyValuePair<string, JToken?> entry in one)
            {
                kind = entry.Key;
                payload = entry.Value;
            }
        }

        if (kind == null || Array.IndexOf(cases, kind) < 0)
        {
            Problem(path, $"is one of {string.Join(", ", cases)} (a name, or an object with that one key)");
            return null;
        }

        return (kind, payload);
    }

    internal static bool Given(JObject? owner, string key) =>
        owner != null && owner[key] is { } value && value.Type != JTokenType.Null;

    internal string? Text(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.String } text)
        {
            return (string)text!;
        }

        Problem(path, "is text");
        return null;
    }

    internal double? Number(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer or JTokenType.Float } number)
        {
            return (double)number;
        }

        Problem(path, "is a number");
        return null;
    }

    internal int? Whole(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer } number)
        {
            long value = (long)number;
            if (value >= int.MinValue && value <= int.MaxValue)
            {
                return (int)value;
            }
        }

        Problem(path, "is a whole number");
        return null;
    }

    internal long? Id(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer } number)
        {
            return (long)number;
        }

        if (token is JValue { Type: JTokenType.String } text &&
            long.TryParse((string)text!, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        Problem(path, "is an id");
        return null;
    }

    internal bool? Flag(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Boolean } flag)
        {
            return (bool)flag;
        }

        Problem(path, "is true or false");
        return null;
    }

    internal JObject? Object(JToken? token, string path)
    {
        if (token is JObject value)
        {
            return value;
        }

        Problem(path, "is an object");
        return null;
    }

    internal JArray? List(JToken? token, string path)
    {
        if (token is JArray value)
        {
            return value;
        }

        Problem(path, "is a list");
        return null;
    }

    /// <summary>A Lattice value made by its own check; null with the reason as a problem.</summary>
    internal T? Made<T>(L.Result<T, L.Reason> made, string path) where T : struct
    {
        if (made.TryGet(out T value))
        {
            return value;
        }

        made.TryGetError(out L.Reason why);
        Problem(path, $"Lattice refused it ({LatticeText.Of(why)})");
        return null;
    }

    internal T? MadeRef<T>(L.Result<T, L.Reason> made, string path) where T : class
    {
        if (made.TryGet(out T value))
        {
            return value;
        }

        made.TryGetError(out L.Reason why);
        Problem(path, $"Lattice refused it ({LatticeText.Of(why)})");
        return null;
    }

    internal L.ValueList<T> Each<T>(JToken? token, string path, Func<JToken, string, T?> read) where T : class
    {
        List<T> read1 = new List<T>();
        if (List(token, path) is { } list)
        {
            for (int index = 0; index < list.Count; index++)
            {
                if (read(list[index], $"{path}[{index}]") is { } value)
                {
                    read1.Add(value);
                }
            }
        }

        return L.ValueList.From(read1);
    }

    internal L.ValueList<T> EachValue<T>(JToken? token, string path, Func<JToken, string, T?> read) where T : struct
    {
        List<T> read1 = new List<T>();
        if (List(token, path) is { } list)
        {
            for (int index = 0; index < list.Count; index++)
            {
                if (read(list[index], $"{path}[{index}]") is { } value)
                {
                    read1.Add(value);
                }
            }
        }

        return L.ValueList.From(read1);
    }

    // ---- space -------------------------------------------------------------------------------------------------

    /// <summary>A cell from metres [x, y, z]: the game's Grid3 key (decimetres, a half away from zero).</summary>
    internal L.Cell? Cell(JToken? token, string path) =>
        Metres3(token, path) is { } point ? LatticeGhosts.CellOf(point) : (L.Cell?)null;

    internal L.CellOffset? Offset(JToken? token, string path) =>
        Metres3(token, path) is { } by
            ? new L.CellOffset(Decimetres(by.X), Decimetres(by.Y), Decimetres(by.Z))
            : (L.CellOffset?)null;

    internal Vec3? Metres3(JToken? token, string path)
    {
        if (token is JArray { Count: 3 } list && Number(list[0], path + "[0]") is { } x &&
            Number(list[1], path + "[1]") is { } y && Number(list[2], path + "[2]") is { } z)
        {
            return new Vec3(x, y, z);
        }

        if (!(token is JArray { Count: 3 }))
        {
            Problem(path, "is [x, y, z] in metres");
        }

        return null;
    }

    internal static int Decimetres(double metres) =>
        (int)Math.Round(metres * 10.0, MidpointRounding.AwayFromZero);

    /// <summary>A turn: rotation degrees [x, y, z] in quarter turns, or Lattice's turn index 0..23.</summary>
    internal L.Turn? Turn(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer })
        {
            return Whole(token, path) is { } index ? Made(L.Turn.Create(index), path) : null;
        }

        if (Metres3(token, path) is not { } degrees)
        {
            return null;
        }

        if (LatticeGhosts.TurnOf(degrees) is { } turn)
        {
            return turn;
        }

        Problem(path, "is quarter turns (each angle a multiple of 90 degrees)");
        return null;
    }

    internal L.Axis? Axis(JToken? token, string path) =>
        Case(token, path, "x", "y", "z") is { } axis
            ? axis.Kind switch { "x" => L.Axis.X, "y" => L.Axis.Y, _ => L.Axis.Z }
            : (L.Axis?)null;

    internal L.WorldPoint? Point(JToken? token, string path) =>
        Metres3(token, path) is { } point ? new L.WorldPoint((float)point.X, (float)point.Y, (float)point.Z) : (L.WorldPoint?)null;

    internal L.WorldVector? Vector(JToken? token, string path) =>
        Metres3(token, path) is { } point ? new L.WorldVector((float)point.X, (float)point.Y, (float)point.Z) : (L.WorldVector?)null;

    // ---- pieces, kits, structures --------------------------------------------------------------------------------

    internal L.PrefabKey? Piece(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer })
        {
            return Whole(token, path) is { } hash ? new L.PrefabKey(hash) : (L.PrefabKey?)null;
        }

        if (Text(token, path) is not { } name)
        {
            return null;
        }

        if (_pieces == null)
        {
            _pieces = new Dictionary<string, L.PrefabKey>(StringComparer.OrdinalIgnoreCase);
            foreach (L.PieceEntry piece in _system.Catalogue.Pieces)
            {
                _pieces[piece.PrefabName] = piece.Key;
            }
        }

        if (_pieces.TryGetValue(name, out L.PrefabKey key))
        {
            return key;
        }

        Problem(path, $"'{name}' is no structure prefab the game holds");
        return null;
    }

    internal L.KitKey? Kit(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer })
        {
            return Whole(token, path) is { } hash ? new L.KitKey(hash) : (L.KitKey?)null;
        }

        if (Text(token, path) is not { } name)
        {
            return null;
        }

        if (_kits == null)
        {
            _kits = new Dictionary<string, L.KitKey>(StringComparer.OrdinalIgnoreCase);
            foreach (L.KitEntry kit in _system.Catalogue.Kits)
            {
                _kits[kit.PrefabName] = kit.Key;
            }
        }

        if (_kits.TryGetValue(name, out L.KitKey key))
        {
            return key;
        }

        Problem(path, $"'{name}' is no kit the game holds");
        return null;
    }

    internal L.Placement? Placement(JToken? token, string path)
    {
        if (Object(token, path) is not { } placement)
        {
            return null;
        }

        L.PrefabKey? piece = Piece(placement["piece"], path + ".piece");
        L.Cell? at = Cell(placement["at"], path + ".at");
        L.Turn? turn = Given(placement, "turn") ? Turn(placement["turn"], path + ".turn") : L.Turn.None;
        return piece is { } p && at is { } a && turn is { } t ? new L.Placement(p, a, t) : null;
    }

    /// <summary>A built structure by reference id, as Lattice's copy of the world holds it now.</summary>
    internal L.StructureRef? Structure(JToken? token, string path)
    {
        if (Id(token, path) is not { } id)
        {
            return null;
        }

        if (_system.World.Structure(new L.ReferenceId(id)).TryGet(out L.StructureInfo info))
        {
            return info.Ref;
        }

        Problem(path, $"no built structure {id} in Lattice's world");
        return null;
    }

    // ---- names -------------------------------------------------------------------------------------------------

    internal L.GhostId? Ghost(JToken? token, string path) => Id(token, path) is { } id ? new L.GhostId(id) : (L.GhostId?)null;

    internal L.PlanId? Plan(JToken? token, string path) => Id(token, path) is { } id ? new L.PlanId(id) : (L.PlanId?)null;

    internal L.Option<L.PlanId>? Into(JObject owner, string key, string path)
    {
        if (!Given(owner, key))
        {
            return L.Option<L.PlanId>.None;
        }

        return Plan(owner[key], path + "." + key) is { } plan ? L.Option.Some(plan) : null;
    }

    internal L.Option<L.Revision>? Since(JObject owner, string path)
    {
        if (!Given(owner, "unchanged_since"))
        {
            return L.Option<L.Revision>.None;
        }

        return Id(owner["unchanged_since"], path + ".unchanged_since") is { } at
            ? L.Option.Some(new L.Revision(at))
            : null;
    }

    /// <summary>"owner/name"; a bare name is owned by StationGod.</summary>
    internal (L.ModId Owner, string Name)? Qualified(JToken? token, string path)
    {
        if (Text(token, path) is not { } text)
        {
            return null;
        }

        int slash = text.IndexOf('/');
        if (slash < 0)
        {
            return (LatticeNames.Mod, text);
        }

        return Made(L.ModId.Create(text.Substring(0, slash)), path) is { } owner ? (owner, text.Substring(slash + 1)) : null;
    }

    internal L.Maker? Maker(JToken? token, string path) =>
        Qualified(token, path) is { } name ? Made(L.Maker.Create(name.Owner, name.Name), path) : null;

    internal L.Maker? MadeBy(JObject owner, string path) =>
        Given(owner, "made_by") ? Maker(owner["made_by"], path + ".made_by") : LatticeNames.AgentMaker;

    internal L.AttachmentKey? AttachmentKey(JToken? token, string path) =>
        Qualified(token, path) is { } name ? Made(L.AttachmentKey.Create(name.Owner, name.Name), path) : null;

    /// <summary>Attached data: text as given; any other JSON value is kept as its JSON text.</summary>
    internal L.Attachment? Attachment(JToken? token, string path)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            Problem(path, "is the data to attach: text, or any JSON value");
            return null;
        }

        return Made(L.Attachment.Create(token.Type == JTokenType.String
            ? (string)token!
            : token.ToString(Newtonsoft.Json.Formatting.None)), path);
    }

    internal L.ValueMap<L.AttachmentKey, L.Attachment>? Attached(JToken? token, string path)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return L.ValueMap<L.AttachmentKey, L.Attachment>.Empty;
        }

        if (Object(token, path) is not { } given)
        {
            return null;
        }

        L.ValueMap<L.AttachmentKey, L.Attachment> attached = L.ValueMap<L.AttachmentKey, L.Attachment>.Empty;
        foreach (KeyValuePair<string, JToken?> entry in given)
        {
            if (AttachmentKey(new JValue(entry.Key), $"{path}.{entry.Key}") is { } key &&
                Attachment(entry.Value, $"{path}.{entry.Key}") is { } value)
            {
                attached = attached.With(key, value);
            }
        }

        return attached;
    }

    internal L.Actor? Actor(JToken? token, string path)
    {
        if (Case(token, path, "agent", "player", "mod", "me") is not { } actor)
        {
            return null;
        }

        switch (actor.Kind)
        {
            case "me":
                return LatticeNames.Agent;
            case "agent":
                if (actor.Payload == null)
                {
                    return LatticeNames.Agent;
                }

                return Text(actor.Payload, path + ".agent") is { } agent && Made(L.AgentId.Create(agent), path) is { } id
                    ? new L.Actor.Agent(id)
                    : null;
            case "player":
                return Id(actor.Payload, path + ".player") is { } client ? new L.Actor.Player(new L.ClientId((ulong)client)) : null;
            default:
                return Text(actor.Payload, path + ".mod") is { } mod && Made(L.ModId.Create(mod), path) is { } modId
                    ? new L.Actor.Mod(modId)
                    : null;
        }
    }

    internal L.Visibility? Visibility(JToken? token, string path)
    {
        if (Case(token, path, "everyone", "owner_only", "team") is not { } visibility)
        {
            return null;
        }

        return visibility.Kind switch
        {
            "everyone" => new L.Visibility.Everyone(),
            "owner_only" => new L.Visibility.OwnerOnly(),
            _ => Text(visibility.Payload, path + ".team") is { } team && Made(L.TeamName.Create(team), path) is { } name
                ? new L.Visibility.Team(name)
                : null
        };
    }

    internal L.Option<L.Label>? Label(JToken? token, string path)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return L.Option<L.Label>.None;
        }

        return Text(token, path) is { } text && Made(L.Label.Create(text), path) is { } label ? L.Option.Some(label) : null;
    }

    /// <summary>A tint: [r, g, b] or [r, g, b, a] from 0 to 1, or "#rrggbb" / "#rrggbbaa"; null clears it.</summary>
    internal L.Option<L.Tint>? Tint(JToken? token, string path)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return L.Option<L.Tint>.None;
        }

        return Rgba(token, path) is { } colour
            ? L.Option.Some(new L.Tint(colour.R, colour.G, colour.B, colour.A))
            : null;
    }

    internal L.Rgba? Rgba(JToken? token, string path)
    {
        if (token is JArray { Count: 3 or 4 } channels)
        {
            byte[] bytes = { 255, 255, 255, 255 };
            for (int index = 0; index < channels.Count; index++)
            {
                if (Number(channels[index], $"{path}[{index}]") is not { } channel)
                {
                    return null;
                }

                bytes[index] = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, channel)) * 255.0);
            }

            return new L.Rgba(bytes[0], bytes[1], bytes[2], bytes[3]);
        }

        if (token is JValue { Type: JTokenType.String } hex && ((string)hex!).StartsWith("#", StringComparison.Ordinal))
        {
            string digits = ((string)hex!).Substring(1);
            if ((digits.Length == 6 || digits.Length == 8) &&
                uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            {
                if (digits.Length == 6)
                {
                    value = (value << 8) | 0xFF;
                }

                return new L.Rgba((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            }
        }

        Problem(path, "is [r, g, b(, a)] from 0 to 1, or \"#rrggbb(aa)\"");
        return null;
    }

    internal L.GroupName? Group(JToken? token, string path) =>
        Text(token, path) is { } name ? Made(L.GroupName.Create(name), path) : null;

    internal L.ValueSet<L.GroupName>? Groups(JToken? token, string path)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return L.ValueSet<L.GroupName>.Empty;
        }

        L.ValueList<L.GroupName> groups = EachValue(token, path, Group);
        return L.ValueSet.From(groups);
    }

    /// <summary>A finish: {colour (the game's paint index), name, setting}, at least one.</summary>
    internal L.Finish? Finish(JToken? token, string path)
    {
        if (Object(token, path) is not { } finish)
        {
            return null;
        }

        L.Option<L.PaintColour> colour = L.Option<L.PaintColour>.None;
        L.Option<L.ThingName> name = L.Option<L.ThingName>.None;
        L.Option<L.Setting> setting = L.Option<L.Setting>.None;
        if (Given(finish, "colour") && Whole(finish["colour"], path + ".colour") is { } index &&
            Made(L.PaintColour.Create(index), path + ".colour") is { } paint)
        {
            colour = L.Option.Some(paint);
        }

        if (Given(finish, "name") && Text(finish["name"], path + ".name") is { } text &&
            Made(L.ThingName.Create(text), path + ".name") is { } thing)
        {
            name = L.Option.Some(thing);
        }

        if (Given(finish, "setting") && Number(finish["setting"], path + ".setting") is { } number &&
            Made(L.Setting.Create(number), path + ".setting") is { } value)
        {
            setting = L.Option.Some(value);
        }

        return MadeRef(L.Finish.Create(colour, name, setting), path);
    }

    internal L.Option<L.Finish>? FinishOf(JObject owner, string path)
    {
        if (!Given(owner, "finish"))
        {
            return L.Option<L.Finish>.None;
        }

        return Finish(owner["finish"], path + ".finish") is { } finish ? L.Option.Some(finish) : null;
    }

    // ---- selectors ---------------------------------------------------------------------------------------------

    internal L.Selector? Selector(JToken? token, string path)
    {
        if (token is JArray)
        {
            // A bare list is ids, the common case.
            return new L.Selector.Ids(EachValue(token, path, Ghost));
        }

        if (Case(token, path, "everything", "ids", "box", "run_through", "network", "room", "group", "owner",
                "made_by", "plan", "tagged", "piece", "all", "any") is not { } selector)
        {
            return null;
        }

        JToken? payload = selector.Payload;
        string at = path + "." + selector.Kind;
        switch (selector.Kind)
        {
            case "everything":
                return new L.Selector.Everything();
            case "ids":
                return new L.Selector.Ids(EachValue(payload, at, Ghost));
            case "box":
                return Corners(payload, at) is { } box ? new L.Selector.Box(box.A, box.B) : null;
            case "run_through":
                return Cell(payload, at) is { } cell ? new L.Selector.RunThrough(cell) : null;
            case "network":
                return Id(payload, at) is { } network ? new L.Selector.Network(new L.NetworkRef(network)) : null;
            case "room":
                return Id(payload, at) is { } room ? new L.Selector.Room(new L.RoomRef(room)) : null;
            case "group":
                return Group(payload, at) is { } group ? new L.Selector.Group(group) : null;
            case "owner":
                return Actor(payload, at) is { } owner ? new L.Selector.Owner(owner) : null;
            case "made_by":
                return Maker(payload, at) is { } maker ? new L.Selector.MadeBy(maker) : null;
            case "plan":
                return Plan(payload, at) is { } plan ? new L.Selector.InPlan(plan) : null;
            case "tagged":
                if (Object(payload, at) is not { } tagged || AttachmentKey(tagged["key"], at + ".key") is not { } key)
                {
                    return null;
                }

                if (!Given(tagged, "value"))
                {
                    return new L.Selector.Tagged(key, L.Option<L.Attachment>.None);
                }

                return Attachment(tagged["value"], at + ".value") is { } value
                    ? new L.Selector.Tagged(key, L.Option.Some(value))
                    : null;
            case "piece":
                return Piece(payload, at) is { } piece ? new L.Selector.OfPiece(piece) : null;
            case "all":
                return new L.Selector.All(Each(payload, at, Selector));
            default:
                return new L.Selector.Any(Each(payload, at, Selector));
        }
    }

    internal L.StructureSelector? Structures(JToken? token, string path)
    {
        if (token is JArray)
        {
            return new L.StructureSelector.Refs(EachValue(token, path, Structure));
        }

        if (Case(token, path, "refs", "box", "run_through", "network", "room", "piece", "all", "any") is not { } selector)
        {
            return null;
        }

        JToken? payload = selector.Payload;
        string at = path + "." + selector.Kind;
        switch (selector.Kind)
        {
            case "refs":
                return new L.StructureSelector.Refs(EachValue(payload, at, Structure));
            case "box":
                return Corners(payload, at) is { } box ? new L.StructureSelector.Box(box.A, box.B) : null;
            case "run_through":
                return Cell(payload, at) is { } cell ? new L.StructureSelector.RunThrough(cell) : null;
            case "network":
                return Id(payload, at) is { } network ? new L.StructureSelector.Network(new L.NetworkRef(network)) : null;
            case "room":
                return Id(payload, at) is { } room ? new L.StructureSelector.Room(new L.RoomRef(room)) : null;
            case "piece":
                return Piece(payload, at) is { } piece ? new L.StructureSelector.OfPiece(piece) : null;
            case "all":
                return new L.StructureSelector.All(Each(payload, at, Structures));
            default:
                return new L.StructureSelector.Any(Each(payload, at, Structures));
        }
    }

    private (L.Cell A, L.Cell B)? Corners(JToken? token, string path)
    {
        if (token is JArray { Count: 2 } corners && Cell(corners[0], path + "[0]") is { } a &&
            Cell(corners[1], path + "[1]") is { } b)
        {
            return (a, b);
        }

        if (!(token is JArray { Count: 2 }))
        {
            Problem(path, "is two corners [[x, y, z], [x, y, z]] in metres");
        }

        return null;
    }

    // ---- drafts and intents ------------------------------------------------------------------------------------

    /// <summary>
    /// A ghost to place: one intent key (build, remove, swap, finish_only) and plan, made_by, visibility, label, tint,
    /// groups, attached and look (StationGod's look, as show ghost's options) beside it.
    /// </summary>
    internal L.GhostDraft? Draft(JToken? token, string path, LatticeLookIn? looks = null)
    {
        if (Object(token, path) is not { } draft)
        {
            return null;
        }

        L.Intent? intent = null;
        int intents = 0;
        foreach (string kind in new[] { "build", "remove", "swap", "finish_only" })
        {
            if (Given(draft, kind))
            {
                intents++;
                intent = Intent(kind, draft[kind]!, $"{path}.{kind}");
            }
        }

        if (intents != 1)
        {
            Problem(path, "has one of build, remove, swap or finish_only");
            return null;
        }

        L.Option<L.PlanId>? plan = Into(draft, "plan", path);
        L.Maker? maker = MadeBy(draft, path);
        L.Visibility? visibility = Given(draft, "visibility")
            ? Visibility(draft["visibility"], path + ".visibility")
            : new L.Visibility.Everyone();
        L.Option<L.Label>? label = Label(draft["label"], path + ".label");
        L.Option<L.Tint>? tint = Tint(draft["tint"], path + ".tint");
        L.ValueSet<L.GroupName>? groups = Groups(draft["groups"], path + ".groups");
        L.ValueMap<L.AttachmentKey, L.Attachment>? attached = Attached(draft["attached"], path + ".attached");
        if (attached != null && looks != null && Given(draft, "look"))
        {
            attached = looks.Marks(draft["look"], path + ".look", attached);
        }

        return intent != null && plan is { } p && maker is { } m && visibility != null && label is { } l &&
               tint is { } t && groups != null && attached != null
            ? new L.GhostDraft(intent, p, m, visibility, l, t, groups, attached)
            : null;
    }

    internal L.Intent? Intent(string kind, JToken payload, string path)
    {
        switch (kind)
        {
            case "build":
            {
                if (Object(payload, path) is not { } build)
                {
                    return null;
                }

                L.Placement? target = Placement(build, path);
                L.Option<L.Finish>? finish = FinishOf(build, path);
                return target != null && finish is { } f ? new L.Intent.Build(target, f) : null;
            }
            case "remove":
                return Structure(payload, path) is { } removed ? new L.Intent.Remove(removed) : null;
            case "swap":
            {
                if (Object(payload, path) is not { } swap)
                {
                    return null;
                }

                L.StructureRef? old = Structure(swap["old"], path + ".old");
                L.Placement? made = Placement(swap["new"], path + ".new");
                L.Option<L.Finish>? finish = FinishOf(swap, path);
                return old is { } o && made != null && finish is { } f ? new L.Intent.Swap(o, made, f) : null;
            }
            default:
            {
                if (Object(payload, path) is not { } finishOnly)
                {
                    return null;
                }

                L.StructureRef? target = Structure(finishOnly["target"], path + ".target");
                L.Finish? finish = Finish(finishOnly["finish"], path + ".finish");
                return target is { } t && finish != null ? new L.Intent.FinishOnly(t, finish) : null;
            }
        }
    }

    // ---- commands ----------------------------------------------------------------------------------------------

    internal static readonly string[] CommandKinds =
    {
        "place", "remove", "change", "move", "create_plan", "edit_plan", "move_to_plan", "revert", "batch",
        "finish_plan", "catch_up", "mark_removal", "plan_swap", "plan_finish", "plan_run", "plan_route", "transform",
        "place_template", "place_template_in", "repair", "take_down", "reopen", "plan_split"
    };

    /// <summary>A command; null when any part of it had a problem (each in Problems), never a command with parts dropped.</summary>
    internal L.Command? Command(JToken? token, string path, LatticeLookIn? looks = null)
    {
        int before = Problems.Count;
        L.Command? command = CommandOf(token, path, looks);
        return Problems.Count > before ? null : command;
    }

    private L.Command? CommandOf(JToken? token, string path, LatticeLookIn? looks)
    {
        if (Case(token, path, CommandKinds) is not { } command)
        {
            return null;
        }

        JToken? payload = command.Payload;
        string at = path + "." + command.Kind;
        JObject? fields = payload as JObject;
        switch (command.Kind)
        {
            case "place":
                return new L.Command.Place(Each(payload, at, (draft, where) => Draft(draft, where, looks)));
            case "remove":
                return Selector(payload, at) is { } removed ? new L.Command.Remove(removed) : null;
            case "change":
            {
                if (Object(payload, at) is not { } change)
                {
                    return null;
                }

                L.Selector? which = Selector(change["which"], at + ".which");
                L.GhostEdit? edit = Edit(change["edit"], at + ".edit");
                L.Option<L.Revision>? since = Since(change, at);
                return which != null && edit != null && since is { } s ? new L.Command.Change(which, edit, s) : null;
            }
            case "move":
            {
                if (Object(payload, at) is not { } move || Object(move["to"], at + ".to") is not { } to)
                {
                    return null;
                }

                Dictionary<L.GhostId, L.Placement> placements = new Dictionary<L.GhostId, L.Placement>();
                foreach (KeyValuePair<string, JToken?> entry in to)
                {
                    if (Ghost(new JValue(entry.Key), $"{at}.to.{entry.Key}") is { } ghost &&
                        Placement(entry.Value, $"{at}.to.{entry.Key}") is { } placement)
                    {
                        placements[ghost] = placement;
                    }
                }

                return Since(move, at) is { } since
                    ? new L.Command.Move(L.ValueMap.From<L.GhostId, L.Placement>(placements), since)
                    : null;
            }
            case "create_plan":
            {
                if (PlanDraft(payload, at) is not { } draft)
                {
                    return null;
                }

                L.ValueList<L.GhostDraft> ghosts = fields != null && Given(fields, "ghosts")
                    ? Each(fields!["ghosts"], at + ".ghosts", (ghost, where) => Draft(ghost, where, looks))
                    : L.ValueList<L.GhostDraft>.Empty;
                return new L.Command.CreatePlan(draft) { Ghosts = ghosts };
            }
            case "edit_plan":
            {
                if (Object(payload, at) is not { } edit)
                {
                    return null;
                }

                L.PlanId? plan = Plan(edit["plan"], at + ".plan");
                L.PlanEdit? change = PlanEdit(edit["edit"], at + ".edit");
                L.Option<L.Revision>? since = Since(edit, at);
                return plan is { } p && change != null && since is { } s ? new L.Command.EditPlan(p, change, s) : null;
            }
            case "move_to_plan":
            {
                if (Object(payload, at) is not { } move)
                {
                    return null;
                }

                L.Selector? which = Selector(move["which"], at + ".which");
                L.PlanId? to = Plan(move["to"], at + ".to");
                return which != null && to is { } t ? new L.Command.MoveToPlan(which, t) : null;
            }
            case "revert":
                return Id(payload, at) is { } changeSet ? new L.Command.Revert(new L.ChangeSetId(changeSet)) : null;
            case "batch":
                return new L.Command.Batch(Each(payload, at, (step, where) => Command(step, where, looks)));
            case "finish_plan":
                return Plan(payload, at) is { } finished ? new L.Command.FinishPlan(finished) : null;
            case "catch_up":
                return Plan(payload, at) is { } behind ? new L.Command.CatchUp(behind) : null;
            case "mark_removal":
            {
                if (Object(payload, at) is not { } mark)
                {
                    return null;
                }

                L.StructureSelector? which = Structures(mark["which"], at + ".which");
                L.Option<L.PlanId>? into = Into(mark, "into", at);
                L.Maker? maker = MadeBy(mark, at);
                return which != null && into is { } i && maker is { } m ? new L.Command.MarkRemoval(which, i, m) : null;
            }
            case "plan_swap":
            {
                if (Object(payload, at) is not { } swap)
                {
                    return null;
                }

                L.StructureSelector? which = Structures(swap["which"], at + ".which");
                L.SwapTo? to = SwapTo(swap["to"], at + ".to");
                L.Option<L.PlanId>? into = Into(swap, "into", at);
                L.Maker? maker = MadeBy(swap, at);
                return which != null && to != null && into is { } i && maker is { } m
                    ? new L.Command.PlanSwap(which, to, i, m)
                    : null;
            }
            case "plan_finish":
            {
                if (Object(payload, at) is not { } finish)
                {
                    return null;
                }

                L.StructureSelector? which = Structures(finish["which"], at + ".which");
                L.Finish? look = Finish(finish["finish"], at + ".finish");
                L.Option<L.PlanId>? into = Into(finish, "into", at);
                L.Maker? maker = MadeBy(finish, at);
                return which != null && look != null && into is { } i && maker is { } m
                    ? new L.Command.PlanFinish(which, look, i, m)
                    : null;
            }
            case "plan_run":
                return Run(payload, at) is { } run ? new L.Command.PlanRun(run) : null;
            case "plan_route":
                return Route(payload, at) is { } route ? new L.Command.PlanRoute(route) : null;
            case "transform":
            {
                if (Object(payload, at) is not { } transform)
                {
                    return null;
                }

                L.Selector? which = Selector(transform["which"], at + ".which");
                L.Transformation? how = Transformation(transform["how"], at + ".how");
                L.TransformMode? mode = new L.TransformMode.Move();
                if (Given(transform, "copy"))
                {
                    mode = transform["copy"] is JValue { Type: JTokenType.Boolean } copy
                        ? (bool)copy ? new L.TransformMode.Copy(LatticeNames.AgentMaker) : mode
                        : Maker(transform["copy"], at + ".copy") is { } maker ? new L.TransformMode.Copy(maker) : null;
                }

                return which != null && how != null && mode != null ? new L.Command.Transform(which, how, mode) : null;
            }
            case "place_template":
            case "place_template_in":
            {
                if (Object(payload, at) is not { } place)
                {
                    return null;
                }

                L.Template? template = Template(place["template"], at + ".template", looks);
                L.Cell? origin = Cell(place["origin"], at + ".origin");
                L.Turn? turn = Given(place, "turn") ? Turn(place["turn"], at + ".turn") : L.Turn.None;
                if (template == null || origin is not { } o || turn is not { } t)
                {
                    return null;
                }

                if (command.Kind == "place_template_in")
                {
                    return Into(place, "into", at) is { } into ? new L.Command.PlaceTemplateIn(template, o, t, into) : null;
                }

                return PlanDraft(place["as"], at + ".as") is { } plan ? new L.Command.PlaceTemplate(template, o, t, plan) : null;
            }
            case "repair":
            case "take_down":
            {
                L.PlanId? plan;
                L.Maker? maker = LatticeNames.AgentMaker;
                if (payload is JObject finishedPlan)
                {
                    plan = Plan(finishedPlan["plan"], at + ".plan");
                    maker = MadeBy(finishedPlan, at);
                }
                else
                {
                    plan = Plan(payload, at);
                }

                if (plan is not { } p || maker is not { } m)
                {
                    return null;
                }

                return command.Kind == "repair" ? new L.Command.Repair(p, m) : new L.Command.TakeDown(p, m);
            }
            case "reopen":
                return Plan(payload, at) is { } reopened ? new L.Command.Reopen(reopened) : null;
            default:
            {
                if (Object(payload, at) is not { } split)
                {
                    return null;
                }

                L.SplitTargets? which = null;
                if (Given(split, "ghosts") == Given(split, "structures"))
                {
                    Problem(at, "names ghosts or structures");
                }
                else if (Given(split, "ghosts"))
                {
                    which = Selector(split["ghosts"], at + ".ghosts") is { } ghosts ? new L.SplitTargets.Ghosts(ghosts) : null;
                }
                else
                {
                    which = Structures(split["structures"], at + ".structures") is { } built
                        ? new L.SplitTargets.Structures(built)
                        : null;
                }

                L.Option<L.PlanId>? into = Into(split, "into", at);
                L.Maker? maker = MadeBy(split, at);
                return which != null && into is { } i && maker is { } m ? new L.Command.PlanSplit(which, i, m) : null;
            }
        }
    }

    internal L.GhostEdit? Edit(JToken? token, string path)
    {
        if (Case(token, path, "label", "tint", "join_group", "leave_group", "finish", "visibility", "attach", "detach")
            is not { } edit)
        {
            return null;
        }

        string at = path + "." + edit.Kind;
        switch (edit.Kind)
        {
            case "label":
                return Label(edit.Payload, at) is { } label ? new L.GhostEdit.SetLabel(label) : null;
            case "tint":
                return Tint(edit.Payload, at) is { } tint ? new L.GhostEdit.SetTint(tint) : null;
            case "join_group":
                return Group(edit.Payload, at) is { } join ? new L.GhostEdit.JoinGroup(join) : null;
            case "leave_group":
                return Group(edit.Payload, at) is { } leave ? new L.GhostEdit.LeaveGroup(leave) : null;
            case "finish":
                if (edit.Payload == null || edit.Payload.Type == JTokenType.Null)
                {
                    return new L.GhostEdit.SetFinish(L.Option<L.Finish>.None);
                }

                return Finish(edit.Payload, at) is { } finish ? new L.GhostEdit.SetFinish(L.Option.Some(finish)) : null;
            case "visibility":
                return Visibility(edit.Payload, at) is { } visibility ? new L.GhostEdit.SetVisibility(visibility) : null;
            case "attach":
            {
                if (Object(edit.Payload, at) is not { } attach)
                {
                    return null;
                }

                L.AttachmentKey? key = AttachmentKey(attach["key"], at + ".key");
                L.Attachment? value = Attachment(attach["value"], at + ".value");
                return key is { } k && value is { } v ? new L.GhostEdit.Attach(k, v) : null;
            }
            default:
                return AttachmentKey(edit.Payload, at) is { } detached ? new L.GhostEdit.Detach(detached) : null;
        }
    }

    internal L.PlanDraft? PlanDraft(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.String })
        {
            return Text(token, path) is { } bare && Made(L.PlanName.Create(bare), path) is { } named
                ? new L.PlanDraft(named, LatticeNames.AgentMaker, new L.Visibility.Everyone(),
                    L.ValueMap<L.AttachmentKey, L.Attachment>.Empty)
                : null;
        }

        if (Object(token, path) is not { } draft)
        {
            return null;
        }

        L.PlanName? name = Text(draft["name"], path + ".name") is { } text ? Made(L.PlanName.Create(text), path + ".name") : null;
        L.Maker? maker = MadeBy(draft, path);
        L.Visibility? visibility = Given(draft, "visibility")
            ? Visibility(draft["visibility"], path + ".visibility")
            : new L.Visibility.Everyone();
        L.ValueMap<L.AttachmentKey, L.Attachment>? data = Attached(draft["data"], path + ".data");
        if (name is not { } n || maker is not { } m || visibility == null || data == null)
        {
            return null;
        }

        L.PlanDraft made = new L.PlanDraft(n, m, visibility, data);
        if (!Given(draft, "colour"))
        {
            return made;
        }

        return Needs(LatticeOut.BuildersMinor, path + ".colour") ? Coloured(made, draft["colour"], path + ".colour") : null;
    }

    internal L.PlanEdit? PlanEdit(JToken? token, string path)
    {
        if (Case(token, path, "rename", "owner", "visibility", "set_data", "clear_data", "recolour", "assign", "unassign") is not { } edit)
        {
            return null;
        }

        string at = path + "." + edit.Kind;
        switch (edit.Kind)
        {
            case "recolour" or "assign" or "unassign":
                return Needs(LatticeOut.BuildersMinor, at) ? BuildersEdit(edit.Kind, edit.Payload, at) : null;
            case "rename":
                return Text(edit.Payload, at) is { } name && Made(L.PlanName.Create(name), at) is { } planName
                    ? new L.PlanEdit.Rename(planName)
                    : null;
            case "owner":
                return Actor(edit.Payload, at) is { } owner ? new L.PlanEdit.ChangeOwner(owner) : null;
            case "visibility":
                return Visibility(edit.Payload, at) is { } visibility ? new L.PlanEdit.ChangeVisibility(visibility) : null;
            case "set_data":
            {
                if (Object(edit.Payload, at) is not { } set)
                {
                    return null;
                }

                L.AttachmentKey? key = AttachmentKey(set["key"], at + ".key");
                L.Attachment? value = Attachment(set["value"], at + ".value");
                return key is { } k && value is { } v ? new L.PlanEdit.SetData(k, v) : null;
            }
            default:
                return AttachmentKey(edit.Payload, at) is { } cleared ? new L.PlanEdit.ClearData(cleared) : null;
        }
    }

    internal L.SwapTo? SwapTo(JToken? token, string path)
    {
        if (Case(token, path, "piece", "same_shape_from") is not { } to)
        {
            return null;
        }

        return to.Kind == "piece"
            ? Piece(to.Payload, path + ".piece") is { } piece ? new L.SwapTo.Exactly(piece) : null
            : Kit(to.Payload, path + ".same_shape_from") is { } kit ? new L.SwapTo.SameShapeFrom(kit) : null;
    }

    internal L.Transformation? Transformation(JToken? token, string path)
    {
        if (Case(token, path, "move", "rotate", "mirror") is not { } how)
        {
            return null;
        }

        string at = path + "." + how.Kind;
        switch (how.Kind)
        {
            case "move":
                return Offset(how.Payload, at) is { } by ? new L.Transformation.Move(by) : null;
            case "rotate":
            {
                if (Object(how.Payload, at) is not { } rotate)
                {
                    return null;
                }

                L.Cell? pivot = Cell(rotate["pivot"], at + ".pivot");
                L.Turn? turn = Turn(rotate["turn"], at + ".turn");
                return pivot is { } p && turn is { } t ? new L.Transformation.Rotate(p, t) : null;
            }
            default:
            {
                if (Object(how.Payload, at) is not { } mirror)
                {
                    return null;
                }

                L.Axis? normal = Axis(mirror["normal"], at + ".normal");
                double? across = Number(mirror["at"], at + ".at");
                return normal is { } n && across is { } m
                    ? new L.Transformation.Mirror(new L.MirrorPlane(n, Decimetres(m)))
                    : null;
            }
        }
    }

    /// <summary>A template: {text} (Lattice's template form), {plan, name} (an active or finished plan's), or {name, ghosts}.</summary>
    internal L.Template? Template(JToken? token, string path, LatticeLookIn? looks = null)
    {
        if (Object(token, path) is not { } template)
        {
            return null;
        }

        if (Given(template, "text"))
        {
            return Text(template["text"], path + ".text") is { } text
                ? MadeRef(_system.Plans.ReadTemplate(text), path + ".text")
                : null;
        }

        L.TemplateName? name = Given(template, "name") && Text(template["name"], path + ".name") is { } given
            ? Made(L.TemplateName.Create(given), path + ".name")
            : Made(L.TemplateName.Create("template"), path);
        if (Given(template, "plan"))
        {
            return Plan(template["plan"], path + ".plan") is { } plan && name is { } n
                ? MadeRef(_system.Plans.AsTemplate(plan, n), path + ".plan")
                : null;
        }

        L.ValueList<L.GhostDraft> ghosts = Each(template["ghosts"], path + ".ghosts", (ghost, where) => Draft(ghost, where, looks));
        return name is { } named ? new L.Template(named, ghosts) : null;
    }

    internal L.RunRequest? Run(JToken? token, string path)
    {
        if (Object(token, path) is not { } run)
        {
            return null;
        }

        L.KitKey? kit = Kit(run["kit"], path + ".kit");
        L.Cell? from = Cell(run["from"], path + ".from");
        L.ValueList<L.Cell> via = Given(run, "via") ? EachValue(run["via"], path + ".via", Cell) : L.ValueList<L.Cell>.Empty;
        L.Cell? to = Cell(run["to"], path + ".to");
        L.Option<L.PlanId>? into = Into(run, "into", path);
        L.Maker? maker = MadeBy(run, path);
        L.Option<L.PortEnd>? fromPort = PortEndOf(run, "from_port", path);
        L.Option<L.PortEnd>? toPort = PortEndOf(run, "to_port", path);
        L.LayOptions? lay = Lay(run, path);
        return kit is { } k && from is { } f && to is { } t && into is { } i && maker is { } m && fromPort is { } fp &&
               toPort is { } tp && lay != null
            ? new L.RunRequest(k, f, via, t, i, m) { FromPort = fp, ToPort = tp, Lay = lay }
            : null;
    }

    internal L.RouteRequest? Route(JToken? token, string path)
    {
        if (Object(token, path) is not { } route)
        {
            return null;
        }

        L.KitKey? kit = Kit(route["kit"], path + ".kit");
        L.Cell? from = Cell(route["from"], path + ".from");
        L.Cell? to = Cell(route["to"], path + ".to");
        L.RouteToggles? toggles = Toggles(route["toggles"], path + ".toggles");
        L.Option<L.PlanId>? into = Into(route, "into", path);
        L.Maker? maker = MadeBy(route, path);
        L.Option<L.PortEnd>? fromPort = PortEndOf(route, "from_port", path);
        L.Option<L.PortEnd>? toPort = PortEndOf(route, "to_port", path);
        L.LayOptions? lay = Lay(route, path);
        L.Option<int> bound = L.Option<int>.None;
        if (Given(route, "search_bound") && Whole(route["search_bound"], path + ".search_bound") is { } limit)
        {
            bound = L.Option.Some(limit);
        }

        L.ValueList<L.RouteBranch> branches = Given(route, "branches")
            ? Each(route["branches"], path + ".branches", Branch)
            : L.ValueList<L.RouteBranch>.Empty;
        return kit is { } k && from is { } f && to is { } t && toggles != null && into is { } i && maker is { } m &&
               fromPort is { } fp && toPort is { } tp && lay != null
            ? new L.RouteRequest(k, f, t, toggles, i, m)
            {
                FromPort = fp, ToPort = tp, Lay = lay, SearchBound = bound, Branches = branches
            }
            : null;
    }

    private L.RouteBranch? Branch(JToken? token, string path)
    {
        if (Object(token, path) is not { } branch)
        {
            return null;
        }

        L.Cell? from = Cell(branch["from"], path + ".from");
        L.Option<L.PortEnd>? port = PortEndOf(branch, "port", path);
        return from is { } f && port is { } p ? new L.RouteBranch(f, p) : null;
    }

    internal L.Option<L.PortEnd>? PortEndOf(JObject owner, string key, string path)
    {
        if (!Given(owner, key))
        {
            return L.Option<L.PortEnd>.None;
        }

        string at = path + "." + key;
        if (Object(owner[key], at) is not { } end)
        {
            return null;
        }

        L.GhostId? ghost = Ghost(end["ghost"], at + ".ghost");
        L.PortIndex? port = Whole(end["port"], at + ".port") is { } index ? Made(L.PortIndex.Create(index), at + ".port") : null;
        return ghost is { } g && port is { } p ? L.Option.Some<L.PortEnd>(new L.PortEnd.OnGhost(g, p)) : null;
    }

    private L.LayOptions? Lay(JObject owner, string path)
    {
        if (!Given(owner, "lay"))
        {
            return L.LayOptions.Default;
        }

        if (Object(owner["lay"], path + ".lay") is not { } lay)
        {
            return null;
        }

        bool? single = Given(lay, "single_pieces_only") ? Flag(lay["single_pieces_only"], path + ".lay.single_pieces_only") : false;
        bool? grade = Given(lay, "match_grade") ? Flag(lay["match_grade"], path + ".lay.match_grade") : false;
        return single is { } s && grade is { } g ? new L.LayOptions(s, g) : null;
    }

    internal L.RouteToggles? Toggles(JToken? token, string path)
    {
        JObject toggles = token as JObject ?? new JObject();
        if (token != null && token.Type != JTokenType.Null && !(token is JObject))
        {
            Problem(path, "is an object of route toggles");
            return null;
        }

        bool Toggle(string name) =>
            Given(toggles, name) && (Flag(toggles[name], path + "." + name) ?? false);

        L.Option<L.FirstLeg> first = L.Option<L.FirstLeg>.None;
        if (Given(toggles, "first") && Case(toggles["first"], path + ".first", "vertical", "level") is { } leg)
        {
            first = L.Option.Some(leg.Kind == "vertical" ? L.FirstLeg.Vertical : L.FirstLeg.Level);
        }

        return new L.RouteToggles(Toggle("on_frames"), Toggle("fewest_bends"), Toggle("keep_doors_and_windows_clear"),
            Toggle("keep_ports_free"), Toggle("no_bridging"))
        {
            First = first,
            HugFramesAndWalls = Toggle("hug_frames_and_walls")
        };
    }
}
