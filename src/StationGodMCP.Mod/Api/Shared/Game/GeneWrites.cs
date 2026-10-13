#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Genetics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Writes genes into one gene set the way the game's Gene Splicer does (PlantGeneReads describes how the game keeps
/// genes), following PlantGeneticSplicer.StartSplice: set the value in the Lookup and, on a multiplayer host, set
/// network flag 512, which Plant.BuildUpdate sends as the top gene set. A multiplayer client does not own the plant, so
/// it is refused. The checks (the plant, the host, the gene set, then each gene's name and value) make a plan; the
/// write applies it, and a report describes it.
/// </summary>
internal static class GeneWriter
{
    // Plant.BuildUpdate sends the top gene set when this network flag is set.
    private const ushort GenesNetworkFlag = 512;

    /// <summary>
    /// The plan for genes (gene name to a number) in the plant's gene set unit (the top when null): each gene's edit and
    /// result, or why it is not written. allowOutOfRange writes values outside the game's range.
    /// </summary>
    internal static GenesPlan Check(ThingId id, int? unit, JObject genes, bool allowOutOfRange)
    {
        Plant plant = PlantGenePlants.Require(id);
        if (NetworkManager.IsClient)
        {
            throw ApiErrors.Refused("not_host",
                "This game is a multiplayer client; only the host can change genes.");
        }

        if (!GeneSets.TryPick(plant, unit, out GeneCollection? set, out GeneSetHeader? header,
                out ApiException? error))
        {
            throw error!;
        }

        BatchBuilder results = new BatchBuilder(genes.Count);
        List<GeneEdit> edits = new List<GeneEdit>(genes.Count);
        int index = 0;
        foreach (JProperty property in genes.Properties())
        {
            if (TryPlan(set!, property, allowOutOfRange, index, edits, out BatchItemView item))
            {
                results.Succeeded(item);
            }
            else
            {
                results.Failed(item);
            }

            index++;
        }

        return new GenesPlan(plant, set!, header!, edits, results.Build());
    }

    /// <summary>Writes the plan's edits; answers the gene set and each gene's result.</summary>
    internal static GenesPlan Write(GenesPlan plan)
    {
        foreach (GeneEdit edit in plan.Edits)
        {
            if (!plan.Set.Lookup.TryGetValue(edit.Gene, out GeneWrapper? wrapper) || wrapper == null)
            {
                // Every gene set the game makes has all 19 genes; a missing one is added as a new gene would be.
                wrapper = new GeneWrapper(edit.Gene, 0f, 0f);
                plan.Set.Lookup[edit.Gene] = wrapper;
            }

            wrapper.Value = edit.Value;
        }

        if (plan.Edits.Count > 0 && plan.Header.IsTop)
        {
            // The only gene-derived state the game caches; everything else reads the genes live.
            plan.Plant.lifeRequirements?.UpdateTemperaturePressureCurves();
            if (NetworkManager.IsServer)
            {
                plan.Plant.NetworkUpdateFlags |= GenesNetworkFlag;
            }
        }

        return plan;
    }

    // One gene: its edit and the result the write answers for it, or why it is not written.
    private static bool TryPlan(GeneCollection set, JProperty property, bool allowOutOfRange, int index, List<GeneEdit> edits,
        out BatchItemView item)
    {
        Gene? parsed = GeneTable.Parse(property.Name);
        if (!parsed.HasValue)
        {
            item = new GeneNotWrittenView(index, property.Name, null, ApiErrors.Refused("unknown_gene",
                $"'{property.Name}' is not a gene. Genes: {string.Join(", ", GeneCollection.Genes)}."));
            return false;
        }

        Gene gene = parsed.Value;
        set.Lookup.TryGetValue(gene, out GeneWrapper? wrapper);
        float? previous = wrapper?.Value;
        // GeneOrders has refused any value that is not a number.
        double value = property.Value.Value<double>();
        ApiException? refusal = ValueRefusal(gene, value, allowOutOfRange);
        if (refusal != null)
        {
            item = new GeneNotWrittenView(index, property.Name, previous, refusal);
            return false;
        }

        edits.Add(new GeneEdit(gene, (float)value));
        item = new GeneWrittenView(index, gene.ToString(), previous, (float)value);
        return true;
    }

    private static ApiException? ValueRefusal(Gene gene, double value, bool allowOutOfRange)
    {
        switch (GeneValues.Refusal(value, allowOutOfRange))
        {
            case GeneValueRefusal.NotFinite:
                return ApiErrors.InvalidArgument($"Gene {gene}: the value must be finite.");
            case GeneValueRefusal.OutOfRange:
                return ApiErrors.Refused("out_of_range",
                    $"Gene {gene}: {value} is outside the game's range {GeneView.Minimum} to {GeneView.Maximum}; " +
                    "allow out_of_range writes it anyway.");
            default:
                return null;
        }
    }

    /// <summary>One gene's new value.</summary>
    internal sealed class GeneEdit
    {
        internal GeneEdit(Gene gene, float value)
        {
            Gene = gene;
            Value = value;
        }

        internal Gene Gene { get; }

        internal float Value { get; }
    }

    /// <summary>A checked write: the plant, its gene set, the edits and each gene's result.</summary>
    internal sealed class GenesPlan
    {
        internal GenesPlan(Plant plant, GeneCollection set, GeneSetHeader header, List<GeneEdit> edits,
            BatchResultView results)
        {
            Plant = plant;
            Set = set;
            Header = header;
            Edits = edits;
            Results = results;
        }

        internal Plant Plant { get; }

        internal GeneCollection Set { get; }

        internal GeneSetHeader Header { get; }

        internal List<GeneEdit> Edits { get; }

        internal BatchResultView Results { get; }
    }
}
