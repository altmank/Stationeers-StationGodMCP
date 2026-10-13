#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Genetics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;


/// <summary>
/// plant_genes' write: edits one gene set the way the game's Gene Splicer does (PlantGeneReads describes how the game
/// keeps genes). The write follows PlantGeneticSplicer.StartSplice: set the value in the Lookup and, on a multiplayer
/// host, set network flag 512, which Plant.BuildUpdate sends as the top gene set. A multiplayer client does not own the
/// plant, so it is refused.
/// </summary>
internal static class PlantGenesApi
{
    internal static object Handle(Args args) =>
        GeneWriter.Answer(GenesRequest.Parse(args), ReportOnly.Asked(args));
}

/// <summary>plant_genes' write form.</summary>
internal static class GenesRequest
{
    internal static Write Parse(Args args)
    {
        ThingId id = args.ThingId("reference_id");
        int? unit = args.OptionalInt("unit", 0, int.MaxValue);
        JObject? genes = args.OptionalObject("genes");
        if (genes == null || !genes.HasValues)
        {
            throw ApiErrors.InvalidArgument(
                Vocabulary.Text("Argument '{arg:genes}' must be an object of gene name to value, with at least one " +
                    "gene."));
        }

        return new Write(id, unit, GeneArgs.RequireNumbers(genes), args.OptionalBool("force") ?? false);
    }

    internal sealed class Write
    {
        internal Write(ThingId id, int? unit, JObject genes, bool force)
        {
            Id = id;
            Unit = unit;
            Genes = genes;
            Force = force;
        }

        internal ThingId Id { get; }

        internal int? Unit { get; }

        internal JObject Genes { get; }

        internal bool Force { get; }
    }
}

/// <summary>
/// Writes genes into one gene set, as PlantGeneticSplicer.StartSplice does. The checks (the plant, the host, the gene
/// set, then each gene's name and value) make a plan; the write applies it, and dry_run reports it (ReportOnly).
/// </summary>
internal static class GeneWriter
{
    // Plant.BuildUpdate sends the top gene set when this network flag is set.
    private const ushort GenesNetworkFlag = 512;

    internal static object Answer(GenesRequest.Write request, bool report) =>
        WriteCheck<GenesPlan>.Run(() => Check(request)).Answer(report, Write, Describe);

    private static GenesPlan Check(GenesRequest.Write request)
    {
        Plant plant = PlantGenePlants.Require(request.Id);
        if (NetworkManager.IsClient)
        {
            throw ApiErrors.Refused("not_host",
                "This game is a multiplayer client; only the host can change genes.");
        }

        if (!GeneSets.TryPick(plant, request.Unit, out GeneCollection? set, out GeneSetHeader? header,
                out ApiException? error))
        {
            throw error!;
        }

        BatchBuilder results = new BatchBuilder(request.Genes.Count);
        List<GeneEdit> edits = new List<GeneEdit>(request.Genes.Count);
        int index = 0;
        foreach (JProperty property in request.Genes.Properties())
        {
            if (TryPlan(set!, property, request.Force, index, edits, out BatchItemView item))
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

    private static object Write(GenesPlan plan)
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

        return new GenesWrittenView(plan.Header, plan.Results);
    }

    private static object Describe(GenesPlan plan) => new GenesReportView(plan.Header, plan.Results);

    // One gene: its edit and the result the write answers for it, or why it is not written.
    private static bool TryPlan(GeneCollection set, JProperty property, bool force, int index, List<GeneEdit> edits,
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
        // GenesRequest.Parse has refused any value that is not a number.
        double value = property.Value.Value<double>();
        ApiException? refusal = ValueRefusal(gene, value, force);
        if (refusal != null)
        {
            item = new GeneNotWrittenView(index, property.Name, previous, refusal);
            return false;
        }

        edits.Add(new GeneEdit(gene, (float)value));
        item = new GeneWrittenView(index, gene.ToString(), previous, (float)value);
        return true;
    }

    private static ApiException? ValueRefusal(Gene gene, double value, bool force)
    {
        switch (GeneArgs.ValueRefusal(value, force))
        {
            case GeneValueRefusal.NotFinite:
                return ApiErrors.InvalidArgument($"Gene {gene}: the value must be finite.");
            case GeneValueRefusal.OutOfRange:
                return ApiErrors.Refused("out_of_range",
                    $"Gene {gene}: {value} is outside the game's range {GeneView.Minimum} to {GeneView.Maximum}; pass " +
                    $"{Vocabulary.Arg("force")} to write it anyway.");
            default:
                return null;
        }
    }

    /// <summary>One gene's new value.</summary>
    private sealed class GeneEdit
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
    private sealed class GenesPlan
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
