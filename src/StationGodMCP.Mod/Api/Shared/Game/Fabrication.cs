#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using DLC;
using Reagents;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// write fabricator: a recipe's item made at once on a SimpleFabricatorBase (Autolathe, Tool Manufactory, Electronics
/// Printer, Pipe Bender...), by the finishing block of the game's own make (SimpleFabricatorBase.WaitThenMake): the
/// recipe subtracted from the stock at the build state's material multiplier, then the item created in the export slot
/// (SpawnCreatedItems) once per output multiplier. The recipe's time and energy are skipped. Ingots may be loaded into the
/// stock first as the import does (FabricatorBase.CollectResource). A cheat, host only; every check runs first, so a
/// refused call loads and makes nothing. Main thread only.
/// </summary>
internal static class Fabrication
{
    /// <summary>The checked call; every refusal is thrown here, before anything changes.</summary>
    internal static FabricationPlan Check(FabricateOrder order, string label, string id)
    {
        HostOnly.Require();

        Thing thing = TargetIds.Thing(id, label);
        if (!(thing is SimpleFabricatorBase fab) || fab == null)
        {
            throw ApiErrors.Refused("not_a_fabricator",
                $"{Names.Of(thing)} ({thing.PrefabName}) is not a fabricator of the printer family (Autolathe, " +
                "Tool Manufactory, Electronics Printer, Pipe Bender...); find device lists them.");
        }

        List<FabricationLoad> loads = new List<FabricationLoad>(order.Loads.Count);
        foreach (LoadEntry entry in order.Loads)
        {
            loads.Add(ResolveLoad(fab, entry));
        }

        DynamicThing? product = null;
        Recipe recipe = default;
        if (order.Make)
        {
            (product, recipe) = ResolveRecipe(fab, order.Prefab!);
        }

        string? busy = FabricateRule.BusyReason(fab.Activate == 1, fab.IsOpen, OccupantId(fab));
        if (busy != null)
        {
            throw Busy(fab, busy);
        }

        if (order.Make)
        {
            string? power = FabricateRule.PowerReason(fab.IsStructureCompleted,
                fab.CurrentBuildState != null && fab.CurrentBuildState.CanManufacture, fab.OnOff, fab.Powered,
                fab.Error);
            if (power != null)
            {
                throw ApiErrors.Refused("fabricator_unpowered",
                    $"{Names.Of(fab)} cannot make now ({power}). Nothing ran.", new Dictionary<string, object?> { ["reason"] = power });
            }
        }

        ReagentMixture stock = new ReagentMixture(fab.ReagentMixture);
        foreach (FabricationLoad load in loads)
        {
            stock.Add(load.Ingot.CreatedReagentMixture * load.Quantity);
        }

        if (order.Make && !stock.Contains(recipe))
        {
            Dictionary<string, double> missing = PrefabRecipes.Inputs(recipe.GetMissingReagents(stock));
            throw ApiErrors.Refused("missing_reagents",
                FabricateRule.MissingReagents(Names.Of(fab), missing, IngotsOf(missing)), new Dictionary<string, object?> { ["missing"] = missing });
        }

        return new FabricationPlan(fab, loads, product, recipe, order.Make);
    }

    /// <summary>The call acted on: ingots loaded, the recipe subtracted, the item created in the output slot.</summary>
    internal static FabricateView Make(FabricationPlan plan)
    {
        SimpleFabricatorBase fab = plan.Fabricator;
        Dictionary<string, double> before = Stock(fab.ReagentMixture);
        foreach (FabricationLoad load in plan.Loads)
        {
            fab.ReagentMixture.Add(load.Ingot.CreatedReagentMixture * load.Quantity);
        }

        List<FabricateMadeView> made = new List<FabricateMadeView>();
        BuildStateManufacturingDat dat = fab.CurrentBuildState.ManufactureDat;
        if (plan.Product != null)
        {
            fab.CurrentHash = plan.Product.PrefabHash;
            if (!fab.ReagentMixture.Contains(plan.Recipe))
            {
                throw new ApiException("internal_error",
                    "The fabricator's stock lost an ingredient between the check and the make.");
            }

            fab.ReagentMixture.Subtract(plan.Recipe, dat.MaterialCostMultiplier);
            made = Create(fab, plan.Product, dat.ItemSpawnMultiplier);
        }

        return View(plan, before, Stock(fab.ReagentMixture), Loaded(plan), made);
    }

    /// <summary>What the call would do, nothing changed.</summary>
    internal static FabricateView Preview(FabricationPlan plan)
    {
        SimpleFabricatorBase fab = plan.Fabricator;
        Dictionary<string, double> before = Stock(fab.ReagentMixture);
        ReagentMixture stock = new ReagentMixture(fab.ReagentMixture);
        foreach (FabricationLoad load in plan.Loads)
        {
            stock.Add(load.Ingot.CreatedReagentMixture * load.Quantity);
        }

        if (plan.Product != null)
        {
            stock.Subtract(plan.Recipe, fab.CurrentBuildState.ManufactureDat.MaterialCostMultiplier);
        }

        return View(plan, before, Stock(stock), Loaded(plan), new List<FabricateMadeView>());
    }

