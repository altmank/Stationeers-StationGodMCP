#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>reagents: the old StationApi.Reagents shape against ReagentsView, from the same values.</summary>
public sealed class ReagentsWireTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameWire(bool withReagents)
    {
        long referenceId = 9007199254740993;
        double total = withReagents ? 12.5 : 0.0;
        List<object> oldReagents = new List<object>();
        List<ReagentView> newReagents = new List<ReagentView>();
        if (withReagents)
        {
            oldReagents.Add(new { reagent = "Iron", name = "Iron", quantity = 10.25 });
            oldReagents.Add(new { reagent = "Carbon", name = (string?)null, quantity = 2.25 });
            newReagents.Add(new ReagentView("Iron", "Iron", 10.25, "g"));
            newReagents.Add(new ReagentView("Carbon", null, 2.25, "g"));
        }

        var old = new
        {
            reference_id = referenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            prefab_name = "StructureFurnace",
            display_name = "Furnace",
            total,
            reagents = oldReagents
        };
        ReagentsView view = new ReagentsView(
            new ThingView(new ThingId(referenceId), "StructureFurnace", "Furnace"), total, newReagents);
        WireCheck.SameAfterRenames(old, view, new Dictionary<string, string>(), "reagents[].unit");
    }
}
