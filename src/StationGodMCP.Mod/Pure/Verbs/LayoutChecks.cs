#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Verbs;

/// <summary>The cells check layout lints: a room by its id, or the 2 m cells a box overlaps.</summary>
internal abstract class LayoutRegion
{
    private LayoutRegion()
    {
    }

    internal sealed class Room : LayoutRegion
    {
        internal Room(long id)
        {
            Id = id;
        }

        public long Id { get; }
    }

    internal sealed class Box : LayoutRegion
    {
        internal Box(BoxCorners corners)
        {
            Corners = corners;
        }

        public BoxCorners Corners { get; }

        public string Described => $"box {Corners.First} to {Corners.Second}";
    }
}

/// <summary>
/// check layout's call, checked without the game: the region (room or box, one of them; a box at most
/// MaximumCells 2 m cells), the codes kept and left out, the things the findings are narrowed to (id, at most
/// MaximumIds, and since), and how many findings are listed.
/// </summary>
internal sealed class LayoutCheck
{
    internal const long MaximumCells = 4000;
    internal const int MaximumIds = 256;

    private LayoutCheck(LayoutRegion region, LintCodeFilter codes, LintThingFilter things, int? limit)
    {
        Region = region;
        Codes = codes;
        Things = things;
        Limit = limit;
    }

    public LayoutRegion Region { get; }

    public LintCodeFilter Codes { get; }

    public LintThingFilter Things { get; }

    /// <summary>Findings listed; null lists every one.</summary>
    public int? Limit { get; }

    /// <summary>The call's check, or null with every problem added.</summary>
    internal static LayoutCheck? Of(CheckLayoutOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        LayoutRegion? region = RegionOf(options.Selector, problems);
        List<string>? codes = CodesOf(options.Codes, "codes", problems);
        List<string>? excluded = CodesOf(options.ExcludeCodes, "exclude_codes", problems);
        List<long>? ids = Ids(options.Selector.Ids, problems);
        long? since = null;
        if (options.Since != null)
        {
            since = IdOf(options.Since);
            if (since == null)
            {
                problems.Add(new OptionProblem("since", "is a thing's id"));
            }
        }

        return problems.Count > before || region == null
            ? null
            : new LayoutCheck(region, LintCodeFilter.Of(codes, excluded), LintThingFilter.Of(ids, since),
                options.Paging.Limit);
    }

    private static LayoutRegion? RegionOf(Selector selector, List<OptionProblem> problems)
    {
        if (selector.Room != null && selector.Box != null)
        {
            problems.Add(new OptionProblem("room", "goes without box"));
            return null;
        }

        if (selector.Room is { } room)
        {
            if (IdOf(room) is { } id)
            {
                return new LayoutRegion.Room(id);
            }

            problems.Add(new OptionProblem("room", "is a room's id, as find room lists it"));
            return null;
        }

        if (selector.Box is { } box)
        {
            long cells = LargeCells.CountInBox(box.First, box.Second);
            if (cells <= MaximumCells)
            {
                return new LayoutRegion.Box(box);
            }

            problems.Add(new OptionProblem("box",
                $"holds {cells.ToString(CultureInfo.InvariantCulture)} 2 m cells; at most {MaximumCells}"));
            return null;
        }

        problems.Add(new OptionProblem("room", "or box is needed: the cells to check"));
        return null;
    }

    private static List<string>? CodesOf(IReadOnlyList<string>? given, string option, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return null;
        }

        List<string> codes = new List<string>(given.Count);
        for (int index = 0; index < given.Count; index++)
        {
            string code = given[index].Trim();
            if (code.Length == 0)
            {
                problems.Add(new OptionProblem($"{option}[{index}]", "is a lint code, as counts names them"));
            }
            else
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    private static List<long>? Ids(PerIdList? given, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return null;
        }

        if (given.Ids.Count > MaximumIds)
        {
            problems.Add(new OptionProblem("id", $"narrows to at most {MaximumIds} things"));
            return null;
        }

        List<long> ids = new List<long>(given.Ids.Count);
        for (int index = 0; index < given.Ids.Count; index++)
        {
            if (IdOf(given.Ids[index]) is { } id)
            {
                ids.Add(id);
            }
            else
            {
                problems.Add(new OptionProblem(given.GivenAsList ? $"id[{index}]" : "id", "is a thing's id"));
            }
        }

        return ids;
    }

    private static long? IdOf(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0
            ? id
            : null;
}
