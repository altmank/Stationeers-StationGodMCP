#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// build terrain (catalogue build.json): the box's ground filled solid, reverted to the generated ground, levelled to a
/// flat plane, or its enclosed pockets filled (TerrainFills), never in a structure's cell nor over a player. A cheat:
/// without confirm the call reads the box over frames and reports what the fill would do, and changes nothing.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class BuildTerrain
{
    internal static VerbOutcome Run(BuildTerrainOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        DigBox? box = DigBox.Of(options.Selector.Box, problems);
        FillMode? mode = FillMode.Of(options.Mode, options.Level, problems);
        if (box == null || mode == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(BuildTerrainOptions.Spec.Label, problems));
        }

        OreHandling ores = OreHandling.Of(options.Ores);
        string undoMark = BuildTerrainOptions.Spec.Write!.UndoMark;
        return WriteCheck<FillPlan>.Planned(answer => TerrainFills.Checked(box, mode, ores, answer), options.Report,
            plan => new VerbOutcome.Pending(TerrainFills.Fill(plan)),
            plan => TerrainFillReplies.Reported(plan.Reading, undoMark));
    }
}