    private static FabricateView View(FabricationPlan plan, Dictionary<string, double> before,
        Dictionary<string, double> after, List<FabricateLoadView> loaded, List<FabricateMadeView> made)
    {
        SimpleFabricatorBase fab = plan.Fabricator;
        BuildStateManufacturingDat dat = fab.CurrentBuildState.ManufactureDat;
        Dictionary<string, double> added = new Dictionary<string, double>();
        foreach (FabricateLoadView load in loaded)
        {
            added[load.Reagent] = (added.TryGetValue(load.Reagent, out double sum) ? sum : 0.0) + load.Quantity;
        }

        FabricateRecipeView? recipe = null;
        FabricateSkippedView? skipped = null;
        if (plan.Product != null)
        {
            recipe = new FabricateRecipeView(plan.Product.PrefabName, plan.Product.DisplayName,
                plan.Product.RecipeTier.ToString(), plan.Recipe.Time, plan.Recipe.Energy,
                PrefabRecipes.Inputs(plan.Recipe));
            skipped = new FabricateSkippedView(plan.Recipe.Time * dat.BuildTimeMultiplier,
                plan.Recipe.Energy * dat.EnergyCostMultiplier);
        }

        return new FabricateView(true, GameLookup.ViewOf(fab), fab.CurrentTier.ToString(), recipe,
            new FabricateMultipliersView(dat.BuildTimeMultiplier, dat.EnergyCostMultiplier, dat.MaterialCostMultiplier,
                dat.ItemSpawnMultiplier),
            loaded, before, after, FabricateRule.Consumed(before, added, after), new Dictionary<string, double>(), made,
            skipped);
    }

    // The game's own SpawnCreatedItems lines (it reads a private field, so it cannot be called): one item per unit of
    // the output multiplier, each moved into the export slot; a stackable product makes one, its quantity the multiplier.
    private static List<FabricateMadeView> Create(SimpleFabricatorBase fab, DynamicThing product, int count)
    {
        List<FabricateMadeView> made = new List<FabricateMadeView>();
        Slot export = fab.ExportSlot;
        int slot = fab.Slots.IndexOf(export);
        for (int index = 0; index < count; index++)
        {
            DynamicThing item = Thing.Create<DynamicThing>(product, export.Location.position,
                export.Location.rotation, 0L);
            item.name = product.name;
            OnServer.MoveToSlot(item, export);
            DynamicThing.ItemManufactured(item, count);
            made.Add(new FabricateMadeView(new ThingId(item.ReferenceId), item.PrefabName, item.DisplayName,
                item is Stackable stack ? stack.Quantity : 1.0, slot));
            if (item is Stackable)
            {
                break;
            }
        }

        return made;
    }

    private static List<FabricateLoadView> Loaded(FabricationPlan plan)
    {
        List<FabricateLoadView> loaded = new List<FabricateLoadView>(plan.Loads.Count);
        foreach (FabricationLoad load in plan.Loads)
        {
            foreach (KeyValuePair<Reagent, double> part in Amounts(load.Ingot.CreatedReagentMixture))
            {
                double amount = part.Value * load.Quantity;
                if (amount > FabricateRule.Trace)
                {
                    loaded.Add(new FabricateLoadView(load.Ingot.PrefabName, part.Key.TypeName,
                        System.Math.Round(amount, 6)));
                }
            }
        }

        return loaded;
    }

    private static FabricationLoad ResolveLoad(SimpleFabricatorBase fab, LoadEntry entry)
    {
        Thing found = ItemSpawns.ThingPrefab(entry.Prefab);
        if (!(found is Ingot ingot) || ingot == null)
        {
            throw ApiErrors.Refused("not_accepted",
                $"{found.PrefabName} is not an ingot; load takes ingots only. Nothing was loaded or made.");
        }

        foreach (KeyValuePair<Reagent, double> part in Amounts(ingot.CreatedReagentMixture))
        {
            if (part.Value > 0.0 && !fab.CanProcess(part.Key))
            {
                throw ApiErrors.Refused("not_accepted",
                    $"{Names.Of(fab)} does not take {part.Key.TypeName} ({ingot.PrefabName}). " +
                    "Nothing was loaded or made.");
            }
        }

        return new FabricationLoad(ingot, entry.Quantity);
    }

