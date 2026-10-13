#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// silo_deposit: put things straight into an SDB Silo's store from wherever they are (a player at any depth, a
/// container, the ground), each as one entry made as the silo's import makes it (SiloSaver: the thing's save data and
/// everything it holds, then the things destroyed), at the back of the store. No chute, import slot or door is
/// involved. For part of a stack the part is stored and taken off the source stack. Writes; host only.
///
/// What the silo takes (CODE): a thing reaches the import slot from a chute (DeviceImport.TryChuteImport, which takes
/// any occupant) or from the input trigger, which takes an Item whose SlotType is the import slot's class, or any Item
/// when that class is None (DeviceImport.OnInputTriggerEnter, DeviceImport.cs:381); this applies the trigger's rule.
/// The silo refuses an import when it holds 600 entries (CanBeginImport, Silo.cs:77); this counts an import the silo
/// is saving as taken.
/// </summary>
internal static class SiloDepositApi
{
    internal static SiloDepositView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        bool dryRun = WriteMode.IsDryRun(args);
        SiloStore store = SiloStore.Of(Silos.Resolve(args.ThingId("silo_id")));
        SiloDepositForm form = SiloDepositForm.Of(args);
        bool allowInUse = args.OptionalBool("allow_in_use") ?? false;
        store.RequirePowered();
        SiloDepositList list = ListOf(form, args, store.Silo, allowInUse);
        int room = SiloRules.Room(store.Entries.Count, store.Importing);
        BatchBuilder batch = new BatchBuilder(list.Picks.Count);
        List<SiloDepositPlan> plans = new List<SiloDepositPlan>(list.Picks.Count);
        HashSet<long> named = new HashSet<long>();
        for (int index = 0; index < list.Picks.Count; index++)
        {
            SiloDepositPick pick = list.Picks[index];
            ApiException? refusal = SiloDepositPlan.TryPlan(pick, store.Silo, named, plans, allowInUse,
                out SiloDepositPlan? plan);
            if (refusal == null && plans.Count >= room)
            {
                refusal = ApiErrors.Refused("silo_full",
                    $"{Names.Of(store.Silo)} holds {store.Entries.Count} of {SiloRules.Capacity} entries" +
                    (store.Importing ? " and is saving an import" : string.Empty) +
                    $"; {room} more fit, and this deposit already takes them.");
            }

            if (refusal != null)
            {
                batch.Failed(new NotDepositedView(index, pick.Id, list.PrefabOf(index), refusal));
                continue;
            }

            batch.Succeeded(plan!.View(index, store.Entries.Count + plans.Count));
            plans.Add(plan);
        }

        int before = store.Entries.Count;
        if (!dryRun && plans.Count > 0)
        {
            Store(store, plans);
        }

        int after = dryRun ? before + plans.Count : SiloStore.Of(store.Silo).Entries.Count;
        return new SiloDepositView(dryRun, store.View(), list.Matched, list.Skipped, list.Truncated, batch.Build(),
            new SiloCountView(before, after));
    }

    private static SiloDepositList ListOf(SiloDepositForm form, Args args, Silo silo, bool allowInUse)
    {
        if (form is SiloDepositForm.ById ids)
        {
            return SiloDepositList.ById(ids.Picks);
        }

        SiloDepositList list = SiloDepositList.Nearest(ItemFilter.Parse(args), ((SiloDepositForm.ByFilter)form).Limit,
            silo, allowInUse);
        SiloDepositForm.NoteCut(list.Picks.Count, list.Taken);
        return list;
    }

    // Each plan becomes an entry at the back; the store is written even when a game call fails part way, so every
    // thing already destroyed is in it.
    private static void Store(SiloStore store, List<SiloDepositPlan> plans)
    {
        List<StoredThings> entries = new List<StoredThings>(store.Entries);
        try
        {
            foreach (SiloDepositPlan plan in plans)
            {
                entries.Add(plan.Apply());
            }
        }
        finally
        {
            store.Replace(entries);
        }
    }
}
