#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure.DeviceReads;

/// <summary>One (id, logic type) pair read logic reads: a thing's id (or "looking_at") and a logic type name or number.</summary>
internal sealed class LogicPair
{
    internal LogicPair(string id, JToken logic)
    {
        Id = id;
        Logic = logic;
    }

    public string Id { get; }

    public JToken Logic { get; }
}

/// <summary>
/// read logic's request (catalogue read.json, what logic), in its three reply forms: One, one id with one logic type;
/// Pairs, id and logic lists (every id with every logic type) or reads whose every entry is {id, logic} with one logic
/// type, one row per pair, up to 256; Items, reads with a logic list, slots, atmosphere or reagents, or include clock,
/// one row per entry in one frame, up to 128 entries and 1,024 values. Built with every problem at once (Of), in verb
/// names; what a logic name means is left to the game, which fails that value alone.
/// </summary>
internal abstract class LogicReadRequest
{
    internal const int MaximumPairs = 256;

    private const string Clock = "clock";

    private static readonly string[] ReadKeys = { "id", "logic", "slots", "atmosphere", "reagents" };
    private static readonly string[] SlotKeys = { "index", "logic" };
    private static readonly string[] AtmosphereKeys = { "of", "port" };

    private LogicReadRequest()
    {
    }

    internal sealed class One : LogicReadRequest
    {
        internal One(LogicPair pair)
        {
            Pair = pair;
        }

        public LogicPair Pair { get; }
    }

    internal sealed class Pairs : LogicReadRequest
    {
        internal Pairs(IReadOnlyList<LogicPair> entries)
        {
            Entries = entries;
        }

        public IReadOnlyList<LogicPair> Entries { get; }
    }

    internal sealed class Items : LogicReadRequest
    {
        internal Items(DeviceReadRequest request)
        {
            Request = request;
        }

        public DeviceReadRequest Request { get; }
    }

    /// <summary>
    /// The request the options give; null with what is wrong with them in problems. bounded false leaves the size bounds
    /// to the caller (a subscription answers them as its own limits).
    /// </summary>
    internal static LogicReadRequest? Of(ReadLogicOptions options, List<OptionProblem> problems, bool bounded = true)
    {
        int before = problems.Count;
        PerIdList? ids = options.Selector.Ids;
        bool clock = options.Fields.Includes(Clock);
        LogicReadRequest? request;
        if (ids != null && options.Reads != null)
        {
            problems.Add(new OptionProblem("reads", "goes without id: give id with logic, or reads"));
            request = null;
        }
        else if (ids != null)
        {
            request = OfIds(ids, options.Logic, clock, problems);
        }
        else if (options.Reads is JArray reads && reads.Count > 0)
        {
            request = OfReads(reads, options.Logic, clock, problems);
        }
        else
        {
            problems.Add(new OptionProblem(options.Reads != null ? "reads" : "id",
                "is needed: id with logic, or reads, the values to read"));
            request = null;
        }

        if (bounded && request != null)
        {
            Bounds(request, ids != null ? "id" : "reads", problems);
        }

        return problems.Count > before ? null : request;
    }

    // The bounds of one read: 256 pairs, or 128 items and 1,024 values.
    private static void Bounds(LogicReadRequest request, string path, List<OptionProblem> problems)
    {
        if (request is Pairs pairs && pairs.Entries.Count > MaximumPairs)
        {
            problems.Add(new OptionProblem(path,
                $"names {pairs.Entries.Count} (id, logic) pairs; at most {MaximumPairs} in one call"));
        }

        if (request is not Items { Request: var items })
        {
            return;
        }

        if (items.Items.Count > DeviceReadBounds.MaximumItems)
        {
            problems.Add(new OptionProblem(path,
                $"names {items.Items.Count} things to read with slots, atmosphere, reagents, a logic list or the " +
                $"clock; at most {DeviceReadBounds.MaximumItems} in one call"));
            return;
        }

        int values = 0;
        foreach (DeviceReadItem item in items.Items)
        {
            values += item.Weight;
        }

        if (values > DeviceReadBounds.MaximumValues)
        {
            problems.Add(new OptionProblem(path,
                $"reads {values} values; at most {DeviceReadBounds.MaximumValues} in one call (a slot without logic " +
                $"counts {DeviceReadBounds.AllSlotValuesWeight})"));
        }
    }

    private static LogicReadRequest? OfIds(PerIdList ids, JToken? logic, bool clock, List<OptionProblem> problems)
    {
        IReadOnlyList<SentName>? types = logic != null ? Names(logic, "logic", problems) : null;
        if (logic == null)
        {
            problems.Add(new OptionProblem("logic", "is needed with id: the logic types to read"));
        }

        if (types == null)
        {
            return null;
        }

        if (clock)
        {
            List<DeviceReadItem> items = new List<DeviceReadItem>(ids.Ids.Count);
            foreach (string id in ids.Ids)
            {
                items.Add(new DeviceReadItem(id, types, null, null, false));
            }

            return new Items(new DeviceReadRequest(items, true));
        }

        if (!ids.GivenAsList && logic is not JArray)
        {
            return new One(new LogicPair(ids.Ids[0], types[0].Token));
        }

        List<LogicPair> pairs = new List<LogicPair>(ids.Ids.Count * types.Count);
        foreach (string id in ids.Ids)
        {
            foreach (SentName type in types)
            {
                pairs.Add(new LogicPair(id, type.Token));
            }
        }

        return new Pairs(pairs);
    }

