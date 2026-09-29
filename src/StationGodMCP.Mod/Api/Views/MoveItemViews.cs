#nullable enable

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

/// <summary>One move that was made.</summary>
internal sealed class ItemMovedView : BatchItemView
{
    internal ItemMovedView(int index, ThingId referenceId, SlotRefView? from, SlotRefView to, int quantityMoved,
        ThingId? mergedInto, ThingId destinationReferenceId, string? warning = null) : base(index, ok: true)
    {
        ReferenceId = referenceId;
        From = from;
        To = to;
        QuantityMoved = quantityMoved;
        MergedInto = mergedInto;
        DestinationReferenceId = destinationReferenceId;
        Warning = warning;
    }

    public ThingId ReferenceId { get; }

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
