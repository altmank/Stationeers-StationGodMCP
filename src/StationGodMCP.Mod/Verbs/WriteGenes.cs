#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write genes (catalogue write.json): genes edited in one gene set of a plant, seed or produce, as the Gene Splicer
/// does (GeneWriter); unit picks the set, the top by default. Each gene is written or refused alone; allow out_of_range
/// writes values outside -1..1. One id, or "looking_at". A cheat: without confirm the call reports every gene from the
/// same checks and writes nothing.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteGenes
{
    internal static VerbOutcome Run(WriteGenesOptions options)
    {
        string label = WriteGenesOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        JObject? genes = GeneOrders.Of(options.Genes, problems);
        if (options.Unit is > int.MaxValue)
        {
            problems.Add(new OptionProblem("unit", $"is a gene set from 0 to {int.MaxValue}"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string id = options.Selector.Ids!.Ids[0];
        int? unit = options.Unit is { } given ? (int)given : null;
        bool allowOutOfRange = Allow.Lifts(options.Allow, "out_of_range");
        string undoMark = WriteGenesOptions.Spec.Write!.UndoMark;
        return WriteCheck<GeneWriter.GenesPlan>.Run(() =>
                GeneWriter.Check(TargetIds.Of(id, label), unit, genes!, allowOutOfRange))
            .Outcome(options.Report,
                plan =>
                {
                    GeneWriter.GenesPlan written = GeneWriter.Write(plan);
                    return new VerbOutcome.Answered(GeneReplies.Acted(written.Header, written.Results));
                },
                plan => GeneReplies.Reported(plan.Header, plan.Results, undoMark));
    }
}
