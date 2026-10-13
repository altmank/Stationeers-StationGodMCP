#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Objects.Electrical;
using Objects.Items;
using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Batteries charged as the console's 'setbatteries' and 'power chargeall' charge every battery in the world at once
/// (SetBatteriesCommand, PowerCommand: both set IChargable.PowerStored, host only), here on the batteries named.
///
/// What stores power (CODE): IChargable, which the game implements on Battery (Station Battery, Large Station
/// Battery and the rocket batteries) and BatteryCell (every battery cell item, the wireless cell among them; suits,
/// tools, rovers, robots and the APC hold one in a slot), and PowerPylonTerminus, whose own PowerStored is a pylon
/// end's buffer. A Disposable Battery Charger is a consumable whose charge is its stack quantity, so it is refused.
///
/// Sync: Battery.PowerStored's setter raises network flag 512 on a host with clients, which sends the charge
/// (Battery.BuildUpdate); a cell's setter only stores it, and the next power tick (BatteryCell.OnPowerTick) sets
/// CurrentPowerPercentage (flag 256) and the Mode interactable through OnServer.Interact, as charging does. A pylon
/// buffer is the host's alone (never sent). Host only.
/// </summary>
internal static class BatteryCharges
{
    /// <summary>
    /// Each target's charge under goal, in order, or why it is left alone; set only when act. Refused not_host on a
    /// multiplayer client.
    /// </summary>
    internal static List<BatteryResult> Charge(BatteryTargets targets, ChargeGoal goal, bool act)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host sets charge.");
        }

        List<BatteryResult> results = new List<BatteryResult>(targets.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            if (!targets.TryStore(index, out PowerStore? store, out ThingId? id, out ApiException? refusal))
            {
                results.Add(new BatteryResult.Refused(index, id, refusal!));
                continue;
            }

            ChargeChange change = goal.Apply(store!.Charge, store.Capacity);
            if (act)
            {
                store.Charge = change.After;
                change = new ChargeChange(change.Before, store.Charge, change.Capacity, change.Clamped);
            }

            results.Add(new BatteryResult.Charged(index, GameLookup.ViewOf(store.Thing), store.Kind, store.HeldBy,
                change));
        }

        return results;
    }
}

/// <summary>
/// The batteries a call names: ids (each checked alone), a rocket's, or those in a holder's slots ("player" for the
/// player). The rocket and holder forms are refused whole when they hold none, or more than one call sets.
/// </summary>
internal abstract class BatteryTargets
{
    internal const int MaximumTargets = 256;
    internal const int MaximumDepth = 6;

    private BatteryTargets()
    {
    }

    internal abstract int Count { get; }

    internal virtual ThingView? FoundIn => null;

    internal abstract bool TryStore(int index, out PowerStore? store, out ThingId? id, out ApiException? refusal);

    internal static BatteryTargets Named(IReadOnlyList<string> ids, string label) => new NamedTargets(ids, label);

    internal static BatteryTargets OfRocket(string rocketId)
    {
        Rocket rocket = RocketLocator.Find(rocketId);
        ThingView view = new ThingView(new ThingId(rocket.ReferenceId), null, rocket.DisplayName);
        return Found.Of(view, RocketStores(rocket), $"The rocket {rocket.DisplayName} {rocket.ReferenceId} has no battery.");
    }

    internal static BatteryTargets Within(string holderId, string label)
    {
        Thing holder = string.Equals(holderId.Trim(), "player", System.StringComparison.OrdinalIgnoreCase)
            ? PlayerOrigin.RequireHuman()
            : TargetIds.Thing(holderId, label);
        List<PowerStore> held = new List<PowerStore>();
        HeldStores.Collect(holder, MaximumDepth, held);
        return Found.Of(GameLookup.ViewOf(holder), held,
            $"{Names.Of(holder)} {holder.ReferenceId} holds no battery cell in its slots " +
            $"(searched {MaximumDepth} levels down).");
    }

    // The power stores on the rocket's network, as inspect rocket counts its batteries (RocketNetwork.Internals).
    private static List<PowerStore> RocketStores(Rocket rocket)
    {
        List<PowerStore> stores = new List<PowerStore>();
        List<IRocketInternals> parts = rocket.RocketNetwork.Internals;
        for (int index = 0; index < parts.Count; index++)
        {
            if (parts[index] is Thing thing && thing != null && !thing.IsBeingDestroyed &&
                PowerStore.TryOf(thing, out PowerStore? store))
            {
                stores.Add(store!);
            }
        }

        return stores;
    }

    /// <summary>Why a thing is not a battery, naming what it is and where its battery is when it has one.</summary>
    private static string NotABattery(Thing thing)
    {
        string name = $"{Names.Of(thing)} {thing.ReferenceId} ({thing.PrefabName}, {ThingKinds.Of(thing)} " +
                      $"{thing.GetType().Name})";
        if (thing is DisposableBatteryCharger)
        {
            return $"{name} is a consumable charger: its charge is its stack quantity, not a stored charge.";
        }

        List<PowerStore> held = new List<PowerStore>();
        HeldStores.Collect(thing, MaximumDepth, held);
        if (held.Count > 0)
        {
            return $"{name} stores no power itself; its slots hold {held.Count} battery cell(s) (first " +
                $"{held[0].Thing.ReferenceId}): set them with within: \"{thing.ReferenceId}\".";
        }

        return thing is IBatteryPowered
            ? $"{name} runs on a battery cell, but its battery slot is empty."
            : $"{name} stores no power.";
    }

