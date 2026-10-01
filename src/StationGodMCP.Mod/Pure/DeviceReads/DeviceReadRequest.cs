#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.DeviceReads;

/// <summary>
/// read_devices' bounds. Over any of them the whole request is refused (invalid_argument) before anything is read.
/// A slot read without logic reads every slot logic type the device reads there; it counts AllSlotValuesWeight values.
/// </summary>
internal static class DeviceReadBounds
{
    internal const int MaximumItems = 128;
    internal const int MaximumLogicPerItem = 64;
    internal const int MaximumSlotsPerItem = 16;
    internal const int MaximumLogicPerSlot = 64;
    internal const int MaximumValues = 1024;
    internal const int AllSlotValuesWeight = 32;
    internal const int MaximumPort = 64;
}

/// <summary>
/// A logic or slot logic type as the client sent it: the key its value or error comes back under (the string as
/// sent, or a JSON number's own text), and the token the game side parses. Keyed by what was sent, never by the
/// enum's name: the enum has aliases that share a value, and "280" names a type by number.
/// </summary>
internal sealed class SentName
{
    internal SentName(string key, JToken token)
    {
        Key = key;
        Token = token;
    }

    internal string Key { get; }

    internal JToken Token { get; }
}

/// <summary>Which atmosphere an item asks for.</summary>
internal abstract class AtmosphereTarget
{
    private AtmosphereTarget()
    {
    }

    /// <summary>
    /// {}: the id's own atmosphere: a thing's internal one, a pipe's or landing pad piece's network, a network id's,
    /// or an atmosphere id's.
    /// </summary>
    internal sealed class Own : AtmosphereTarget
    {
        internal static readonly Own Instance = new Own();

        private Own()
        {
        }
    }

    /// <summary>{of: "internal"}: only the thing's internal atmosphere.</summary>
    internal sealed class Internal : AtmosphereTarget
    {
        internal static readonly Internal Instance = new Internal();

        private Internal()
        {
        }
    }

    /// <summary>{port: n}: the pipe network joined at the device's port n (its OpenEnds index).</summary>
    internal sealed class AtPort : AtmosphereTarget
    {
        internal AtPort(int port)
        {
            Port = port;
        }

        internal int Port { get; }
    }
}

/// <summary>One slot of an item: its index, and the slot logic types to read (null: every one the slot reads).</summary>
internal sealed class SlotReadRequest
{
    internal SlotReadRequest(int index, IReadOnlyList<SentName>? logic)
    {
        Index = index;
        Logic = logic;
    }

    internal int Index { get; }

    internal IReadOnlyList<SentName>? Logic { get; }

    /// <summary>The values this slot counts toward DeviceReadBounds.MaximumValues.</summary>
    internal int Weight => Logic?.Count ?? DeviceReadBounds.AllSlotValuesWeight;
}

/// <summary>One item: a reference id and the parts to read from it (at least one).</summary>
internal sealed class DeviceReadItem
{
    internal DeviceReadItem(long referenceId, IReadOnlyList<SentName>? logic, IReadOnlyList<SlotReadRequest>? slots,
        AtmosphereTarget? atmosphere, bool reagents)
    {
        ReferenceId = referenceId;
        Logic = logic;
        Slots = slots;
        Atmosphere = atmosphere;
        Reagents = reagents;
    }

    internal long ReferenceId { get; }

    internal IReadOnlyList<SentName>? Logic { get; }

    internal IReadOnlyList<SlotReadRequest>? Slots { get; }

    internal AtmosphereTarget? Atmosphere { get; }

    internal bool Reagents { get; }

    internal int Weight
    {
        get
        {
            int weight = Logic?.Count ?? 0;
            if (Slots != null)
            {
                foreach (SlotReadRequest slot in Slots)
                {
                    weight += slot.Weight;
                }
            }

            return weight;
        }
    }
}

/// <summary>A whole read_devices request: its items, in order, and whether the game clock is asked for.</summary>
internal sealed class DeviceReadRequest
{
    internal DeviceReadRequest(IReadOnlyList<DeviceReadItem> items, bool clock)
    {
        Items = items;
        Clock = clock;
    }

    internal IReadOnlyList<DeviceReadItem> Items { get; }

    internal bool Clock { get; }
}

/// <summary>A parsed request, or why it was refused (an invalid_argument message).</summary>
internal abstract class DeviceReadParse
{
    private DeviceReadParse()
    {
    }

    internal sealed class Parsed : DeviceReadParse
    {
        internal Parsed(DeviceReadRequest request)
        {
            Request = request;
        }

        internal DeviceReadRequest Request { get; }
    }

