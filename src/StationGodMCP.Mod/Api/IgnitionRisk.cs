#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// ignition_risk: whether the things the local player carries would catch fire in the air they are in. Read only.
///
/// The game's rule (CODE Thing.ShouldIgnite, DynamicThing.OnFireTick, both run per fire tick): see IgnitionRule. Each
/// carried thing is judged on its own DynamicThing.WorldAtmosphere, the air its fire tick reads: a slot marked
/// UseInternalAtmosphere hands it its holder's own air (a suit), otherwise it takes its holder's, down to the cell the
/// player stands in. ignites_now calls the game's own ShouldIgnite on that air after OnFireTick's guards. A burning
/// thing keeps burning while its cell holds more than 0.3 mol of oxygen (Thing.OnFireConsume: each fire tick it turns
/// 0.3 mol of O2 into CO2 and heat) or while ShouldIgnite still holds.
///
/// Ignition temperatures are per prefab (Thing.flashpointTemperature, autoignitionTemperature; -1 when unset, and a
/// thing with neither never burns: DynamicThing.IsBurnable). include_prefabs lists every prefab that has one.
/// </summary>
internal static class IgnitionRiskApi
{
    private const string WorldAir = "world";
    private const string InternalAir = "internal";
    private const string NoAir = "none";

    internal static IgnitionRiskView Handle(Args args)
    {
        bool includePrefabs = args.OptionalBool("include_prefabs") ?? false;
        Human? human = Human.LocalHuman;
        List<CarriedIgnitionView> items = new List<CarriedIgnitionView>();
        IgnitionCellView? cell = null;
        if (human != null)
        {
            cell = CellOf(human);
            Walk(human, human, items);
        }

        return new IgnitionRiskView(PlayerOrigin.Current().View, cell, items, includePrefabs ? Prefabs() : null);
    }

    private static IgnitionCellView? CellOf(Human human)
    {
        AtmosphericsController air = AtmosphericsController.World;
        if (air == null || human.WorldGrid == WorldGrid.INVALID)
        {
            return null;
        }

        Atmosphere atmosphere = air.SampleGlobalAtmosphere(human.WorldGrid);
        if (atmosphere == null)
        {
            return null;
        }

        RoomController rooms = RoomController.World;
        bool inRoom = rooms != null && rooms.GetRoom(human.WorldGrid.Value) != null;
        return new IgnitionCellView(atmosphere.Temperature.ToDouble(), atmosphere.PressureGasses.ToDouble(),
            atmosphere.GasMixture.TotalEnergy.ToDouble(), atmosphere.Inflamed,
            atmosphere.GasMixture.Oxygen.Quantity.ToDouble(), inRoom);
    }

    // Every slot at every depth, from the player outwards; only burnable things are listed, but all are walked.
    private static void Walk(Thing holder, Human human, List<CarriedIgnitionView> items)
    {
        foreach (Slot slot in holder.Slots)
        {
            if (slot == null)
            {
                continue;
            }

            DynamicThing occupant = slot.Get();
            if (occupant == null || occupant.IsBeingDestroyed)
            {
                continue;
            }

            if (occupant.IsBurnable)
            {
                bool inHand = ReferenceEquals(slot, human.LeftHandSlot) || ReferenceEquals(slot, human.RightHandSlot);
                items.Add(ViewOf(occupant, slot, holder, inHand));
            }

            Walk(occupant, human, items);
        }
    }

    private static CarriedIgnitionView ViewOf(DynamicThing thing, Slot slot, Thing holder, bool inHand)
    {
        Atmosphere atmosphere = thing.WorldAtmosphere;
        bool hidden = thing.IsHiddenInParentSlot();
        double? flashpoint = Positive(thing.FlashPointTemperature.ToDouble());
        double? autoignition = Positive(thing.AutoignitionTemperature.ToDouble());
        double? effective = atmosphere == null
            ? null
            : IgnitionRule.EffectiveFlashpointK(flashpoint, atmosphere.RatioOneAtmosphereClamped());
        bool ignites = atmosphere != null
            && IgnitionRule.FireTickChecks(thing.IsBurnable, hidden, atmosphere.PressureGasses.ToDouble())
            && thing.ShouldIgnite(atmosphere);
        string source = atmosphere == null ? NoAir : slot.UseInternalAtmosphere ? InternalAir : WorldAir;
        return new CarriedIgnitionView(GameLookup.ViewOf(thing),
            new CarriedPlaceView(SlotName(slot), inHand, GameLookup.ViewOf(holder), source),
            new IgnitionTemperaturesView(flashpoint, effective, autoignition),
            new IgnitionStateView(thing.IsBurning, hidden, ignites, HealthOf(thing)));
    }

    private static string? SlotName(Slot slot)
    {
        string name = slot.DisplayName;
        return string.IsNullOrEmpty(name) ? slot.StringKey : name;
    }

    private static double? HealthOf(Thing thing)
    {
        IndestructableDamageState damage = thing.DamageState;
        if (damage == null || damage.Indestructable || !(damage.MaxDamage > 0f))
        {
            return null;
        }

        return 1.0 - damage.TotalRatio;
    }

    private static double? Positive(double kelvin) => kelvin > 0.0 ? kelvin : null;

    private static List<PrefabIgnitionView> Prefabs()
    {
        List<PrefabIgnitionView> views = new List<PrefabIgnitionView>();
        foreach (Thing prefab in new List<Thing>(Prefab.AllPrefabs))
        {
            if (prefab == null)
            {
                continue;
            }

            double? flashpoint = Positive(prefab.FlashPointTemperature.ToDouble());
            double? autoignition = Positive(prefab.AutoignitionTemperature.ToDouble());
            if (flashpoint.HasValue || autoignition.HasValue)
            {
                views.Add(new PrefabIgnitionView(prefab.PrefabName, Text.Plain(prefab.DisplayName), flashpoint,
                    autoignition));
            }
        }

        views.Sort(static (a, b) => string.CompareOrdinal(a.PrefabName, b.PrefabName));
        return views;
    }
}