    private sealed class NamedTargets : BatteryTargets
    {
        private readonly IReadOnlyList<string> _ids;
        private readonly string _label;

        internal NamedTargets(IReadOnlyList<string> ids, string label)
        {
            _ids = ids;
            _label = label;
        }

        internal override int Count => _ids.Count;

        internal override bool TryStore(int index, out PowerStore? store, out ThingId? id, out ApiException? refusal)
        {
            store = null;
            id = null;
            try
            {
                id = TargetIds.Of(_ids[index], _label);
            }
            catch (ApiException unread)
            {
                refusal = unread;
                return false;
            }

            if (!GameLookup.TryFindThing(id.Value, out Thing thing) || thing.IsBeingDestroyed)
            {
                refusal = ApiErrors.ThingNotFound(id.Value);
                return false;
            }

            if (!PowerStore.TryOf(thing, out store))
            {
                refusal = ApiErrors.Refused("not_a_battery", NotABattery(thing));
                return false;
            }

            refusal = null;
            return true;
        }
    }

    private sealed class Found : BatteryTargets
    {
        private readonly List<PowerStore> _stores;
        private readonly ThingView _foundIn;

        private Found(ThingView foundIn, List<PowerStore> stores)
        {
            _foundIn = foundIn;
            _stores = stores;
        }

        internal static Found Of(ThingView foundIn, List<PowerStore> stores, string none)
        {
            if (stores.Count == 0)
            {
                throw ApiErrors.Refused("no_batteries", none);
            }

            if (stores.Count > MaximumTargets)
            {
                throw ApiErrors.Refused("too_many_batteries",
                    $"{foundIn.DisplayName} {foundIn.ReferenceId} holds {stores.Count} batteries, over the " +
                    $"{MaximumTargets} one call sets; name them by id, up to {MaximumTargets} a call. Nothing was " +
                    "changed.");
            }

            return new Found(foundIn, stores);
        }

        internal override int Count => _stores.Count;

        internal override ThingView? FoundIn => _foundIn;

        internal override bool TryStore(int index, out PowerStore? store, out ThingId? id, out ApiException? refusal)
        {
            store = _stores[index];
            id = new ThingId(store.Thing.ReferenceId);
            refusal = null;
            return true;
        }
    }
}

/// <summary>The power stores in a thing's slots, following occupants' own slots (organ slots left out).</summary>
internal static class HeldStores
{
    internal static void Collect(Thing holder, int depth, List<PowerStore> into)
    {
        if (holder.Slots == null || depth <= 0)
        {
            return;
        }

        for (int index = 0; index < holder.Slots.Count; index++)
        {
            Slot slot = holder.Slots[index];
            if (slot == null || slot.Type == Slot.Class.Organ)
            {
                continue;
            }

            DynamicThing occupant = slot.Get();
            if (occupant == null || occupant.IsBeingDestroyed)
            {
                continue;
            }

            if (PowerStore.TryOf(occupant, out PowerStore? store))
            {
                into.Add(store!);
            }

            Collect(occupant, depth - 1, into);
        }
    }
}

/// <summary>
/// A thing that stores power, read and written through the property the game charges and drains it by. The
/// setters clamp to 0..capacity themselves.
/// </summary>
internal sealed class PowerStore
{
    private readonly IChargable? _chargeable;
    private readonly PowerPylonTerminus? _pylon;

    private PowerStore(Thing thing, PowerStoreKind kind, IChargable? chargeable, PowerPylonTerminus? pylon)
    {
        Thing = thing;
        Kind = kind;
        _chargeable = chargeable;
        _pylon = pylon;
    }

    internal Thing Thing { get; }

    internal PowerStoreKind Kind { get; }

    internal double Capacity => _chargeable != null ? _chargeable.GetPowerMaximum() : _pylon!.PowerMaximum;

    internal double Charge
    {
        get => _chargeable != null ? _chargeable.PowerStored : _pylon!.PowerStored;
        set
        {
            if (_chargeable != null)
            {
                _chargeable.PowerStored = (float)value;
            }
            else
            {
                _pylon!.PowerStored = (float)value;
            }
        }
    }

    /// <summary>The thing whose slot holds this store (a cell in a suit, tool, APC or charger); null otherwise.</summary>
    internal ThingView? HeldBy =>
        Thing is DynamicThing held && held.ParentSlot != null && held.ParentSlot.Parent != null
            ? GameLookup.ViewOf(held.ParentSlot.Parent)
            : null;

    internal static bool TryOf(Thing thing, out PowerStore? store)
    {
        store = thing switch
        {
            Battery battery => new PowerStore(thing,
                battery.InternalCellType != RocketInternalCellType.None || battery.RocketNetwork != null
                    ? PowerStoreKind.RocketBattery
                    : PowerStoreKind.StationBattery, battery, null),
            BatteryCell cell => new PowerStore(thing, PowerStoreKind.BatteryCell, cell, null),
            IChargable chargeable => new PowerStore(thing, PowerStoreKind.OtherChargeable, chargeable, null),
            PowerPylonTerminus pylon => new PowerStore(thing, PowerStoreKind.PylonBuffer, null, pylon),
            _ => null
        };
        return store != null;
    }
}
