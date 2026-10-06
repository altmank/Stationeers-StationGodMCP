#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>One prefab inside a stored thing (a backpack's contents, a canister's...), summed over its stored children.</summary>
internal sealed class SiloContentView
{
    internal SiloContentView(string? prefabName, double quantity)
    {
        PrefabName = prefabName;
        Quantity = VaultAmount.Of(quantity);
    }

    public string? PrefabName { get; }

    public double Quantity { get; }
}

/// <summary>
/// One entry of a silo's store: a thing the silo imported, kept as save data until it is exported or withdrawn. It is
/// not a thing in the world: reference_id is always null, so move_item cannot take it; silo_withdraw does.
/// </summary>
internal sealed class SiloEntryView
{
    internal SiloEntryView(int index, string? prefabName, string? displayName, double quantity, double? maxQuantity,
        int children, List<SiloContentView> contents, bool rotsWhenTaken)
    {
        Index = index;
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Quantity = VaultAmount.Of(quantity);
        MaxQuantity = maxQuantity;
        Children = children;
        Contents = contents;
        RotsWhenTaken = rotsWhenTaken;
    }

    /// <summary>Its place in the store: 0 is exported next.</summary>
    public int Index { get; }

    public ThingId? ReferenceId => null;

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>A stack's quantity, 1 for anything else.</summary>
    public double Quantity { get; }

    public double? MaxQuantity { get; }

    /// <summary>Things stored inside it, at any depth.</summary>
    public int Children { get; }

    public List<SiloContentView> Contents { get; }

    /// <summary>Food the silo's export rule spoils: it comes out rotten, by the silo's export and by silo_withdraw.</summary>
    public bool RotsWhenTaken { get; }
}

/// <summary>A silo's store as container_contents shows it, after the silo's own slots.</summary>
internal sealed class SiloContentsView
{
    internal SiloContentsView(int count, bool entriesKnown, SiloBusyView? busy, Slice<SiloEntryView> entries)
    {
        Count = count;
        EntriesKnown = entriesKnown;
        Importing = busy?.Importing;
        Exporting = busy?.Exporting;
        DispenseSlot = busy?.DispenseSlot;
        Total = entries.Total;
        Offset = entries.Offset;
        Entries = entries.Items;
    }

    /// <summary>Entries stored (one per imported thing).</summary>
    public int Count { get; }

    public int Capacity => SiloRules.Capacity;

    /// <summary>False on a multiplayer client: only the host keeps the store; count is the game's synced count.</summary>
    public bool EntriesKnown { get; }

    /// <summary>The silo is saving an import (it enters the store when done); null on a client.</summary>
    public bool? Importing { get; }

    /// <summary>An exported thing is in the export slot or its contents are still coming out; null on a client.</summary>
    public bool? Exporting { get; }

    /// <summary>The entry an IC asked to dispense next (DispenseSlot), -1 for none; null on a client.</summary>
    public int? DispenseSlot { get; }

    /// <summary>Entries that match the filter (all without one).</summary>
    public int Total { get; }

    public int Offset { get; }

    public List<SiloEntryView> Entries { get; }
}

/// <summary>What a silo is busy with, as the host reads it.</summary>
internal sealed class SiloBusyView
{
    internal SiloBusyView(bool importing, bool exporting, int dispenseSlot)
    {
        Importing = importing;
        Exporting = exporting;
        DispenseSlot = dispenseSlot;
    }

    internal bool Importing { get; }

    internal bool Exporting { get; }

    internal int DispenseSlot { get; }
}

/// <summary>A silo's entry count before and after a deposit or withdrawal, and its capacity.</summary>
internal sealed class SiloCountView
{
    internal SiloCountView(int before, int after)
    {
        Before = before;
        After = after;
    }

    public int Before { get; }

    public int After { get; }

    public int Capacity => SiloRules.Capacity;
}

/// <summary>The silo a request used, and its power (the silo imports and exports only when on and powered).</summary>
internal sealed class SiloRefView
{
    internal SiloRefView(ThingView silo, bool onOff, bool powered)
    {
        Silo = silo;
        OnOff = onOff;
        Powered = powered;
    }

    public ThingView Silo { get; }

    public bool OnOff { get; }

    public bool Powered { get; }
}

/// <summary>What a withdrawal took from one entry.</summary>
internal sealed class SiloTakenView
{
    internal SiloTakenView(int index, double quantity, double left, bool whole, int children)
    {
        Index = index;
        Quantity = VaultAmount.Of(quantity);
        Left = VaultAmount.Of(left);
        Whole = whole;
        Children = children;
    }

    /// <summary>Its place in the store before the withdrawal.</summary>
    public int Index { get; }

    public double Quantity { get; }

    /// <summary>What the entry keeps; 0 when it left the store.</summary>
    public double Left { get; }

    /// <summary>The stored thing itself was loaded with its contents, rather than pooled into stacks.</summary>
    public bool Whole { get; }

    public int Children { get; }
}

