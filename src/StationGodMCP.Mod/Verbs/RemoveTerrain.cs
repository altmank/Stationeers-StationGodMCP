#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// remove terrain (catalogue remove.json): every terrain voxel in the box dug out as the game's diggers dig one, the
/// ore in it dropped or discarded (TerrainDigs). A cheat: without confirm the call reads the box over frames and reports
/// what the dig would take, its box ready for show preview, and digs nothing.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class RemoveTerrain
{
    internal static VerbOutcome Run(RemoveTerrainOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        DigBox? box = DigBox.Of(options.Selector.Box, problems);
        if (box == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(RemoveTerrainOptions.Spec.Label, problems));
        }

        OreHandling ores = OreHandling.Of(options.Ores);
        string undoMark = RemoveTerrainOptions.Spec.Write!.UndoMark;
        return WriteCheck<TerrainDigView>.Planned(answer => TerrainDigs.Read(box, ores, answer), options.Report,
            _ => new VerbOutcome.Pending(TerrainDigs.Dig(box, ores)),
            reading => TerrainDigReplies.Reported(reading, undoMark));
    }
}
