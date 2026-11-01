#nullable enable

using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Reagents;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// inspect prefab include recipes (2.8.0): what makes a prefab, from the game's own recipe list (ElectronicReader's
/// AllRecipes, built from every machine's recipes when a world loads: fabricators, printers, furnaces, the
/// packaging machines, the reagent processor and the centrifuge). Each recipe gives the machine that makes it, its
/// inputs (Recipe's material amounts above zero, by name: grams of an ingot or units of a reagent), its time and energy,
/// the temperature and pressure range a furnace needs, and the item a recipe takes as its source, where it has one.
/// Main thread only.
/// </summary>
internal static class PrefabRecipes
{
    private static List<FieldInfo>? _amounts;

    internal static List<RecipeView> Of(Thing prefab)
    {
        List<RecipeReference> made = WorldModel.Prefabs.Recipes.Of(prefab.PrefabHash);
        List<RecipeView> recipes = new List<RecipeView>(made.Count);
        foreach (RecipeReference reference in made)
        {
            Recipe recipe = reference.Recipe;
            Thing? maker = reference.Creator;
            recipes.Add(new RecipeView(maker != null ? maker.PrefabName : null,
                maker != null ? Names.Of(maker) : null, recipe.Time, recipe.Energy, Inputs(recipe),
                recipe.Temperature.IsValid ? new RangeView(recipe.Temperature.Start, recipe.Temperature.Stop) : null,
                recipe.Pressure.IsValid ? new RangeView(recipe.Pressure.Start, recipe.Pressure.Stop) : null,
                reference.Source != null ? reference.Source.PrefabName : null));
        }

        return recipes;
    }

    // The recipe's amounts: every double field of Recipe (Iron, Copper, Solder, Potato...) above zero.
    private static Dictionary<string, double> Inputs(Recipe recipe)
    {
        Dictionary<string, double> inputs = new Dictionary<string, double>();
        object boxed = recipe;
        foreach (FieldInfo field in Amounts())
        {
            double amount = (double)field.GetValue(boxed);
            if (amount > 0.0)
            {
                inputs[field.Name] = System.Math.Round(amount, 6);
            }
        }

        return inputs;
    }

    private static List<FieldInfo> Amounts()
    {
        if (_amounts == null)
        {
            _amounts = new List<FieldInfo>();
            foreach (FieldInfo field in typeof(Recipe).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType == typeof(double))
                {
                    _amounts.Add(field);
                }
            }
        }

        return _amounts;
    }
}

/// <summary>The game's recipes by the PrefabHash of what each makes, built once per load (WorldModel.Prefabs.Recipes).</summary>
internal sealed class RecipeIndex
{
    private static readonly List<RecipeReference> None = new List<RecipeReference>();

    private readonly Dictionary<int, List<RecipeReference>> _byMade;

    private RecipeIndex(Dictionary<int, List<RecipeReference>> byMade, int count)
    {
        _byMade = byMade;
        Count = count;
    }

    /// <summary>How many recipes the game listed when this was built.</summary>
    internal int Count { get; }

    internal static RecipeIndex Load(IReadOnlyList<RecipeReference> recipes)
    {
        Dictionary<int, List<RecipeReference>> byMade = new Dictionary<int, List<RecipeReference>>();
        for (int index = 0; index < recipes.Count; index++)
        {
            RecipeReference? recipe = recipes[index];
            if (recipe?.DynamicThing == null)
            {
                continue;
            }

            if (!byMade.TryGetValue(recipe.DynamicThing.PrefabHash, out List<RecipeReference> list))
            {
                byMade[recipe.DynamicThing.PrefabHash] = list = new List<RecipeReference>();
            }

            list.Add(recipe);
        }

        return new RecipeIndex(byMade, recipes.Count);
    }

    internal List<RecipeReference> Of(int prefabHash) =>
        _byMade.TryGetValue(prefabHash, out List<RecipeReference> list) ? list : None;
}