    private static LogicReadRequest? OfReads(JArray reads, JToken? logic, bool clock, List<OptionProblem> problems)
    {
        List<DeviceReadItem> items = new List<DeviceReadItem>(reads.Count);
        List<LogicPair> pairs = new List<LogicPair>(reads.Count);
        bool allPairs = !clock;
        for (int index = 0; index < reads.Count; index++)
        {
            string path = $"reads[{index}]";
            if (reads[index] is not JObject read)
            {
                problems.Add(new OptionProblem(path, "must be {id, logic, slots, atmosphere, reagents}"));
                allPairs = false;
                continue;
            }

            Known(read, path, ReadKeys, problems);
            string? id = read["id"] is JValue { Type: JTokenType.String } given && ((string)given!).Trim().Length > 0
                ? ((string)given!).Trim()
                : null;
            if (id == null)
            {
                problems.Add(new OptionProblem($"{path}.id", "is needed: a thing's id, a non-empty string"));
            }

            JToken? types = Present(read["logic"]) ?? logic;
            IReadOnlyList<SentName>? names = types != null ? Names(types, $"{path}.logic", problems) : null;
            IReadOnlyList<SlotReadRequest>? slots = Present(read["slots"]) is { } slotsToken
                ? Slots(slotsToken, $"{path}.slots", problems)
                : null;
            AtmosphereTarget? atmosphere = Present(read["atmosphere"]) is { } atmosphereToken
                ? Atmosphere(atmosphereToken, $"{path}.atmosphere", problems)
                : null;
            bool reagents = Present(read["reagents"]) is { } reagentsToken && Flag(reagentsToken, $"{path}.reagents", problems);
            if (types == null && Present(read["slots"]) == null && atmosphere == null && !reagents)
            {
                problems.Add(new OptionProblem(path, "asks for nothing: give logic, slots, atmosphere or reagents: true"));
            }

            bool pair = types is not JArray && names is { Count: 1 } && slots == null && atmosphere == null && !reagents;
            allPairs &= pair;
            if (id != null)
            {
                items.Add(new DeviceReadItem(id, names, slots, atmosphere, reagents));
                if (pair)
                {
                    pairs.Add(new LogicPair(id, names![0].Token));
                }
            }
        }

        return allPairs ? new Pairs(pairs) : new Items(new DeviceReadRequest(items, clock));
    }

    /// <summary>
    /// The request as items, one per id in the order first named (a pair's logic types gathered under its id), as a
    /// devices subscription reads it every sample.
    /// </summary>
    internal DeviceReadRequest AsItems()
    {
        switch (this)
        {
            case Items items:
                return items.Request;
            case One one:
                return new DeviceReadRequest(new[] { ItemOf(one.Pair.Id, new List<LogicPair> { one.Pair }) }, false);
            default:
                List<string> order = new List<string>();
                Dictionary<string, List<LogicPair>> byId = new Dictionary<string, List<LogicPair>>(StringComparer.Ordinal);
                foreach (LogicPair pair in ((Pairs)this).Entries)
                {
                    if (!byId.TryGetValue(pair.Id, out List<LogicPair>? pairs))
                    {
                        byId.Add(pair.Id, pairs = new List<LogicPair>());
                        order.Add(pair.Id);
                    }

                    pairs.Add(pair);
                }

                List<DeviceReadItem> listed = new List<DeviceReadItem>(order.Count);
                foreach (string id in order)
                {
                    listed.Add(ItemOf(id, byId[id]));
                }

                return new DeviceReadRequest(listed, false);
        }
    }

    private static DeviceReadItem ItemOf(string id, List<LogicPair> pairs)
    {
        List<SentName> names = new List<SentName>(pairs.Count);
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (LogicPair pair in pairs)
        {
            string key = pair.Logic.Type == JTokenType.String ? (string)pair.Logic! : pair.Logic.ToString(Formatting.None);
            if (keys.Add(key))
            {
                names.Add(new SentName(key, pair.Logic));
            }
        }

        return new DeviceReadItem(id, names, null, null, false);
    }

    // logic: a name or number, or a list of them, each named once.
    private static IReadOnlyList<SentName>? Names(JToken token, string path, List<OptionProblem> problems)
    {
        JArray listed = token as JArray ?? new JArray(token);
        if (listed.Count == 0 || listed.Count > DeviceReadBounds.MaximumLogicPerItem)
        {
            problems.Add(new OptionProblem(path,
                $"must be a logic type, or a list of 1 to {DeviceReadBounds.MaximumLogicPerItem} of them"));
            return null;
        }

        List<SentName> names = new List<SentName>(listed.Count);
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        bool ok = true;
        for (int index = 0; index < listed.Count; index++)
        {
            JToken entry = listed[index];
            string entryPath = token is JArray ? $"{path}[{index}]" : path;
            string? key = entry.Type switch
            {
                JTokenType.String when ((string)entry!).Trim().Length > 0 => (string)entry!,
                JTokenType.Integer => entry.ToString(Formatting.None),
                _ => null
            };
            if (key == null)
            {
                problems.Add(new OptionProblem(entryPath, "must be a logic type's name or number"));
                ok = false;
            }
            else if (!keys.Add(key))
            {
                problems.Add(new OptionProblem(entryPath, $"names '{key}' twice; each value comes back under the name sent"));
                ok = false;
            }
            else
            {
                names.Add(new SentName(key, entry));
            }
        }

        return ok ? names : null;
    }

