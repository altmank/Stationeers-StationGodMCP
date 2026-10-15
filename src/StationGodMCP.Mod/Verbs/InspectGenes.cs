#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect genes (catalogue inspect.json): a plant's, seed's or produce's gene set with each gene's effect on its
/// stats (GeneReader); with one id, unit picks the set, default the top; include notes adds each gene's fixed text. A
/// list of ids answers one row each, every one its top set. Read only.
/// </summary>
internal static class InspectGenes
{
    private const string Notes = "notes";

    [Profiled]
    internal static VerbOutcome Run(InspectGenesOptions options)
    {
        PerIdList ids = options.Selector.Ids!;
        if (ids.AnswersRows && options.Unit != null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(InspectGenesOptions.Spec.Label,
                new[] { new OptionProblem("unit", "goes with one id: a list reads each one's top gene set") }));
        }

        int? unit = options.Unit is { } given ? (int)System.Math.Min(given, int.MaxValue) : null;
        bool meanings = options.Fields.Includes(Notes);
        return IdAnswers.Things(ids, InspectGenesOptions.Spec.Label,
            id => GeneReader.Read(PlantGenePlants.Require(id), unit, meanings));
    }
}
