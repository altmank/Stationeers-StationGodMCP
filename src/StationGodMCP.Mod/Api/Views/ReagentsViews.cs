#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>reagents: a thing's reagents, each with the amount the game holds.</summary>
internal sealed class ReagentsView
{
    internal ReagentsView(ThingView thing, double total, List<ReagentView> reagents)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Total = total;
        Reagents = reagents;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>
    /// The sum of every reagent's quantity, as the game adds it (ReagentMixture.TotalReagents), units mixed.
    /// </summary>
    public double Total { get; }

    public List<ReagentView> Reagents { get; }
}

internal sealed class ReagentView
{
    internal ReagentView(string reagent, string? name, double quantity, string? unit)
    {
        Reagent = reagent;
        Name = name;
        Quantity = quantity;
        Unit = unit;
    }

    /// <summary>The game's type name, e.g. Iron.</summary>
    public string Reagent { get; }

    public string? Name { get; }

    /// <summary>In the reagent's own unit, Unit.</summary>
    public double Quantity { get; }

    /// <summary>The game's unit for this reagent (Reagent.Unit): g for most, ml for alcohol, milk and oil.</summary>
    public string? Unit { get; }
}
