#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// write genes (catalogue write.json, genes): genes is gene name to value, at least one, every value a number; each is
/// a problem otherwise, and nothing is written.
/// </summary>
internal static class GeneOrders
{
    internal const string Option = "genes";

    /// <summary>The genes asked for; null with every problem added.</summary>
    internal static JObject? Of(JToken? genes, List<OptionProblem> problems)
    {
        if (genes is not JObject named || !named.HasValues)
        {
            problems.Add(new OptionProblem(Option, "names at least one gene: {gene name: value from -1 to 1}"));
            return null;
        }

        int before = problems.Count;
        foreach (JProperty gene in named.Properties())
        {
            if (gene.Value.Type is not (JTokenType.Integer or JTokenType.Float))
            {
                problems.Add(new OptionProblem($"{Option}.{gene.Name}", "must be a number"));
            }
        }

        return problems.Count == before ? named : null;
    }
}

/// <summary>
/// write genes' replies: {thing, holder, unit, is_top, results, count, success_count, error_count}, one result per gene
/// {index, ok, gene, previous_value, value | error}. The report has the same keys but results: each gene written is a
/// target, [plant id, "plant (Gene)", previous -> value], with the what's undo mark, and each refused one a problem
/// with its index.
/// </summary>
internal static class GeneReplies
{
    private const string ResultsKey = "results";

    internal static JObject Acted(GeneSetHeader header, BatchResultView results) =>
        JObject.FromObject(new GenesWrittenView(header, results), ApiJson.Serializer);

    internal static ReportReply Reported(GeneSetHeader header, BatchResultView results, string undoMark)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        string plant = header.Thing.ReferenceId.ToString();
        List<ReportTarget> targets = new List<ReportTarget>(results.Results.Count);
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (BatchItemView result in results.Results)
        {
            switch (result)
            {
                case GeneWrittenView written:
                    targets.Add(new ReportTarget(plant, null, $"{header.Thing.DisplayName} ({written.Gene})",
                        ReplyFields.Value(written.PreviousValue, serializer), ReplyFields.Value(written.Value, serializer),
                        undoMark));
                    break;
                case GeneNotWrittenView refused:
                    problems.Add(new ReportProblem(refused.Index, plant,
                        new VerbError(refused.Error.Code, refused.Error.Message)));
                    break;
            }
        }

        JObject body = Acted(header, results);
        body.Remove(ResultsKey);
        return Report.Of(targets, body, problems);
    }
}
