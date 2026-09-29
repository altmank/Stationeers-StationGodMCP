#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Items;
using Reagents;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The IngotVault Workshop mod (IngotVault.dll, types StructureIngotVault and StructureRemoteVault), reached by
/// reflection so StationGod runs with or without it. What the vault keeps (CODE, IngotVault 1.2.0):
///
/// ingots: the vault's own Thing.ReagentMixture. Import adds the ingot's CreatedReagentMixture times its Quantity
/// (CollectResource); a vend Sets the reagent to what is left (TryVendSelected) and makes the ingot
/// GetPrefabHashForReagent names (the first Ingot.AllIngotPrefabs entry whose CreatedReagentMixture holds the reagent)
/// with Quantity = the amount (1 reagent unit = 1 g of ingot). Only SupportedReagents (reagents an ingot is made of)
/// are listed by the vault.
///
/// ores and ices: the public Dictionary&lt;int, double&gt; _storedOresAndIces, prefab hash to count. Import adds the
/// stack's Quantity; a vend removes the entry when 0.01 or less is left. Both flag NetworkUpdateFlags 0x100, which
/// sends the dictionary to clients.
///
/// A Remote Vault keeps nothing: it reads and writes the one StructureIngotVault on its data network
/// (FindConnectedVault; none or more than one is a connection error), so a remote id resolves to that vault.
/// </summary>
internal static class IngotVaults
{
    internal const ushort OresChangedFlag = 0x100;

    private static readonly GameMember[] Needed =
    {
        GameMembers.VaultAll, GameMembers.VaultSupportedReagents, GameMembers.VaultOres,
        GameMembers.VaultPendingVends, GameMembers.VaultShowContents, GameMembers.VaultIngotHashOf
    };

    private static readonly GameMember[] NeededForRemotes =
    {
        GameMembers.RemoteVaultAll, GameMembers.RemoteVaultConnected, GameMembers.RemoteVaultConnectionError
    };

    /// <summary>Refuses unless IngotVault is loaded and still has every member these tools use.</summary>
    internal static void RequireLoaded()
    {
        if (GameMembers.VaultType.OrNull == null)
        {
            throw ApiErrors.Refused("ingot_vault_mod_required",
                "The vault tools need the IngotVault mod (Steam Workshop 3749011679), which is not loaded.");
        }

        RequireMembers(Needed);
    }

