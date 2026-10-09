#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api;

/// <summary>
/// lint_layout: the audit rules of the effective rule set (lint-rules.json of the mod, with the save's file over it)
/// over a room (room_id) or a box (min, max), from what stands there now; codes and exclude_codes (LintCodeFilter) and
/// reference_ids and since_id (LintThingFilter) pick the findings listed, counts still counts them all. Read only.
/// </summary>
internal static class LintLayoutApi
{
    private const int DefaultLimit = ReplyDefaults.LintFindings;
    private const int MaximumLimit = 500;
    private const long MaximumCells = 4000;

    internal static LintLayoutView Handle(Args args)
    {
        List<GridCell> region = Region(args, out string described);
        int limit = args.OptionalInt("limit", 1, MaximumLimit) ?? DefaultLimit;
        LintCodeFilter filter = LintCodeFilter.Of(
            args.Has("codes") ? Codes(args.Array("codes", MaximumCodes), "codes") : null,
            args.Has("exclude_codes") ? Codes(args.Array("exclude_codes", MaximumCodes), "exclude_codes") : null);
        LintThingFilter things = LintThingFilter.Of(
            args.Has("reference_ids") ? args.ThingIds("reference_ids", MaximumIds).ConvertAll(static id => id.Value) : null,
            args.OptionalThingId("since_id")?.Value);
        LintRuleSet rules = LintRuleFiles.Current();
        GameLintWorld world = GameLintWorld.Audit(region);
        LintRun run = LintEngine.Run(rules, world, "audit");
        List<LintFinding> ordered = things.Keep(filter.Keep(LintReport.Ordered(run.Findings)));
        List<LintFindingView> views = new List<LintFindingView>();
        for (int index = 0; index < ordered.Count && index < limit; index++)
        {
            views.Add(new LintFindingView(ordered[index]));
        }

        Pure.Shaping.Truncations.Capped("findings", views.Count, ordered.Count, "limit", MaximumLimit);
        return new LintLayoutView(described, region.Count, world.Subjects("pieces").Count,
            world.Subjects("devices").Count, world.Subjects("structures").Count, world.Doors,
            LintReport.Counts(run.Findings), views, ordered.Count, new LintRuleSourceView(rules), run.Milliseconds,
            filter.Narrows || things.Narrows ? run.Findings.Count - ordered.Count : null);
    }

    private const int MaximumCodes = 64;
    private const int MaximumIds = 256;

    private static List<string> Codes(JArray array, string name)
    {
        List<string> codes = new List<string>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            string? code = array[index].Type == JTokenType.String ? array[index].Value<string>()?.Trim() : null;
            codes.Add(string.IsNullOrEmpty(code)
                ? throw ApiErrors.InvalidArgument($"{name}[{index}] must be a lint code (counts names them).")
                : code!);
        }

        return codes;
    }

    internal static List<GridCell> Region(Args args, out string described)
    {
        if (args.Has("room_id") == (args.Has("min") || args.Has("max")))
        {
            throw ApiErrors.InvalidArgument("Pass room_id (from rooms) or min and max (a box, in metres).");
        }

        if (args.Has("room_id"))
        {
            if (!ThingId.TryRead(args.Optional("room_id"), out ThingId id))
            {
                throw ApiErrors.InvalidArgument("room_id must be a room id as rooms reports it.");
            }

            Room room = StructureAirRecord.FindRoom(id.Value) ??
                        throw ApiErrors.Refused("room_not_found", $"No room has id {id}.");
            List<GridCell> cells = new List<GridCell>();
            foreach (WorldGrid grid in new List<WorldGrid>(room.Grids))
            {
                cells.Add(PieceShapes.Cell(grid.Value));
            }

            described = $"room {id} ({cells.Count} cells)";
            return cells;
        }

        Vec3 min = PointOf(args.Optional("min")!, "min");
        Vec3 max = PointOf(args.Optional("max") ?? throw ApiErrors.InvalidArgument("Pass max too."), "max");
        long count = LargeCells.CountInBox(min, max);
        if (count > MaximumCells)
        {
            throw ApiErrors.InvalidArgument($"The box holds {count} 2 m cells; at most {MaximumCells}.");
        }

        described = $"box {min} to {max}";
        return LargeCells.InBox(min, max);
    }

    private static Vec3 PointOf(JToken token, string name)
    {
        Metres point = BuildArgs.PositionOf(token, name);
        return new Vec3(point.X, point.Y, point.Z);
    }
}
