#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>A slot on a thing: the thing's id and the slot's index in its Slots.</summary>
internal sealed class SlotRefView
{
    internal SlotRefView(ThingId id, int slot)
    {
        Id = id;
        Slot = slot;
    }

    public ThingId Id { get; }

    public int Slot { get; }
}

/// <summary>A slot a move would fill, and how many would go there.</summary>
internal sealed class SlotShareView
{
    internal SlotShareView(SlotRefView slot, int quantity)
    {
        Slot = slot;
        Quantity = quantity;
    }

    internal SlotRefView Slot { get; }

    internal int Quantity { get; }
}

/// <summary>
/// A slot a move spread over several filled: the slot, the stack there, how many the move added, whether they joined
/// a stack already there, and what the stack holds read back after the move (null when the slot no longer holds it).
/// </summary>
internal sealed class SlotFilledView
{
    internal SlotFilledView(SlotRefView to, ThingId referenceId, int added, bool merged, int? quantityNow)
    {
        To = to;
        ReferenceId = referenceId;
        Added = added;
        Merged = merged;
        QuantityNow = quantityNow;
    }

    public SlotRefView To { get; }

    public ThingId ReferenceId { get; }

    public int Added { get; }

    public bool Merged { get; }

    public int? QuantityNow { get; }
}

/// <summary>
/// One move that was made: the item and its name, where it was and went, and the stack it is in now. A move auto spread
/// over several slots (stacks topped up, the rest in an empty slot) names its first slot in to and lists every one in
/// filled.
/// </summary>
internal sealed class ItemMovedView : BatchItemView
{
    internal ItemMovedView(int index, ThingId referenceId, string displayName, SlotRefView? from, SlotRefView to,
        int quantityMoved, ThingId? mergedInto, ThingId destinationReferenceId, string? warning = null,
        List<SlotFilledView>? filled = null)
        : base(index, ok: true)
    {
        ReferenceId = referenceId;
        DisplayName = displayName;
        From = from;
        To = to;
        QuantityMoved = quantityMoved;
        MergedInto = mergedInto;
        DestinationReferenceId = destinationReferenceId;
        Warning = warning;
        Filled = filled;
    }

    public ThingId ReferenceId { get; }

    public string DisplayName { get; }

    /// <summary>Where the item was; null when it lay loose in the world.</summary>
    public SlotRefView? From { get; }

    public SlotRefView To { get; }

    public int QuantityMoved { get; }

    /// <summary>The stack the moved items joined, or null when they went into an empty slot.</summary>
    public ThingId? MergedInto { get; }

    /// <summary>The stack now in the destination slot: the item, the new stack a split made, or MergedInto.</summary>
    public ThingId DestinationReferenceId { get; }

    /// <summary>
    /// Null, or the game's error when one of its calls threw part way but the slot holds the result: the move is done.
    /// </summary>
    public string? Warning { get; }

    /// <summary>Every slot a move spread over several filled, in order; left out for a move into one slot.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SlotFilledView>? Filled { get; }
}

/// <summary>One move that was refused; nothing was changed for it.</summary>
internal sealed class ItemNotMovedView : BatchItemView
{
    internal ItemNotMovedView(int index, ThingId? referenceId, ApiException error) : base(index, ok: false)
    {
        ReferenceId = referenceId;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public ErrorView Error { get; }
}
