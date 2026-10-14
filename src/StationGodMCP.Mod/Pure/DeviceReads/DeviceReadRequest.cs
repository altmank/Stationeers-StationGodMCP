#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure.DeviceReads;

/// <summary>
/// read logic' bounds. Over any of them the whole request is refused (invalid_argument) before anything is read.
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

/// <summary>One item: an id (a reference id, or "looking_at") and the parts to read from it (at least one).</summary>
internal sealed class DeviceReadItem
{
    internal DeviceReadItem(string id, IReadOnlyList<SentName>? logic, IReadOnlyList<SlotReadRequest>? slots,
        AtmosphereTarget? atmosphere, bool reagents)
    {
        Id = id;
        Logic = logic;
        Slots = slots;
        Atmosphere = atmosphere;
        Reagents = reagents;
    }

    internal string Id { get; }

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

/// <summary>A read of many things: its items, in order, and whether the game clock is asked for.</summary>
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
