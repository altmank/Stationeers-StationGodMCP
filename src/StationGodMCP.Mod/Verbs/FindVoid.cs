#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find void (catalogue find.json): the enclosed empty pockets of a box (VoidSearch over the box read over frames),
/// largest first, with the box build terrain's voids mode takes for each. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the ground as it reads now")]
internal static class FindVoid
{
    internal static VerbOutcome Run(FindVoidOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        DigBox? box = DigBox.Of(options.Selector.Box, problems, DigBox.MostSearched, "a call searches");
        if (box == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindVoidOptions.Spec.Label, problems));
        }

        int minCells = (int)System.Math.Min(options.MinCells ?? 8, int.MaxValue);
        return WorkResult<VoxelReading[]>.Of(answer => TerrainFills.Read(box, answer),
            readings => TerrainFills.Voids(box, readings, minCells));
    }
}
