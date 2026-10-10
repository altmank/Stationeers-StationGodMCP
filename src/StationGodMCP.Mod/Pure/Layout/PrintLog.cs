#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Where an item came from: the machine that printed it (or its stack's), when, how many, split from what.</summary>
internal sealed class PrintRecord
{
    internal PrintRecord(long itemId, string? prefab, long makerId, string? makerPrefab, string? makerName,
        double gameTime, int quantity, long? splitFrom)
    {
        ItemId = itemId;
        Prefab = prefab;
        MakerId = makerId;
        MakerPrefab = makerPrefab;
        MakerName = makerName;
        GameTime = gameTime;
        Quantity = quantity;
        SplitFrom = splitFrom;
    }

    internal long ItemId { get; }

    internal string? Prefab { get; }

    internal long MakerId { get; }

    internal string? MakerPrefab { get; }

    internal string? MakerName { get; }

    /// <summary>GameManager.GameTime (seconds since this launch, stops while paused) when it was made.</summary>
    internal double GameTime { get; }

    internal int Quantity { get; }

    /// <summary>The stack it was split off (keeping that stack's maker and time); null for a print.</summary>
    internal long? SplitFrom { get; }

    /// <summary>A stack split off this item: same maker and print time, the new id, the split quantity.</summary>
    internal PrintRecord SplitInto(long newId, int quantity) =>
        new PrintRecord(newId, Prefab, MakerId, MakerPrefab, MakerName, GameTime, quantity, ItemId);
}

/// <summary>
/// The last prints since the mod loaded, oldest dropped first: a ring of Capacity records, one per item id (a newer
/// record for an id replaces the older). In memory only; a reload of the world or the game starts it empty.
/// Main thread only, with no lock: it is written by the print and split hooks (Prints), which the game calls from its
/// main-thread ticks and interactions, and cleared by the world change (WorldStores.Tick); requests read it.
/// </summary>
internal sealed class PrintLog
{
    internal const int DefaultCapacity = 2048;

    private readonly int _capacity;
    private readonly Dictionary<long, PrintRecord> _byItem = new Dictionary<long, PrintRecord>();
    private readonly Queue<long> _order = new Queue<long>();

    internal PrintLog(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
    }

    internal int Count => _byItem.Count;

    /// <summary>Whether recording stopped after a hook failed; Clear starts it again.</summary>
    internal bool Stopped { get; private set; }

    internal void Stop() => Stopped = true;

    internal int Capacity => _capacity;

    internal void Record(PrintRecord record)
    {
        if (!_byItem.ContainsKey(record.ItemId))
        {
            _order.Enqueue(record.ItemId);
        }

        _byItem[record.ItemId] = record;
        while (_byItem.Count > _capacity && _order.Count > 0)
        {
            _byItem.Remove(_order.Dequeue());
        }
    }

    /// <summary>A split: the new stack inherits the source's record when the source has one.</summary>
    internal void Split(long sourceId, long newId, int quantity)
    {
        if (_byItem.TryGetValue(sourceId, out PrintRecord source))
        {
            Record(source.SplitInto(newId, quantity));
        }
    }

    internal PrintRecord? Of(long itemId) => _byItem.TryGetValue(itemId, out PrintRecord record) ? record : null;

    internal void Clear()
    {
        _byItem.Clear();
        _order.Clear();
        Stopped = false;
    }
}

/// <summary>find_things' made_by and made_since: which printed items to keep.</summary>
internal sealed class PrintFilter
{
    internal PrintFilter(long? makerId, string? makerContains, double? since)
    {
        MakerId = makerId;
        MakerContains = makerContains;
        Since = since;
    }

    internal long? MakerId { get; }

    internal string? MakerContains { get; }

    internal double? Since { get; }

    internal bool IsActive => MakerId.HasValue || MakerContains != null || Since.HasValue;

    /// <summary>Whether a record meets the filter; an item with no record never does while the filter is active.</summary>
    internal bool Keeps(PrintRecord? record)
    {
        if (!IsActive)
        {
            return true;
        }

        if (record == null)
        {
            return false;
        }

        return (!MakerId.HasValue || record.MakerId == MakerId.Value) &&
               (MakerContains == null || Contains(record.MakerPrefab, MakerContains) ||
                Contains(record.MakerName, MakerContains)) &&
               (!Since.HasValue || record.GameTime >= Since.Value);
    }

    private static bool Contains(string? text, string part) =>
        text != null && text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
}
