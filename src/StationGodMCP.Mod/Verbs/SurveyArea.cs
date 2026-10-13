#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey area (catalogue survey.json): the 2 m cells of a box or of a room, one page at a time by offset (cells are no
/// things a walk could keep), with occupied_only only the cells that hold something; each cell by what occupies it, or
/// with cell_detail full in full; then what stands in the page's cells (AreaSurvey), sections keeping only the parts
/// named and network, kind and prefab only those pieces and devices. Answered in its declared shape (SurveyAreaReply).
/// Read only.
/// </summary>
internal static class SurveyArea
{
    private const string Refund = "refund";
    private const string PieceCells = "piece_cells";

    internal static VerbOutcome Run(SurveyAreaOptions options)
    {
        Selector selector = options.Selector;
        List<OptionProblem> problems = new List<OptionProblem>();
        if (options.Paging.OffsetOnly() is { } cursor)
        {
            problems.Add(cursor);
        }

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

        SurveyFilter filter = new SurveyFilter(AreaSurvey.NetworkFilter(selector.Networks),
            SurveyKinds.Of(options.Kind), selector.Prefab);
        List<GridCell> cells = selector.Box is { } box
            ? AreaSurvey.InBox(box.First, box.Second)
            : AreaSurvey.InRoom(selector.Room!);
        GridFacts facts = AreaSurvey.Facts();
        if (options.OccupiedOnly == true)
        {
            cells = AreaSurvey.Occupied(facts, cells);
        }

        Page<GridCell> page = options.Paging.Of(cells);
        List<GridCell> shown = new List<GridCell>(page.Entries);
        SurveySections sections = SurveySections.Of(options.Sections);
        SurveyCellDetail detail = SurveyCellDetails.Of(options.CellDetail);
        bool withCells = sections.Includes(SurveySection.Cells);
        List<SurveyCell> views = withCells ? AreaSurvey.CellViews(facts, shown, detail) : new List<SurveyCell>();
        SurveyContents contents = AreaSurvey.Contents(facts, shown, sections, filter, options.Networks ?? true,
            options.Fields.Includes(Refund), options.Fields.Includes(PieceCells));
        string? legend = !withCells ? null
            : detail == SurveyCellDetail.Full ? SurveyLegends.Full
            : SurveyLegends.Occupancy;
        return new VerbOutcome.Answered(SurveyAreaReply.Of(
            new Page<SurveyCell>(views, page.Total, page.Offset, page.Limit, page.HasMore), contents, sections, legend));
    }
}
