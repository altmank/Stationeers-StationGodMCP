#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Reagents;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Material a machine holds as reagents rather than as items (Thing.ReagentMixture), so find_items and item_totals can
/// count it. Two kinds, decided by the machine:
///
/// fabricator: a FabricatorBase (Autolathe, Electronics Printer, Pipe Bender, Tool Manufactory...). Ingots put in
/// become its stock and stop being items. Opening it ejects the stock again (SimpleFabricatorBase.OnServerExportTick,
/// Thing.DropReagent: one reagent at a time, (int) of its quantity as ingots, 1 reagent unit = 1 ingot), as the ingot
/// SimpleFabricatorBase.GetPrefabHashFromReagentHash names: the first Ingot.AllIngotPrefabs entry whose
/// CreatedReagentMixture contains that reagent. So fabricator stock counts under that ingot's prefab.
///
/// processing: any other structure with reagents (a furnace's or arc furnace's melted load, a centrifuge's, a
/// mixer's, an oven's). That load is not ingots yet: a furnace smelts it into whatever recipe the whole mix matches,
/// or drops it as Reagent Mix. So it counts under its reagent's name, never under an ingot prefab.
///
/// A fabricator reagent no ingot is made of (Flour in an oven) counts under its reagent name too.
/// </summary>
internal static class MachineStock
{
    internal const string Location = MachineStockView.Location;

    /// <summary>Amounts at or below this are rounding left in the mixture (ReagentsApi's rule).</summary>
    private const double TraceAmount = 1e-6;

    /// <summary>Every stock entry the filter keeps, one per machine and reagent, in structure order.</summary>
    internal static List<StockRecord> Collect(ItemFilter filter, PlayerOrigin origin)
    {
        List<StockRecord> records = new List<StockRecord>();
        if (!filter.WantsStock)
        {
            return records;
        }

        Dictionary<Reagent, Ingot> ingots = IngotsByReagent();
        if (!CanKeepAny(filter.Prefab, ingots))
        {
            return records;
        }

        List<Structure> structures = GridController.AllStructuresPool.ToList();
        for (int index = 0; index < structures.Count; index++)
        {
            Structure machine = structures[index];
            ReagentMixture? mixture = machine != null ? machine.ReagentMixture : null;
            if (mixture == null || machine!.IsCursor || machine.IsBeingDestroyed ||
                !(mixture.TotalReagents > TraceAmount))
            {
                continue;
            }

            AddMachine(records, machine, mixture, ingots, filter, origin);
        }

        return records;
    }

    private static void AddMachine(List<StockRecord> records, Structure machine, ReagentMixture mixture,
        Dictionary<Reagent, Ingot> ingots, ItemFilter filter, PlayerOrigin origin)
    {
        bool fabricator = machine is FabricatorBase;
        double? distance = origin.ExactDistanceTo(machine.Position);
        foreach (Reagent reagent in Reagent.AllReagents)
        {
            double quantity = mixture.Get(reagent);
            if (!(quantity > TraceAmount))
            {
                continue;
            }

            Ingot? ingot = fabricator && ingots.TryGetValue(reagent, out Ingot found) ? found : null;
            StockRecord record = new StockRecord(machine, fabricator ? StockKind.Fabricator : StockKind.Processing,
                reagent.TypeName, reagent.DisplayName, ingot != null ? ingot.PrefabName : null,
                ingot != null ? ingot.DisplayName : null, quantity, distance);
            if (filter.Keeps(record))
            {
                records.Add(record);
            }
        }
    }

    // A stock entry counts under an ingot's prefab or under none (StockRecord.IngotPrefab), so a prefab filter that
    // keeps none of those names keeps no stock, and the walk over every structure is skipped.
    private static bool CanKeepAny(PrefabMatch prefab, Dictionary<Reagent, Ingot> ingots)
    {
        if (prefab.Keeps(null))
        {
            return true;
        }

        foreach (Ingot ingot in ingots.Values)
        {
            if (prefab.Keeps(ingot.PrefabName))
            {
                return true;
            }
        }

        return false;
    }

    // SimpleFabricatorBase.GetPrefabHashFromReagentHash: the first ingot prefab whose CreatedReagentMixture contains
    // the reagent. Built per call: a few dozen ingots and reagents.
    private static Dictionary<Reagent, Ingot> IngotsByReagent()
    {
        Dictionary<Reagent, Ingot> ingots = new Dictionary<Reagent, Ingot>();
        foreach (Reagent reagent in Reagent.AllReagents)
        {
            foreach (Ingot ingot in Ingot.AllIngotPrefabs)
            {
                if (ingot != null && ingot.CreatedReagentMixture != null &&
                    ingot.CreatedReagentMixture.Contains(reagent))
                {
                    ingots[reagent] = ingot;
                    break;
                }
            }
        }

        return ingots;
    }
}

/// <summary>What a machine's stock is: ejectable ingots, or a working load.</summary>
internal enum StockKind
{
    Fabricator,
    Processing
}

/// <summary>One reagent in one machine, and what it counts as.</summary>
internal sealed class StockRecord
{
    internal StockRecord(Thing machine, StockKind kind, string reagent, string? reagentName, string? ingotPrefab,
        string? ingotName, double quantity, double? distance)
    {
        Machine = machine;
        Kind = kind;
        Reagent = reagent;
        ReagentName = reagentName;
        IngotPrefab = ingotPrefab;
        IngotName = ingotName;
        Quantity = quantity;
        Distance = distance;
    }

    internal Thing Machine { get; }

    internal StockKind Kind { get; }

    /// <summary>The reagent's type name (Reagent.TypeName), e.g. Electrum.</summary>
    internal string Reagent { get; }

    internal string? ReagentName { get; }

    /// <summary>The ingot a fabricator ejects it as; null for a working load or a reagent with no ingot.</summary>
    internal string? IngotPrefab { get; }

    internal string? IngotName { get; }

    /// <summary>Reagent units; for fabricator stock that is ingots (the fraction is not ejected).</summary>
    internal double Quantity { get; }

    internal double? Distance { get; }

    /// <summary>The name it is listed and filtered under: the ingot's, else the reagent's.</summary>
    internal string? DisplayName => IngotName ?? ReagentName ?? Reagent;

    internal string KindName => Kind == StockKind.Fabricator ? "fabricator" : "processing";

    internal StockItemView ToView()
    {
        double? distance = Distance.HasValue ? System.Math.Round(Distance.Value, PositionView.Decimals) : null;
        List<HeldInView> heldIn = new List<HeldInView>
        {
            new HeldInView(GameLookup.ViewOf(Machine), StockItemView.NoSlot, StockItemView.StockSlotName)
        };
        return new StockItemView(IngotPrefab, DisplayName, Quantity, heldIn, GameLookup.ViewOf(Machine.Position),
            distance, new MachineStockView(Reagent, ReagentName, KindName));
    }
}
