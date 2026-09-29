#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// vault_contents: what each Ingot Vault stores, read from the vault's own store (IngotVaults): ingots as reagent
/// grams, ores and ices as counts, exact rather than the vault screen's one decimal. Needs the IngotVault mod. Read
/// only. find_items and item_totals see the ingots as machine stock of kind processing and do not see ores at all.
/// </summary>
internal static class VaultContentsApi
{
    internal static VaultContentsView Handle(Args args)
    {
        IngotVaults.RequireLoaded();
        ThingId? id = args.OptionalThingId("vault_id");
        List<VaultContentsEntryView> vaults = new List<VaultContentsEntryView>();
        if (id.HasValue)
        {
            vaults.Add(Entry(VaultTarget.Resolve(id.Value).Vault));
        }
        else
        {
            foreach (DeviceImportExport vault in IngotVaults.All())
            {
                vaults.Add(Entry(vault));
            }
        }

        return new VaultContentsView(vaults, Remotes());
    }

    private static VaultContentsEntryView Entry(DeviceImportExport vault)
    {
        VaultStore store = VaultStore.Of(vault);
        List<VaultStock> lines = store.Lines();
        List<VaultStockView> stock = new List<VaultStockView>(lines.Count);
        foreach (VaultStock line in lines)
        {
            stock.Add(new VaultStockView(line.View(), line.AmountIn(store)));
        }

        return new VaultContentsEntryView(GameLookup.ViewOf(vault), GameLookup.ViewOf(vault.Position), vault.OnOff,
            vault.Powered, stock, store.PendingVends);
    }

    private static List<RemoteVaultView> Remotes()
    {
        List<RemoteVaultView> remotes = new List<RemoteVaultView>();
        foreach (DeviceImportExport remote in IngotVaults.AllRemotes())
        {
            DeviceImportExport? vault = IngotVaults.ConnectedTo(remote, out string connection);
            remotes.Add(new RemoteVaultView(GameLookup.ViewOf(remote),
                vault != null ? new ThingId(vault.ReferenceId) : (ThingId?)null, connection));
        }

        return remotes;
    }
}
