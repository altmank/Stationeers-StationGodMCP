#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey area (catalogue survey.json): the 2 m cells of a box or of a room, one page at a time, with occupied_only only
/// the cells that hold something; each cell by what occupies it, or with cell_detail full in full; then what stands in
/// the page's cells (AreaSurvey), sections keeping only the parts named and network, kind and prefab only those pieces
/// and devices. The first page scans the cells over frames (ScanBuild, a cell per step), keeps them (CellKey) and
/// describes only its own; with the wire cursor the rest stays under a cursor, whose later pages describe their cells as
/// they are then. What stands in a page's cells is gathered over frames too (AreaSurvey.Contents). Answered in its
/// declared shape (SurveyAreaReply). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the box's cells and the structures in them")]
internal static class SurveyArea
{
    private const string Refund = "refund";
    private const string PieceCells = "piece_cells";


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
        SurveyCellDetail detail = SurveyCellDetails.Of(options.CellDetail);
        bool withCells = SurveySections.Of(options.Sections).Includes(SurveySection.Cells);
        System.Func<long, KeptCell?> live = key => KeptCell.Of(facts, CellKey.Cell(key), withCells, detail);
        int limit = SurveySections.Of(options.Sections).PageLimit(paging.Limit);
        if (walk.Continues)
        {
            return Run(new Page(options, facts, FramedPage<KeptCell>.Later(walk, limit, live)));
        }

        List<GridCell> cells = selector.Box is { } box
            ? AreaSurvey.InBox(box.First, box.Second)
            : AreaSurvey.InRoom(selector.Room!);
        bool occupiedOnly = options.OccupiedOnly == true;
        IWalkBuild build = ScanBuild.InScanOrder(keys => AreaSurvey.Scan(facts, cells, occupiedOnly, keys));
        return Run(new Page(options, facts, FramedPage<KeptCell>.Scan(walk, paging.Offset, limit, build, live)));
    }

    private static VerbOutcome Run(Page page) =>
        page.Step(CurrentFrame.EndMs) ? VerbOutcome.Of(page.Result()) : new VerbOutcome.Pending(page);

    /// <summary>
    /// A survey's page over frames: its cells (scanned, then each described as it is taken), then what stands in them
    /// (AreaSurvey.Contents, a Work body), then the reply in its declared shape (SurveyAreaReply).
    /// </summary>
    private sealed class Page : IPendingResult
    {
        private readonly SurveyAreaOptions _options;
        private readonly GridFacts _facts;
        private readonly FramedPage<KeptCell> _page;
        private FramedResult<KeptCell>? _found;
        private Work<SurveyContents>? _contents;

        internal Page(SurveyAreaOptions options, GridFacts facts, FramedPage<KeptCell> page)
        {
            _options = options;
            _facts = facts;
            _page = page;
        }

        public bool Step(double untilMs)
        {
            if (_found == null)
            {
                if (!_page.Step(StopwatchClock.Instance, untilMs))
                {
                    return false;
                }

                _found = _page.Result();
                Selector selector = _options.Selector;
                SurveyFilter filter = new SurveyFilter(AreaSurvey.NetworkFilter(selector.Networks),
                    SurveyKinds.Of(_options.Kind), selector.Prefab);
                List<GridCell> shown = new List<GridCell>(_found.Page.Entries.Count);
                foreach (KeptCell cell in _found.Page.Entries)
                {
                    shown.Add(cell.Cell);
                }

                SurveySections sections = SurveySections.Of(_options.Sections);
                _contents = new Work<SurveyContents>(answer => AreaSurvey.Contents(_facts, shown, sections, filter,
                    _options.Networks ?? true, _options.Fields.Includes(Refund), _options.Fields.Includes(PieceCells),
                    answer));
            }

            return _contents!.Step(StopwatchClock.Instance, untilMs);
        }

        public object Result()
        {
            Page<KeptCell> page = _found!.Page;
            SurveySections sections = SurveySections.Of(_options.Sections);
            SurveyCellDetail detail = SurveyCellDetails.Of(_options.CellDetail);
            bool withCells = sections.Includes(SurveySection.Cells);
            List<SurveyCell> views = new List<SurveyCell>(withCells ? page.Entries.Count : 0);
            foreach (KeptCell cell in page.Entries)
            {
                if (cell.View != null)
                {
                    views.Add(cell.View);
                }
            }

            string? legend = !withCells ? null
                : detail == SurveyCellDetail.Full ? SurveyLegends.Full
                : SurveyLegends.Occupancy;
            return SurveyAreaReply.Of(new Page<SurveyCell>(views, page.Total, page.Offset, page.Limit, page.HasMore),
                _contents!.Value, sections, legend, _found.CursorId);
        }
    }

    /// <summary>A kept cell of a survey's page, with its view when the page lists cells (a walk's entries are classes).</summary>
    private sealed class KeptCell
    {
        private KeptCell(GridCell cell, SurveyCell? view)
        {
            Cell = cell;
            View = view;
        }

        internal GridCell Cell { get; }

        internal SurveyCell? View { get; }

        internal static KeptCell Of(GridFacts facts, GridCell cell, bool withView, SurveyCellDetail detail) =>
            new KeptCell(cell, withView ? AreaSurvey.CellView(facts, cell, detail) : null);
    }
}
