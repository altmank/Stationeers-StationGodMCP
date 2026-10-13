#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Items;
using Reagents;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api;

/// <summary>
/// vault_deposit: put ingots, ores and ices straight into an Ingot Vault's store, from wherever they are (a player at
/// any depth, a container, the ground), as a move: what leaves the source is what the store gains, nothing is made or
/// lost, and no slot, chute or door is involved. Writes; host only; needs IngotVault.
///
/// The vault's own import (CODE, IngotVault 1.2.0): an item reaches the Import slot only when the slot's class is None
/// or the item's SlotType (DeviceImport.TryChuteImport), is kept only when it is an Ingot or an Ore (Ice is an Ore;
/// OnChildEnterInventory), and otherwise is ejected. CollectResource then adds an ingot's CreatedReagentMixture times
/// its Quantity to the vault's ReagentMixture, or an ore's stack Quantity to _storedOresAndIces[prefab hash] (flagging
/// NetworkUpdateFlags 0x100), destroys the item (OnServer.Destroy) and refreshes the display. This does the same
/// bookkeeping with the same calls, and for part of a stack takes the part off the source the way the trade window
/// trims a stack (RemoveQuantity; Consumable.Quantity for an ingot's grams) instead of destroying it.
/// </summary>
internal static class VaultDepositApi
{
    internal const int MaximumItems = 256;
    internal const int MaximumLimit = 1000;

    internal static VaultDepositView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        IngotVaults.RequireLoaded();
        bool dryRun = WriteMode.IsDryRun(args);
        VaultTarget target = VaultTarget.Resolve(args.ThingId("vault_id"));
        DepositRequest request = DepositRequest.Parse(args);
        target.RequirePowered();
        VaultStore store = target.Store();
        VaultLedger ledger = new VaultLedger(store);
        BatchBuilder batch = new BatchBuilder(request.Entries.Count);
        HashSet<long> seen = new HashSet<long>();
        List<DepositPlan> plans = new List<DepositPlan>(request.Entries.Count);
        for (int index = 0; index < request.Entries.Count; index++)
        {
            DepositEntry entry = request.Entries[index];
            if (!DepositPlan.TryPlan(entry, target, seen, out DepositPlan? plan, out ApiException? refusal))
            {
                batch.Failed(new NotDepositedView(index, entry.Id, entry.PrefabName, refusal!));
                continue;
            }

            plan!.Record(ledger);
            plans.Add(plan);
            batch.Succeeded(plan.View(index));
        }

        if (!dryRun && plans.Count > 0)
        {
            foreach (DepositPlan plan in plans)
            {
                plan.Apply(store);
            }

            store.Refresh();
        }

        return new VaultDepositView(dryRun, target.View(), request.Matched, request.Skipped, request.Truncated,
            batch.Build(), ledger.Views(readBack: !dryRun));
    }
}

/// <summary>The three forms: items [{reference_id, quantity}], reference_ids [...], or a filter over every item.</summary>
internal sealed class DepositRequest
{
    private const int DefaultLimit = ReplyDefaults.VaultDepositItems;

    private static readonly string[] FilterArguments =
        { "prefab_contains", "name_contains", "location", "within_id", "near_player_m", "kind", "limit" };

    private DepositRequest(List<DepositEntry> entries, int? matched, int skipped, bool truncated)
    {
        Entries = entries;
        Matched = matched;
        Skipped = skipped;
        Truncated = truncated;
    }

    internal List<DepositEntry> Entries { get; }

    internal int? Matched { get; }

    internal int Skipped { get; }

    internal bool Truncated { get; }

    internal static DepositRequest Parse(Args args)
    {
        if (args.Has("items"))
        {
            args.Reject("items", "reference_ids");
            args.Reject("items", FilterArguments);
            return new DepositRequest(Items(args), null, 0, false);
        }

        if (args.Has("reference_ids"))
        {
            args.Reject("{arg:reference_ids}", FilterArguments);
            List<DepositEntry> entries = new List<DepositEntry>();
            foreach (ThingId id in args.ThingIds("reference_ids", VaultDepositApi.MaximumItems))
            {
                entries.Add(new DepositEntry(id, null));
            }

            return new DepositRequest(entries, null, 0, false);
        }

        return Filtered(args);
    }

    private static List<DepositEntry> Items(Args args)
    {
        List<Args?> items = args.Objects("items", VaultDepositApi.MaximumItems);
        List<DepositEntry> entries = new List<DepositEntry>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            Args item = items[index] ?? throw ApiErrors.InvalidArgument($"items[{index}] must be an object.");
            entries.Add(new DepositEntry(item.ThingId("reference_id"), item.OptionalPositiveDouble("quantity")));
        }

        return entries;
    }

    // Every item the filter keeps that the vault takes, nearest the player first, up to limit.
    private static DepositRequest Filtered(Args args)
    {
        bool narrowed = false;
        foreach (string name in FilterArguments)
        {
            narrowed |= name != "limit" && args.Has(name);
        }

        if (!narrowed)
        {
            throw ApiErrors.InvalidArgument(
                Vocabulary.Text("Name the items: {arg:items}, {arg:reference_ids}, or a filter " +
                    "({arg:prefab_contains}, {arg:name_contains}, {arg:location}, {arg:within_id}, " +
                    "{arg:near_player_m}, {arg:kind})."));
        }

        if (args.IsWord("location", "machine_stock"))
        {
            throw ApiErrors.InvalidArgument("Machine stock is not an item; location must be any, ground, player or stored.");
        }

        VaultMaterial kind = VaultMaterial.Of(args.OptionalString("kind") ?? "any") ??
                             throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:kind}' must be any, " +
                                 "ingot, ore or ice."));
        int limit = args.OptionalInt("limit", 1, VaultDepositApi.MaximumLimit) ?? DefaultLimit;
        DepositPicks picks = DepositPicks.Nearest(ItemFilter.Parse(args), kind, limit);
        Truncations.Capped("items.results", picks.Entries.Count, picks.Taken, "limit", VaultDepositApi.MaximumLimit);
        return new DepositRequest(picks.Entries, picks.Matched, picks.Skipped, picks.Truncated);
    }
}