/// <summary>The prefab's total in the silo before, the change, and after (read back from the store on a real run).</summary>
internal sealed class SiloStockView
{
    internal SiloStockView(string? prefabName, string? displayName, double before, double change, double after)
    {
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Before = VaultAmount.Of(before);
        Change = VaultAmount.Of(change);
        After = VaultAmount.Of(after);
    }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public double Before { get; }

    public double Change { get; }

    public double After { get; }
}

/// <summary>silo_withdraw's reply.</summary>
internal sealed class SiloWithdrawView
{
    internal SiloWithdrawView(bool dryRun, SiloRefView silo, ThingView to, double quantity, List<SiloTakenView> taken,
        List<PlacedView> placed, SiloStockView stock, SiloCountView count)
    {
        DryRun = dryRun;
        Silo = silo.Silo;
        OnOff = silo.OnOff;
        Powered = silo.Powered;
        To = to;
        Quantity = VaultAmount.Of(quantity);
        Taken = taken;
        Placed = placed;
        Stock = stock;
        Count = count;
    }

    public bool DryRun { get; }

    public ThingView Silo { get; }

    public bool OnOff { get; }

    public bool Powered { get; }

    public ThingView To { get; }

    public double Quantity { get; }

    /// <summary>The entries taken, front first.</summary>
    public List<SiloTakenView> Taken { get; }

    public List<PlacedView> Placed { get; }

    public SiloStockView Stock { get; }

    public SiloCountView Count { get; }
}

/// <summary>One thing a deposit stored (or would store, in a dry run).</summary>
internal sealed class SiloDepositedView : BatchItemView
{
    internal SiloDepositedView(int index, ThingView thing, SlotRefView? from, double quantity, double leftInSource,
        int children, int entry) : base(index, ok: true)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        From = from;
        Quantity = VaultAmount.Of(quantity);
        LeftInSource = VaultAmount.Of(leftInSource);
        Children = children;
        Entry = entry;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The slot it was in; null when it lay in the world.</summary>
    public SlotRefView? From { get; }

    public double Quantity { get; }

    /// <summary>What stays in the source stack; 0 when the whole thing went (and was destroyed, as the import does).</summary>
    public double LeftInSource { get; }

    /// <summary>Things inside it, at any depth, stored with it in the same entry.</summary>
    public int Children { get; }

    /// <summary>The entry's place in the store (deposits go to the back).</summary>
    public int Entry { get; }
}

/// <summary>silo_deposit's reply.</summary>
internal sealed class SiloDepositView
{
    internal SiloDepositView(bool dryRun, SiloRefView silo, int? matched, int skipped, bool truncated,
        BatchResultView items, SiloCountView count)
    {
        DryRun = dryRun;
        Silo = silo.Silo;
        OnOff = silo.OnOff;
        Powered = silo.Powered;
        Matched = matched;
        Skipped = skipped;
        Truncated = truncated;
        Items = items;
        Count = count;
    }

    public bool DryRun { get; }

    public ThingView Silo { get; }

    public bool OnOff { get; }

    public bool Powered { get; }

    /// <summary>Filter form: how many items the filter named before the silo's rule; null for the id form.</summary>
    public int? Matched { get; }

    /// <summary>Filter form: items left out because the silo's import does not take them.</summary>
    public int Skipped { get; }

    /// <summary>Filter form: more items matched than limit; only the first limit were taken.</summary>
    public bool Truncated { get; }

    public BatchResultView Items { get; }

    public SiloCountView Count { get; }
}

/// <summary>
/// A silo entry (or a thing stored inside one) listed by find_items like an item, with location silo. Not a thing in
/// the world: reference_id is null and held_in names the silo with slot_index -1. silo says which entry.
/// </summary>
internal sealed class SiloItemView : IFoundItemView
{
    internal const int NoSlot = -1;
    internal const string StoreSlotName = "silo store";

    internal SiloItemView(string? prefabName, string? displayName, double quantity, double? maxQuantity,
        List<HeldInView> heldIn, PositionView position, double? distanceM, SiloPlaceView silo)
    {
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Quantity = quantity;
        MaxQuantity = maxQuantity;
        HeldIn = heldIn;
        Position = position;
        DistanceM = distanceM;
        Silo = silo;
    }

    public ThingId? ReferenceId => null;

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public double Quantity { get; }

    public double? MaxQuantity { get; }

    public string Location => SiloPlaceView.Location;

    public string? CarriedBy => null;

    public List<HeldInView> HeldIn { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    public SiloPlaceView Silo { get; }
}

/// <summary>Which silo entry a listed silo item is: the entry's index, and the stored thing holding it (null for the entry itself).</summary>
internal sealed class SiloPlaceView
{
    internal const string Location = "silo";

    internal SiloPlaceView(int entry, string? inside)
    {
        Entry = entry;
        Inside = inside;
    }

    public int Entry { get; }

    /// <summary>The prefab of the entry's stored thing when this is inside it (a backpack's contents); null otherwise.</summary>
    public string? Inside { get; }

    public bool Movable => false;
}
