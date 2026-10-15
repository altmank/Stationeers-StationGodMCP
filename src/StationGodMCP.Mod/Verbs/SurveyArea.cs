#nullable enable

using StationGodMCP.Pure.Paging;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey area (catalogue survey.json): the 2 m cells of a box or of a room, one page at a time, with occupied_only only
/// the cells that hold something; each cell by what occupies it, or with cell_detail full in full; then what stands in
/// the page's cells (AreaSurvey), sections keeping only the parts named and network, kind and prefab only those pieces
/// and devices. The first page keeps the cells (CellKey) and describes only its own; with the wire cursor the rest stays
/// under a cursor (ThingListPage), whose later pages describe their cells as they are then. Answered in its declared
/// shape (SurveyAreaReply). Read only.
/// </summary>
internal static class SurveyArea
{
    private const string Refund = "refund";
    private const string PieceCells = "piece_cells";

    private static readonly EntryCost Cost = new EntryCost();

    // survey area answers no scan counts.
    private static readonly object NoCounts = new object();

    internal static VerbOutcome Run(SurveyAreaOptions options)
    {
        Selector selector = options.Selector;
        List<OptionProblem> problems = new List<OptionProblem>();
        if (selector.Box == null && selector.Room == null)
        {
            problems.Add(new OptionProblem("box", "or room is needed: the cells to survey"));
        }
        else if (selector.Box != null && selector.Room != null)
        {
            problems.Add(new OptionProblem("room", "goes without box"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(SurveyAreaOptions.Spec.Label, problems));
        }

        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        GridFacts facts = AreaSurvey.Facts();
        if (walk.Continues)
        {
            return ThingListPage<KeptCell, object>.Later(walk, paging, key => new KeptCell(CellKey.Cell(key)), Cost,
                (page, cursorId, _) => Reply(options, facts, page, cursorId));
        }

        List<GridCell> cells = selector.Box is { } box
            ? AreaSurvey.InBox(box.First, box.Second)
            : AreaSurvey.InRoom(selector.Room!);
        if (options.OccupiedOnly == true)
        {
            cells = AreaSurvey.Occupied(facts, cells);
        }

        long[] keys = new long[cells.Count];
        for (int index = 0; index < cells.Count; index++)
        {
            keys[index] = CellKey.Of(cells[index]);
        }

        return ThingListPage<KeptCell, object>.First(walk, paging, keys, keys.Length, NoCounts,
            key => new KeptCell(CellKey.Cell(key)), Cost, (page, cursorId, _) => Reply(options, facts, page, cursorId));
    }

    // The page's cells described, and what stands in them.
    private static Newtonsoft.Json.Linq.JObject Reply(SurveyAreaOptions options, GridFacts facts, Page<KeptCell> page,
        string? cursorId)
    {
        Selector selector = options.Selector;
        SurveyFilter filter = new SurveyFilter(AreaSurvey.NetworkFilter(selector.Networks),
            SurveyKinds.Of(options.Kind), selector.Prefab);
        List<GridCell> shown = new List<GridCell>(page.Entries.Count);
        foreach (KeptCell cell in page.Entries)
        {
            shown.Add(cell.Cell);
        }

        SurveySections sections = SurveySections.Of(options.Sections);
        SurveyCellDetail detail = SurveyCellDetails.Of(options.CellDetail);
        bool withCells = sections.Includes(SurveySection.Cells);
        List<SurveyCell> views = withCells ? AreaSurvey.CellViews(facts, shown, detail) : new List<SurveyCell>();
        SurveyContents contents = AreaSurvey.Contents(facts, shown, sections, filter, options.Networks ?? true,
            options.Fields.Includes(Refund), options.Fields.Includes(PieceCells));
        string? legend = !withCells ? null
            : detail == SurveyCellDetail.Full ? SurveyLegends.Full
            : SurveyLegends.Occupancy;
        return SurveyAreaReply.Of(new Page<SurveyCell>(views, page.Total, page.Offset, page.Limit, page.HasMore),
            contents, sections, legend, cursorId);
    }

    /// <summary>A kept cell of a survey's page (a walk's entries are classes).</summary>
    private sealed class KeptCell
    {
        internal KeptCell(GridCell cell)
        {
            Cell = cell;
        }

        internal GridCell Cell { get; }
    }
}