    internal sealed class Refused : DeviceReadParse
    {
        internal Refused(string message)
        {
            Message = message;
        }

        internal string Message { get; }
    }

    /// <summary>
    /// items and include as read_devices takes them. Strict inside the items too: a key an item, slot or atmosphere
    /// does not take is refused, as is a logic list naming the same string twice, a slot index given twice, and any
    /// bound of DeviceReadBounds. What a logic name means is left to the game side, which fails that value alone.
    /// </summary>
    internal static DeviceReadParse Of(JToken? items, JToken? include)
    {
        try
        {
            return new Parsed(new DeviceReadRequest(Items(items), Clock(include)));
        }
        catch (RefusedRequest refused)
        {
            return new Refused(refused.Message);
        }
    }

    private static readonly string[] ItemKeys = { "reference_id", "logic", "slots", "atmosphere", "reagents" };
    private static readonly string[] SlotKeys = { "index", "logic" };
    private static readonly string[] AtmosphereKeys = { "of", "port" };

    private static List<DeviceReadItem> Items(JToken? token)
    {
        if (!(token is JArray array) || array.Count == 0 || array.Count > DeviceReadBounds.MaximumItems)
        {
            throw new RefusedRequest(
                $"Argument 'items' must be an array of 1 to {DeviceReadBounds.MaximumItems} entries.");
        }

        List<DeviceReadItem> items = new List<DeviceReadItem>(array.Count);
        int values = 0;
        for (int index = 0; index < array.Count; index++)
        {
            DeviceReadItem item = Item(array[index], $"items[{index}]");
            values += item.Weight;
            items.Add(item);
        }

        if (values > DeviceReadBounds.MaximumValues)
        {
            throw new RefusedRequest(
                $"The request reads {values} values; at most {DeviceReadBounds.MaximumValues} in one call (a slot " +
                $"without logic counts {DeviceReadBounds.AllSlotValuesWeight}). Split it.");
        }

        return items;
    }

    private static DeviceReadItem Item(JToken token, string path)
    {
        JObject item = token as JObject ?? throw new RefusedRequest($"{path} must be an object.");
        RequireKnownKeys(item, path, ItemKeys);
        long id = ReferenceId(item["reference_id"], path);
        IReadOnlyList<SentName>? logic = Present(item["logic"]) is JToken logicToken
            ? Names(logicToken, $"{path}.logic", DeviceReadBounds.MaximumLogicPerItem)
            : null;
        IReadOnlyList<SlotReadRequest>? slots = Present(item["slots"]) is JToken slotsToken
            ? Slots(slotsToken, $"{path}.slots")
            : null;
        AtmosphereTarget? atmosphere = Present(item["atmosphere"]) is JToken atmosphereToken
            ? Atmosphere(atmosphereToken, $"{path}.atmosphere")
            : null;
        bool reagents = Present(item["reagents"]) is JToken reagentsToken && Bool(reagentsToken, $"{path}.reagents");
        if (logic == null && slots == null && atmosphere == null && !reagents)
        {
            throw new RefusedRequest($"{path} asks for nothing: give logic, slots, atmosphere or reagents: true.");
        }

        return new DeviceReadItem(id, logic, slots, atmosphere, reagents);
    }

    private static long ReferenceId(JToken? token, string path)
    {
        JToken present = Present(token) ?? throw new RefusedRequest($"{path}.reference_id is required.");
        string text = present.Type == JTokenType.String
            ? present.Value<string>() ?? string.Empty
            : present.Type == JTokenType.Integer ? present.ToString(Formatting.None) : string.Empty;
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            throw new RefusedRequest($"{path}.reference_id must be a reference id as a decimal string.");
        }

