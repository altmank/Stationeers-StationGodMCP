#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>Vault amounts on the wire: exact to 1e-6 (ingots are float grams, ores whole counts).</summary>
internal static class VaultAmount
{
    internal const int Decimals = 6;

    internal static double Of(double amount) => Math.Round(amount, Decimals);
}

/// <summary>The vault a request used, the Remote Vault it was reached through (or null), and its power.</summary>
internal sealed class VaultRefView
{
    internal VaultRefView(ThingView vault, ThingView? via, bool onOff, bool powered)
    {
        Vault = vault;
        Via = via;
        OnOff = onOff;
        Powered = powered;
    }

    public ThingView Vault { get; }

    public ThingView? Via { get; }

    public bool OnOff { get; }

    public bool Powered { get; }
}

/// <summary>
/// One kind of thing a vault stores: kind ingot (reagent names the stored reagent, prefab_name the ingot a vend makes)
/// or ore (ores and ices; reagent null). max_stack is the item's full stack, null when its prefab is gone.
/// </summary>
internal sealed class VaultItemView
{
    internal VaultItemView(string kind, string? prefabName, string? displayName, string? reagent, double? maxStack)
    {
        Kind = kind;
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Reagent = reagent;
        MaxStack = maxStack;
    }

    public string Kind { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string? Reagent { get; }

    public double? MaxStack { get; }
}

/// <summary>A stored amount: the item's fields and its quantity (grams of ingot, or a count of ore).</summary>
internal sealed class VaultStockView
{
    internal VaultStockView(VaultItemView item, double quantity)
    {
        Kind = item.Kind;
        PrefabName = item.PrefabName;
        DisplayName = item.DisplayName;
        Reagent = item.Reagent;
        MaxStack = item.MaxStack;
        Quantity = VaultAmount.Of(quantity);
    }

    public string Kind { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string? Reagent { get; }

    public double? MaxStack { get; }

    public double Quantity { get; }
}

/// <summary>One stock line a deposit or withdrawal touched: before, the change (+ in, - out), and after.</summary>
internal sealed class StockChangeView
{
    internal StockChangeView(VaultItemView item, double before, double change, double after)
    {
        Kind = item.Kind;
        PrefabName = item.PrefabName;
        DisplayName = item.DisplayName;
        Reagent = item.Reagent;
        Before = VaultAmount.Of(before);
        Change = VaultAmount.Of(change);
        After = VaultAmount.Of(after);
    }

    public string Kind { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string? Reagent { get; }

    public double Before { get; }

    public double Change { get; }

    public double After { get; }
}

/// <summary>One vault and everything it stores.</summary>
internal sealed class VaultContentsEntryView
{
    internal VaultContentsEntryView(ThingView vault, PositionView position, bool onOff, bool powered,
        List<VaultStockView> stock, int pendingVends)
    {
        Vault = vault;
        Position = position;
        OnOff = onOff;
        Powered = powered;
        Stock = stock;
        PendingVends = pendingVends;
    }

    public ThingView Vault { get; }

    public PositionView Position { get; }

    public bool OnOff { get; }

    public bool Powered { get; }

    public List<VaultStockView> Stock { get; }

    /// <summary>Vends taken from the store and still waiting to be made into the export slot.</summary>
    public int PendingVends { get; }
}

/// <summary>A Remote Vault: the vault it reaches (null when none) and the vault mod's connection state.</summary>
internal sealed class RemoteVaultView
{
    internal RemoteVaultView(ThingView remote, ThingId? vaultId, string connection)
    {
        Remote = remote;
        VaultId = vaultId;
        Connection = connection;
    }

    public ThingView Remote { get; }

    public ThingId? VaultId { get; }

    /// <summary>None, NoVaultDetected, MultipleVaultsDetected or VaultPoweredOff (IngotVault's own names).</summary>
    public string Connection { get; }
}

/// <summary>vault_contents: every vault asked for, and the Remote Vaults.</summary>
internal sealed class VaultContentsView
{
    internal VaultContentsView(List<VaultContentsEntryView> vaults, List<RemoteVaultView> remoteVaults)
    {
        Vaults = vaults;
        RemoteVaults = remoteVaults;
    }

    public List<VaultContentsEntryView> Vaults { get; }

    public List<RemoteVaultView> RemoteVaults { get; }

    public int Count => Vaults.Count;
}

/// <summary>One item a deposit took (or would take, in a dry run).</summary>
internal sealed class DepositedView : BatchItemView
{
    internal DepositedView(int index, ThingView item, string kind, SlotRefView? from, double quantity,
        double leftInSource) : base(index, ok: true)
    {
        ReferenceId = item.ReferenceId;
        PrefabName = item.PrefabName;
        DisplayName = item.DisplayName;
        Kind = kind;
        From = from;
        Quantity = VaultAmount.Of(quantity);
        LeftInSource = VaultAmount.Of(leftInSource);
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>ingot or ore.</summary>
    public string Kind { get; }

    /// <summary>The slot it was in; null when it lay in the world.</summary>
    public SlotRefView? From { get; }

    public double Quantity { get; }

    /// <summary>What stays in the source stack; 0 when the whole item went (and was destroyed, as the import does).</summary>
    public double LeftInSource { get; }
}

/// <summary>One item a deposit refused; nothing was changed for it.</summary>
internal sealed class NotDepositedView : BatchItemView
{
    internal NotDepositedView(int index, ThingId? referenceId, string? prefabName, ApiException error)
        : base(index, ok: false)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public string? PrefabName { get; }