    private static (DynamicThing, Recipe) ResolveRecipe(SimpleFabricatorBase fab, PrefabPattern pattern)
    {
        Thing found = ItemSpawns.ThingPrefab(pattern);
        int hash = found.PrefabHash;
        DynamicThing? key = null;
        Recipe recipe = default;
        if (fab.Recipes != null)
        {
            foreach (KeyValuePair<DynamicThing, Recipe> pair in fab.Recipes)
            {
                if (pair.Key != null && pair.Key.PrefabHash == hash)
                {
                    key = pair.Key;
                    recipe = pair.Value;
                    break;
                }
            }
        }

        if (key == null)
        {
            List<string> makers = new List<string>();
            foreach (RecipeReference reference in WorldModel.Prefabs.Recipes.Of(hash))
            {
                if (reference.Creator != null && !makers.Contains(reference.Creator.PrefabName))
                {
                    makers.Add(reference.Creator.PrefabName);
                }
            }

            throw ApiErrors.Refused("not_a_recipe",
                FabricateRule.NotARecipe(found.PrefabName, Names.Of(fab), makers), new Dictionary<string, object?> { ["makers"] = makers });
        }

        bool valid = false;
        if (fab.ValidDynamicThings != null)
        {
            foreach (DynamicThing candidate in fab.ValidDynamicThings)
            {
                if (candidate != null && candidate.PrefabHash == hash)
                {
                    valid = true;
                    break;
                }
            }
        }

        if (!valid)
        {
            string recipeTier = key.RecipeTier.ToString();
            string machineTier = fab.CurrentTier.ToString();
            throw ApiErrors.Refused("wrong_tier",
                FabricateRule.WrongTier(key.PrefabName, recipeTier, Names.Of(fab), machineTier),
                new Dictionary<string, object?> { ["recipe_tier"] = recipeTier, ["fabricator_tier"] = machineTier });
        }

        if (!SharedDLCManager.CheckSharedAccess(key.DLCType))
        {
            throw ApiErrors.Refused("not_a_recipe",
                $"{key.PrefabName} needs a DLC this game lacks. Nothing was loaded or made.", new Dictionary<string, object?> { ["reason"] = "dlc" });
        }

        return (key, recipe);
    }

    private static ApiException Busy(SimpleFabricatorBase fab, string reason)
    {
        string name = Names.Of(fab);
        if (reason == "output_occupied")
        {
            Thing held = fab.ExportSlot.Get();
            ThingId occupant = new ThingId(held.ReferenceId);
            return ApiErrors.Refused("fabricator_busy",
                $"{name}'s output slot holds {Names.Of(held)} ({occupant}); take it out. " +
                "Nothing was loaded or made.", new Dictionary<string, object?> { ["reason"] = reason, ["occupant"] = occupant });
        }

        return ApiErrors.Refused("fabricator_busy",
            reason == "making"
                ? $"{name} is making something; wait for it. Nothing was loaded or made."
                : $"{name} is open and ejects its stock; close it. Nothing was loaded or made.",
            new Dictionary<string, object?> { ["reason"] = reason });
    }

    private static string? OccupantId(SimpleFabricatorBase fab) =>
        fab.ExportSlot?.Get() is { } occupant && occupant != null
            ? occupant.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    // The reagent's ingot prefab, for each reagent named (the first ingot made of it, as MachineStock counts it).
    private static Dictionary<string, string> IngotsOf(Dictionary<string, double> reagents)
    {
        Dictionary<string, string> ingots = new Dictionary<string, string>();
        foreach (KeyValuePair<Reagent, Ingot> pair in WorldModel.Prefabs.IngotsByReagent)
        {
            if (reagents.ContainsKey(pair.Key.TypeName))
            {
                ingots[pair.Key.TypeName] = pair.Value.PrefabName;
            }
        }

        return ingots;
    }

    // Reagent name to units, values above the trace only, as MachineStock reads a machine's stock.
    private static Dictionary<string, double> Stock(ReagentMixture mixture)
    {
        Dictionary<string, double> stock = new Dictionary<string, double>();
        foreach (KeyValuePair<Reagent, double> part in Amounts(mixture))
        {
            stock[part.Key.TypeName] = part.Value;
        }

        return stock;
    }

    // Each reagent the mixture holds above the trace, with its quantity (the world's reagents are read by index, as
    // MachineStock reads them).
    private static List<KeyValuePair<Reagent, double>> Amounts(ReagentMixture mixture)
    {
        List<KeyValuePair<Reagent, double>> parts = new List<KeyValuePair<Reagent, double>>();
        IReadOnlyList<Reagent> reagents = World.Reagents;
        for (int index = 0; index < reagents.Count; index++)
        {
            double quantity = mixture.Get(reagents[index]);
            if (quantity > FabricateRule.Trace)
            {
                parts.Add(new KeyValuePair<Reagent, double>(reagents[index], quantity));
            }
        }

        return parts;
    }
}

/// <summary>One checked load: the ingot prefab and the units.</summary>
internal sealed class FabricationLoad
{
    internal FabricationLoad(Ingot ingot, double quantity)
    {
        Ingot = ingot;
        Quantity = quantity;
    }

    internal Ingot Ingot { get; }

    internal double Quantity { get; }
}

/// <summary>A checked write fabricator: the machine, the loads, and the recipe's product and amounts (null when only loading).</summary>
internal sealed class FabricationPlan
{
    internal FabricationPlan(SimpleFabricatorBase fabricator, List<FabricationLoad> loads, DynamicThing? product,
        Recipe recipe, bool make)
    {
        Fabricator = fabricator;
        Loads = loads;
        Product = make ? product : null;
        Recipe = recipe;
    }

    internal SimpleFabricatorBase Fabricator { get; }

    internal List<FabricationLoad> Loads { get; }

    internal DynamicThing? Product { get; }

    internal Recipe Recipe { get; }
}