        return id;
    }

    private static IReadOnlyList<SentName> Names(JToken token, string path, int maximum)
    {
        if (!(token is JArray array) || array.Count == 0 || array.Count > maximum)
        {
            throw new RefusedRequest($"{path} must be an array of 1 to {maximum} logic type names or numbers.");
        }

        List<SentName> names = new List<SentName>(array.Count);
        HashSet<string> keys = new HashSet<string>(System.StringComparer.Ordinal);
        for (int index = 0; index < array.Count; index++)
        {
            JToken entry = array[index];
            string key = entry.Type switch
            {
                JTokenType.String => entry.Value<string>() ?? string.Empty,
                JTokenType.Integer or JTokenType.Float => entry.ToString(Formatting.None),
                _ => throw new RefusedRequest($"{path}[{index}] must be a logic type name or number."),
            };
            if (!keys.Add(key))
            {
                throw new RefusedRequest($"{path} names '{key}' twice; each value comes back under the name sent.");
            }

            names.Add(new SentName(key, entry));
        }

        return names;
    }

    private static IReadOnlyList<SlotReadRequest> Slots(JToken token, string path)
    {
        if (!(token is JArray array) || array.Count == 0 || array.Count > DeviceReadBounds.MaximumSlotsPerItem)
        {
            throw new RefusedRequest(
                $"{path} must be an array of 1 to {DeviceReadBounds.MaximumSlotsPerItem} slots, each {{index, logic}}.");
        }

        List<SlotReadRequest> slots = new List<SlotReadRequest>(array.Count);
        HashSet<int> seen = new HashSet<int>();
        for (int index = 0; index < array.Count; index++)
        {
            string entryPath = $"{path}[{index}]";
            JObject slot = array[index] as JObject ??
                           throw new RefusedRequest($"{entryPath} must be an object {{index, logic}}.");
            RequireKnownKeys(slot, entryPath, SlotKeys);
            int slotIndex = Integer(slot["index"], $"{entryPath}.index", 0, int.MaxValue) ??
                            throw new RefusedRequest($"{entryPath}.index is required.");
            if (!seen.Add(slotIndex))
            {
                throw new RefusedRequest($"{path} names slot {slotIndex} twice.");
            }

            IReadOnlyList<SentName>? logic = Present(slot["logic"]) is JToken logicToken
                ? Names(logicToken, $"{entryPath}.logic", DeviceReadBounds.MaximumLogicPerSlot)
                : null;
            slots.Add(new SlotReadRequest(slotIndex, logic));
        }

        return slots;
    }

    private static AtmosphereTarget Atmosphere(JToken token, string path)
    {
        JObject atmosphere = token as JObject ??
                             throw new RefusedRequest(
                                 $"{path} must be an object: {{}} (its own), {{of: \"internal\"}} or {{port: n}}.");
        RequireKnownKeys(atmosphere, path, AtmosphereKeys);
        JToken? of = Present(atmosphere["of"]);
        int? port = Integer(atmosphere["port"], $"{path}.port", 0, DeviceReadBounds.MaximumPort);
        if (of != null && port.HasValue)
        {
            throw new RefusedRequest($"{path} takes of or port, not both.");
        }

        if (of != null)
        {
            if (of.Type != JTokenType.String ||
                !string.Equals(of.Value<string>()?.Trim(), "internal", System.StringComparison.OrdinalIgnoreCase))
            {
                throw new RefusedRequest($"{path}.of must be \"internal\".");
            }

            return AtmosphereTarget.Internal.Instance;
        }

        return port.HasValue ? new AtmosphereTarget.AtPort(port.Value) : AtmosphereTarget.Own.Instance;
    }

    private static bool Clock(JToken? token)
    {
        if (Present(token) == null)
        {
            return false;
        }

        if (!(token is JArray array))
        {
            throw new RefusedRequest("Argument 'include' must be an array; it takes \"clock\".");
        }

        bool clock = false;
        for (int index = 0; index < array.Count; index++)
        {
            JToken entry = array[index];
            if (entry.Type != JTokenType.String ||
                !string.Equals(entry.Value<string>()?.Trim(), "clock", System.StringComparison.OrdinalIgnoreCase))
            {
                throw new RefusedRequest($"include[{index}] must be \"clock\"; nothing else can be included.");
            }

            clock = true;
        }

        return clock;
    }

    private static void RequireKnownKeys(JObject value, string path, string[] known)
    {
        foreach (JProperty property in value.Properties())
        {
            if (System.Array.IndexOf(known, property.Name) < 0)
            {
                throw new RefusedRequest(
                    $"{path} has an unknown key '{property.Name}'; it takes {string.Join(", ", known)}.");
            }
        }
    }

    private static bool Bool(JToken token, string path) =>
        token.Type == JTokenType.Boolean
            ? token.Value<bool>()
            : throw new RefusedRequest($"{path} must be true or false.");

    private static int? Integer(JToken? token, string path, int minimum, int maximum)
    {
        JToken? present = Present(token);
        if (present == null)
        {
            return null;
        }

        if (present.Type != JTokenType.Integer ||
            !long.TryParse(present.ToString(Formatting.None), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long value) || value < minimum || value > maximum)
        {
            throw new RefusedRequest($"{path} must be an integer from {minimum} to {maximum}.");
        }

        return (int)value;
    }

    // JSON null is an absent key, as every argument reads it.
    private static JToken? Present(JToken? token) => token == null || token.Type == JTokenType.Null ? null : token;

    private sealed class RefusedRequest : System.Exception
    {
        internal RefusedRequest(string message) : base(message)
        {
        }
    }
}