    private static IReadOnlyList<SlotReadRequest>? Slots(JToken token, string path, List<OptionProblem> problems)
    {
        if (token is not JArray listed || listed.Count == 0 || listed.Count > DeviceReadBounds.MaximumSlotsPerItem)
        {
            problems.Add(new OptionProblem(path,
                $"must be a list of 1 to {DeviceReadBounds.MaximumSlotsPerItem} slots, each {{index, logic}}"));
            return null;
        }

        List<SlotReadRequest> slots = new List<SlotReadRequest>(listed.Count);
        HashSet<int> seen = new HashSet<int>();
        bool ok = true;
        for (int index = 0; index < listed.Count; index++)
        {
            string entryPath = $"{path}[{index}]";
            if (listed[index] is not JObject slot)
            {
                problems.Add(new OptionProblem(entryPath, "must be {index, logic}"));
                ok = false;
                continue;
            }

            Known(slot, entryPath, SlotKeys, problems);
            int? slotIndex = Integer(slot["index"], $"{entryPath}.index", 0, int.MaxValue, problems);
            if (Present(slot["index"]) == null)
            {
                problems.Add(new OptionProblem($"{entryPath}.index", "is needed: the slot's index"));
            }

            IReadOnlyList<SentName>? logic = Present(slot["logic"]) is { } logicToken
                ? Names(logicToken, $"{entryPath}.logic", problems)
                : null;
            if (slotIndex is not { } number)
            {
                ok = false;
            }
            else if (!seen.Add(number))
            {
                problems.Add(new OptionProblem($"{entryPath}.index", $"names slot {number} twice"));
                ok = false;
            }
            else
            {
                slots.Add(new SlotReadRequest(number, logic));
            }
        }

        return ok ? slots : null;
    }

    // {} or true: the thing's own; {of: "internal"}; {port: n}; false asks for none.
    private static AtmosphereTarget? Atmosphere(JToken token, string path, List<OptionProblem> problems)
    {
        if (token.Type == JTokenType.Boolean)
        {
            return (bool)token ? AtmosphereTarget.Own.Instance : null;
        }

        if (token is not JObject atmosphere)
        {
            problems.Add(new OptionProblem(path, "must be {} (its own), {of: \"internal\"} or {port: n}"));
            return null;
        }

        Known(atmosphere, path, AtmosphereKeys, problems);
        JToken? of = Present(atmosphere["of"]);
        int? port = Integer(atmosphere["port"], $"{path}.port", 0, DeviceReadBounds.MaximumPort, problems);
        if (of != null && Present(atmosphere["port"]) != null)
        {
            problems.Add(new OptionProblem(path, "takes of or port, not both"));
            return null;
        }

        if (of != null)
        {
            if (of.Type != JTokenType.String ||
                !string.Equals(((string)of!).Trim(), "internal", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new OptionProblem($"{path}.of", "must be \"internal\""));
                return null;
            }

            return AtmosphereTarget.Internal.Instance;
        }

        return port.HasValue ? new AtmosphereTarget.AtPort(port.Value) : AtmosphereTarget.Own.Instance;
    }

    private static void Known(JObject value, string path, string[] keys, List<OptionProblem> problems)
    {
        foreach (JProperty property in value.Properties())
        {
            if (Array.IndexOf(keys, property.Name) < 0)
            {
                problems.Add(new OptionProblem($"{path}.{property.Name}",
                    $"not a key here; it takes {string.Join(", ", keys)}", NearestNames.Of(property.Name, keys)));
            }
        }
    }

    private static bool Flag(JToken token, string path, List<OptionProblem> problems)
    {
        if (token.Type == JTokenType.Boolean)
        {
            return (bool)token;
        }

        problems.Add(new OptionProblem(path, "must be true or false"));
        return false;
    }

    private static int? Integer(JToken? token, string path, int minimum, int maximum, List<OptionProblem> problems)
    {
        JToken? present = Present(token);
        if (present == null)
        {
            return null;
        }

        if (present.Type == JTokenType.Integer &&
            long.TryParse(present.ToString(Formatting.None), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long value) && value >= minimum && value <= maximum)
        {
            return (int)value;
        }

        problems.Add(new OptionProblem(path, $"must be an integer from {minimum} to {maximum}"));
        return null;
    }

    // JSON null is a key left out, as every option reads it.
    private static JToken? Present(JToken? token) => token == null || token.Type == JTokenType.Null ? null : token;
}
