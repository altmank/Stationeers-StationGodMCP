#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// write fabricator's reply: ready (a report: the call would go through; confirmed: true), the fabricator and its
/// tier, the recipe (null when only loading), the build state's multipliers, the ingots loaded, the stock before and
/// after (reagent name to units), what the make consumed, what is still missing (always empty once a call passes its
/// checks), the items made (empty in a report), and what a real run would have spent in time and energy and this call
/// skipped (null when only loading).
/// </summary>
internal sealed class FabricateView
{
    internal FabricateView(bool ready, ThingView fabricator, string tier, FabricateRecipeView? recipe,
        FabricateMultipliersView multipliers, List<FabricateLoadView> loaded, Dictionary<string, double> stockBefore,
        Dictionary<string, double> stockAfter, Dictionary<string, double> consumed, Dictionary<string, double> missing,
        List<FabricateMadeView> made, FabricateSkippedView? skipped)
    {
        Ready = ready;
        Fabricator = fabricator;
        Tier = tier;
        Recipe = recipe;
        Multipliers = multipliers;
        Loaded = loaded;
        StockBefore = stockBefore;
        StockAfter = stockAfter;
        Consumed = consumed;
        Missing = missing;
        Made = made;
        Skipped = skipped;
    }

    public bool Ready { get; }

    public ThingView Fabricator { get; }

    public string Tier { get; }

    public FabricateRecipeView? Recipe { get; }

    public FabricateMultipliersView Multipliers { get; }

    public List<FabricateLoadView> Loaded { get; }

    public Dictionary<string, double> StockBefore { get; }

    public Dictionary<string, double> StockAfter { get; }

    public Dictionary<string, double> Consumed { get; }

    public Dictionary<string, double> Missing { get; }

    public List<FabricateMadeView> Made { get; }

    public FabricateSkippedView? Skipped { get; }
}

/// <summary>The recipe: the product, its tier, its time and energy as the recipe states them, and its inputs.</summary>
internal sealed class FabricateRecipeView
{
    internal FabricateRecipeView(string prefabName, string? displayName, string tier, double timeS, double energyJ,
        Dictionary<string, double> inputs)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        Tier = tier;
        TimeS = timeS;
        EnergyJ = energyJ;
        Inputs = inputs;
    }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    public string Tier { get; }

    public double TimeS { get; }

    public double EnergyJ { get; }

    public Dictionary<string, double> Inputs { get; }
}

/// <summary>The build state's multipliers on the recipe: time, energy and material cost, and the items made per make.</summary>
internal sealed class FabricateMultipliersView
{
    internal FabricateMultipliersView(double time, double energy, double material, int output)
    {
        Time = time;
        Energy = energy;
        Material = material;
        Output = output;
    }

    public double Time { get; }

    public double Energy { get; }

    public double Material { get; }

    public int Output { get; }
}

/// <summary>One ingot loaded: its prefab, the reagent it adds to the stock, and the units.</summary>
internal sealed class FabricateLoadView
{
    internal FabricateLoadView(string prefabName, string reagent, double quantity)
    {
        PrefabName = prefabName;
        Reagent = reagent;
        Quantity = quantity;
    }

    public string PrefabName { get; }

    public string Reagent { get; }

    public double Quantity { get; }
}

/// <summary>One item made, in the output slot.</summary>
internal sealed class FabricateMadeView
{
    internal FabricateMadeView(ThingId referenceId, string prefabName, string? displayName, double quantity, int slot)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        DisplayName = displayName;
        Quantity = quantity;
        Slot = slot;
    }

    public ThingId ReferenceId { get; }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    public double Quantity { get; }

    public int Slot { get; }
}

/// <summary>The time and energy of a real make that this call did not spend.</summary>
internal sealed class FabricateSkippedView
{
    internal FabricateSkippedView(double timeS, double energyJ)
    {
        TimeS = timeS;
        EnergyJ = energyJ;
    }

    public double TimeS { get; }

    public double EnergyJ { get; }
}
