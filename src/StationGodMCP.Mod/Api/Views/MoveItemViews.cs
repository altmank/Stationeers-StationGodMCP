#nullable enable

using System.Collections.Generic;
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
internal sealed class ItemNotMovedView : BatchItemView, IBatchFailure
{
    internal ItemNotMovedView(int index, ThingId? referenceId, ApiException error) : base(index, ok: false)
    {
        ReferenceId = referenceId;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public ErrorView Error { get; }
}

/// <summary>One move a report checked: what it would move, from where to where; nothing was moved.</summary>
internal sealed class MovePlannedView : BatchItemView
{
    internal MovePlannedView(int index, ThingId referenceId, SlotRefView? from, SlotRefView to, int quantityMoved,
        ThingId? mergedInto) : base(index, ok: true)
    {
        ReferenceId = referenceId;
        From = from;
        To = to;
        QuantityMoved = quantityMoved;
        MergedInto = mergedInto;
    }

    public ThingId ReferenceId { get; }

    /// <summary>Where the item is; null when it lies loose in the world.</summary>
    public SlotRefView? From { get; }

    public SlotRefView To { get; }

    /// <summary>How many would move (one for planting or fertilising).</summary>
    public int QuantityMoved { get; }

    /// <summary>The stack the items would join, or null for an empty slot.</summary>
    public ThingId? MergedInto { get; }
}

/// <summary>move_item's report (dry_run) of one move: the move its checks passed; nothing moved.</summary>
internal sealed class MoveReportView
{
    internal MoveReportView(MovePlannedView planned)
    {
        ReferenceId = planned.ReferenceId;
        From = planned.From;
        To = planned.To;
        QuantityMoved = planned.QuantityMoved;
        MergedInto = planned.MergedInto;
    }

    public bool ReportOnly => true;

    /// <summary>Always empty: a refused check answers ReportRefusedView instead.</summary>
    public List<ProblemView> Problems { get; } = new List<ProblemView>();

    public ThingId ReferenceId { get; }

    public SlotRefView? From { get; }

    public SlotRefView To { get; }

    public int QuantityMoved { get; }

    public ThingId? MergedInto { get; }
}

/// <summary>
/// move_item's report (dry_run) of a moves list: each move planned or refused, each refusal also a problem with its
/// index; nothing moved.
/// </summary>
internal sealed class MovesReportView
{
    internal MovesReportView(BatchResultView results)
    {
        Problems = ProblemView.Of(results.Results);
        Results = results.Results;
        Count = results.Count;
        SuccessCount = results.SuccessCount;
        ErrorCount = results.ErrorCount;
    }

    public bool ReportOnly => true;

    public List<ProblemView> Problems { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }
}