    public ErrorView Error { get; }
}

/// <summary>vault_deposit's reply.</summary>
internal sealed class VaultDepositView
{
    internal VaultDepositView(bool dryRun, VaultRefView vault, int? matched, int skipped, bool truncated,
        BatchResultView items, List<StockChangeView> stock)
    {
        DryRun = dryRun;
        Vault = vault.Vault;
        Via = vault.Via;
        Matched = matched;
        Skipped = skipped;
        Truncated = truncated;
        Items = items;
        Stock = stock;
    }

    public bool DryRun { get; }

    public ThingView Vault { get; }

    public ThingView? Via { get; }

    /// <summary>Filter form: how many items the filter named before the vault's rule; null for the id form.</summary>
    public int? Matched { get; }

    /// <summary>Filter form: items left out because the vault does not take them (not ingot, ore or ice).</summary>
    public int Skipped { get; }

    /// <summary>Filter form: more items matched than limit; only the first limit were taken.</summary>
    public bool Truncated { get; }

    public BatchResultView Items { get; }

    public List<StockChangeView> Stock { get; }
}

/// <summary>Where one part of a withdrawal went: merged (onto a stack), slot (a new stack) or ground.</summary>
internal sealed class PlacedView
{
    internal const string Merged = "merged";
    internal const string InSlot = "slot";
    internal const string Ground = "ground";

    internal PlacedView(string where, SlotRefView? slot, double quantity, ThingId? referenceId)
    {
        Where = where;
        Slot = slot;
        Quantity = VaultAmount.Of(quantity);
        ReferenceId = referenceId;
    }

    public string Where { get; }

    /// <summary>The slot it went into; null on the ground.</summary>
    public SlotRefView? Slot { get; }

    public double Quantity { get; }

    /// <summary>The stack it is in now; null in a dry run for a stack not made yet.</summary>
    public ThingId? ReferenceId { get; }
}

/// <summary>vault_withdraw's reply.</summary>
internal sealed class VaultWithdrawView
{
    internal VaultWithdrawView(bool dryRun, VaultRefView vault, ThingView to, double quantity,
        List<PlacedView> placed, List<StockChangeView> stock)
    {
        DryRun = dryRun;
        Vault = vault.Vault;
        Via = vault.Via;
        To = to;
        Quantity = VaultAmount.Of(quantity);
        Placed = placed;
        Stock = stock;
    }

    public bool DryRun { get; }

    public ThingView Vault { get; }

    public ThingView? Via { get; }

    public ThingView To { get; }

    public double Quantity { get; }

    public List<PlacedView> Placed { get; }

    public List<StockChangeView> Stock { get; }
}

/// <summary>One vault's side of a transferred line: what the store held before the entry and after it.</summary>
internal sealed class TransferSideView
{
    internal TransferSideView(double before, double after)
    {
        Before = VaultAmount.Of(before);
        After = VaultAmount.Of(after);
    }

    public double Before { get; }

    public double After { get; }
}

/// <summary>One line a transfer moved (or would move, in a dry run), with both vaults' amounts around it.</summary>
internal sealed class TransferredView : BatchItemView
{
    internal TransferredView(int index, VaultItemView item, double? requested, double quantity, bool partial,
        TransferSideView from, TransferSideView to) : base(index, ok: true)
    {
        Kind = item.Kind;
        PrefabName = item.PrefabName;
        DisplayName = item.DisplayName;
        Reagent = item.Reagent;
        Requested = requested.HasValue ? VaultAmount.Of(requested.Value) : (double?)null;
        Quantity = VaultAmount.Of(quantity);
        Partial = partial;
        From = from;
        To = to;
    }

    public string Kind { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string? Reagent { get; }

    /// <summary>The amount asked for; null when the entry asked for all of it.</summary>
    public double? Requested { get; }

    public double Quantity { get; }

    /// <summary>More was asked than the source held; only what it held moved.</summary>
    public bool Partial { get; }

    public TransferSideView From { get; }

    public TransferSideView To { get; }
}

/// <summary>One entry a transfer refused; nothing was moved for it.</summary>
internal sealed class NotTransferredView : BatchItemView
{
    internal NotTransferredView(int index, string asked, ErrorView error) : base(index, ok: false)
    {
        Asked = asked;
        Error = error;
    }

    /// <summary>The prefab name, prefab hash or reagent as the entry named it.</summary>
    public string Asked { get; }

    public ErrorView Error { get; }
}

/// <summary>vault_transfer's reply.</summary>
internal sealed class VaultTransferView
{
    internal VaultTransferView(bool dryRun, VaultRefView from, VaultRefView to, BatchResultView items, int partialCount)
    {
        DryRun = dryRun;
        FromVault = from.Vault;
        FromVia = from.Via;
        ToVault = to.Vault;
        ToVia = to.Via;
        Items = items;
        PartialCount = partialCount;
    }

    public bool DryRun { get; }

    public ThingView FromVault { get; }

    public ThingView? FromVia { get; }

    public ThingView ToVault { get; }

    public ThingView? ToVia { get; }

    public BatchResultView Items { get; }

    /// <summary>Entries that moved less than they asked for (the source held less).</summary>
    public int PartialCount { get; }
}