    private static void RequireMembers(GameMember[] members)
    {
        List<string> missing = new List<string>();
        foreach (GameMember member in members)
        {
            if (!member.TryResolve())
            {
                missing.Add(member.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw ApiErrors.Refused("ingot_vault_changed",
                $"The IngotVault mod has changed: {string.Join(", ", missing)} is gone. This StationGod build needs " +
                "an update for this IngotVault version; nothing was changed.");
        }
    }

    internal static bool IsVault(Thing thing) => IsA(thing, GameMembers.VaultType.Name);

    internal static bool IsRemote(Thing thing) => IsA(thing, GameMembers.RemoteVaultType.Name);

    // By the type's full name up the hierarchy, so move_item's check costs no assembly search when IngotVault is absent.
    private static bool IsA(Thing thing, string fullName)
    {
        for (Type? type = thing.GetType(); type != null; type = type.BaseType)
        {
            if (type.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    internal static List<DeviceImportExport> All() => Devices(GameMembers.VaultAll);

    /// <summary>Every Remote Vault; empty when the loaded IngotVault has none, or lacks what reading them needs.</summary>
    internal static List<DeviceImportExport> AllRemotes()
    {
        foreach (GameMember member in NeededForRemotes)
        {
            if (!member.TryResolve())
            {
                return new List<DeviceImportExport>();
            }
        }

        return Devices(GameMembers.RemoteVaultAll);
    }

    private static List<DeviceImportExport> Devices(GameField list)
    {
        List<DeviceImportExport> devices = new List<DeviceImportExport>();
        if (list.GetValue(null) is IList all)
        {
            foreach (object entry in all)
            {
                if (entry is DeviceImportExport device && device != null && !device.IsBeingDestroyed)
                {
                    devices.Add(device);
                }
            }
        }

        return devices;
    }

    /// <summary>The vault a Remote Vault is linked to, or null with the vault's own connection error.</summary>
    internal static DeviceImportExport? ConnectedTo(DeviceImportExport remote, out string connection)
    {
        RequireMembers(NeededForRemotes);
        DeviceImportExport? vault = GameMembers.RemoteVaultConnected.Invoke(remote) as DeviceImportExport;
        connection = vault != null
            ? "None"
            : GameMembers.RemoteVaultConnectionError.Invoke(remote)?.ToString() ?? "NoVaultDetected";
        return vault;
    }

    /// <summary>The ingot IngotVault makes of a reagent (its own GetPrefabHashForReagent), or null for none.</summary>
    internal static Ingot? IngotOf(Reagent reagent)
    {
        int hash = (int)GameMembers.VaultIngotHashOf.Invoke(null, reagent);
        return hash != 0 ? Prefab.Find(hash) as Ingot : null;
    }

    internal static List<Reagent> SupportedReagents()
    {
        List<Reagent> reagents = new List<Reagent>();
        if (GameMembers.VaultSupportedReagents.GetValue(null) is IList all)
        {
            foreach (object entry in all)
            {
                if (entry is Reagent reagent)
                {
                    reagents.Add(reagent);
                }
            }
        }

        return reagents;
    }
}

/// <summary>A vault as a request names it: the vault itself, and the Remote Vault it was reached through, if any.</summary>
internal sealed class VaultTarget
{
    private VaultTarget(DeviceImportExport vault, DeviceImportExport? via)
    {
        Vault = vault;
        Via = via;
    }

    internal DeviceImportExport Vault { get; }

    internal DeviceImportExport? Via { get; }

    internal static VaultTarget Resolve(ThingId id)
    {
        Thing thing = GameLookup.RequireThing(id);
        if (thing.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(id);
        }

        if (IngotVaults.IsVault(thing))
        {
            return new VaultTarget((DeviceImportExport)thing, null);
        }

        if (!IngotVaults.IsRemote(thing))
        {
            throw ApiErrors.Refused("not_a_vault",
                $"{thing.DisplayName} ({id}) is not an Ingot Vault or a Remote Vault.");
        }

        DeviceImportExport remote = (DeviceImportExport)thing;
        DeviceImportExport? vault = IngotVaults.ConnectedTo(remote, out string connection);
        return vault != null
            ? new VaultTarget(vault, remote)
            : throw ApiErrors.Refused("vault_not_connected",
                $"Remote Vault {id} reaches no vault ({connection}): it needs exactly one Ingot Vault on its data " +
                "network.");
    }

    /// <summary>The vault's own UI refuses to vend while it is off or unpowered, and it imports nothing then.</summary>
    internal void RequirePowered()
    {
        if (!Vault.OnOff || !Vault.Powered)
        {
            throw ApiErrors.Refused("vault_unpowered",
                $"{Vault.DisplayName} ({Vault.ReferenceId}) is {(Vault.OnOff ? "unpowered" : "off")}; the vault " +
                "neither imports nor vends then. Nothing was changed.");
        }
    }

    internal VaultStore Store() => VaultStore.Of(Vault);

    internal VaultRefView View() =>
        new VaultRefView(GameLookup.ViewOf(Vault), Via != null ? GameLookup.ViewOf(Via) : null,
            Vault.OnOff, Vault.Powered);
}

/// <summary>One vault's store: its ingot reagents and its ore and ice counts, read and written as the vault does.</summary>
internal sealed class VaultStore
{
    /// <summary>Amounts at or below this are rounding left in the store (ReagentsApi's rule).</summary>
    internal const double Trace = 1e-6;

    /// <summary>A vend removes an ore entry when this much or less is left (TryVendSelected).</summary>
    internal const double OreRemovalThreshold = 0.01;

    private VaultStore(DeviceImportExport vault, ReagentMixture ingots, Dictionary<int, double> ores)
    {
        Vault = vault;
        Ingots = ingots;
        Ores = ores;
    }

    internal DeviceImportExport Vault { get; }

    internal ReagentMixture Ingots { get; }

    internal Dictionary<int, double> Ores { get; }

    internal static VaultStore Of(DeviceImportExport vault)
    {
        // The vault makes its ReagentMixture in Awake; a vault without one has never been live.
        ReagentMixture ingots = vault.ReagentMixture ??
                                throw ApiErrors.Refused("vault_not_ready", $"{vault.DisplayName} has no store yet.");
        Dictionary<int, double> ores = GameMembers.VaultOres.GetValue(vault) as Dictionary<int, double> ??
                                       throw new GameChangedException(GameMembers.VaultOres.Name);
        return new VaultStore(vault, ingots, ores);
    }

    /// <summary>Everything stored, ingots in the vault's reagent order, then ores and ices in its dictionary order.</summary>
    internal List<VaultStock> Lines()
    {
        List<VaultStock> lines = new List<VaultStock>();
        foreach (Reagent reagent in IngotVaults.SupportedReagents())
        {
            if (Ingots.Get(reagent) > Trace)
            {
                lines.Add(new VaultStock.IngotStock(reagent));
            }
        }

        foreach (KeyValuePair<int, double> ore in Ores)
        {
            if (ore.Value > Trace)
            {
                lines.Add(new VaultStock.OreStock(ore.Key));
            }
        }

        return lines;
    }

    internal int PendingVends =>
        GameMembers.VaultPendingVends.GetValue(Vault) is ICollection pending ? pending.Count : 0;

    /// <summary>What the vault shows after its own imports and vends: display, slots and selected item.</summary>
    internal void Refresh() => GameMembers.VaultShowContents.Invoke(Vault);

    internal void FlagOresChanged() => Vault.NetworkUpdateFlags |= IngotVaults.OresChangedFlag;
}

/// <summary>One kind of thing a vault stores, with the vault's own way of reading, adding and taking it.</summary>
internal abstract class VaultStock
{
    private VaultStock()
    {
    }

    /// <summary>ingot or ore (ores and ices share the vault's one dictionary).</summary>
    internal abstract string Kind { get; }

    /// <summary>A key unique per stock kind within one vault.</summary>
    internal abstract string Key { get; }

    /// <summary>The item a vend makes of it.</summary>
    internal abstract int PrefabHash { get; }

    /// <summary>Whether it counts in whole items (a stack's count) rather than grams.</summary>
    internal abstract bool CountsWhole { get; }

    internal abstract double AmountIn(VaultStore store);

    internal abstract void Add(VaultStore store, double amount);

    internal abstract void Take(VaultStore store, double amount);

    internal abstract VaultItemView View();

    internal Item? ItemPrefab => Prefab.Find(PrefabHash) as Item;

    internal sealed class IngotStock : VaultStock
    {
        private readonly int _prefabHash;

        internal IngotStock(Reagent reagent)
        {
            Reagent = reagent;
            Ingot? ingot = IngotVaults.IngotOf(reagent);
            _prefabHash = ingot != null ? ingot.PrefabHash : 0;
        }

        internal Reagent Reagent { get; }

        internal override string Kind => "ingot";

        internal override string Key => "reagent:" + Reagent.TypeName;

        internal override int PrefabHash => _prefabHash;

        internal override bool CountsWhole => false;

        internal override double AmountIn(VaultStore store) => store.Ingots.Get(Reagent);

        // CollectResource adds CreatedReagentMixture * Quantity; callers pass the ingot's whole mixture through
        // VaultDeposit, so this is only for putting back an undelivered withdrawal.
        internal override void Add(VaultStore store, double amount) =>
            store.Ingots.Set(Reagent.ReagentId, store.Ingots.Get(Reagent) + amount);

        // TryVendSelected: ReagentMixture.Set(reagent, value - amount); never below 0 (a request may name the whole
        // stock, which can differ from the float an ingot held by rounding).
        internal override void Take(VaultStore store, double amount) =>
            store.Ingots.Set(Reagent.ReagentId, Math.Max(0.0, store.Ingots.Get(Reagent) - amount));

        internal override VaultItemView View()
        {
            Item? prefab = ItemPrefab;
            return new VaultItemView(Kind, prefab != null ? prefab.PrefabName : null,
                prefab != null ? prefab.DisplayName : Reagent.DisplayName, Reagent.TypeName,
                prefab is Consumable consumable ? consumable.MaxQuantity : (double?)null);
        }
    }

    internal sealed class OreStock : VaultStock
    {
        internal OreStock(int prefabHash)
        {
            PrefabHash = prefabHash;
        }

        internal override string Kind => "ore";

        internal override string Key => "prefab:" + PrefabHash;

        internal override int PrefabHash { get; }

        internal override bool CountsWhole => true;

        internal override double AmountIn(VaultStore store) =>
            store.Ores.TryGetValue(PrefabHash, out double amount) ? amount : 0.0;

        // CollectResource: the entry plus the stack's Quantity, or a new entry.
        internal override void Add(VaultStore store, double amount)
        {
            store.Ores[PrefabHash] = AmountIn(store) + amount;
            store.FlagOresChanged();
        }

        // TryVendSelected: the entry removed when 0.01 or less is left.
        internal override void Take(VaultStore store, double amount)
        {
            double left = AmountIn(store) - amount;
            if (left <= VaultStore.OreRemovalThreshold)
            {
                store.Ores.Remove(PrefabHash);
            }
            else
            {
                store.Ores[PrefabHash] = left;
            }

            store.FlagOresChanged();
        }

        internal override VaultItemView View()
        {
            Item? prefab = ItemPrefab;
            return new VaultItemView(Kind, prefab != null ? prefab.PrefabName : null,
                prefab != null ? prefab.DisplayName : $"unknown prefab {PrefabHash}", null,
                prefab is Stackable stackable ? stackable.MaxQuantity : (double?)null);
        }
    }
}

/// <summary>
/// Each stock line a request touches: the amount before it, and the change. A real run reads the after amounts back
/// from the vault; a dry run reports before + change.
/// </summary>
internal sealed class VaultLedger
{
    private readonly VaultStore _store;
    private readonly List<VaultStock> _stocks = new List<VaultStock>();
    private readonly List<double> _before = new List<double>();
    private readonly List<double> _change = new List<double>();

    internal VaultLedger(VaultStore store)
    {
        _store = store;
    }

    internal void Record(VaultStock stock, double change)
    {
        int at = _stocks.FindIndex(known => known.Key == stock.Key);
        if (at < 0)
        {
            _stocks.Add(stock);
            _before.Add(stock.AmountIn(_store));
            _change.Add(change);
            return;
        }

        _change[at] += change;
    }

    /// <summary>What the stock line holds after the changes recorded so far (a dry run's after).</summary>
    internal double Projected(VaultStock stock)
    {
        int at = _stocks.FindIndex(known => known.Key == stock.Key);
        return at < 0 ? stock.AmountIn(_store) : _before[at] + _change[at];
    }

    internal List<StockChangeView> Views(bool readBack)
    {
        List<StockChangeView> views = new List<StockChangeView>(_stocks.Count);
        for (int index = 0; index < _stocks.Count; index++)
        {
            double after = readBack ? _stocks[index].AmountIn(_store) : _before[index] + _change[index];
            views.Add(new StockChangeView(_stocks[index].View(), _before[index], _change[index], after));
        }

        return views;
    }
}
