#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find damaged (catalogue find.json): every damaged or broken thing in the world (HealthScanner), broken first, then
/// worst first, kept by min_damage (a ratio below 1), broken_only, structures_only and near "player" with radius, paged,
/// with the scan's counts (structures, broken, scanned); with the wire cursor a walk (ThingListPage), whose later pages
/// read each thing's damage again and answer the first page's scan counts. A thing's
/// networks are read only when the call's fields keep things' networks. Read only.
/// </summary>
internal static class FindDamaged
{
    private const string List = "things";

    private static readonly EntryCost Cost = new EntryCost();

    internal static VerbOutcome Run(FindDamagedOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        double minimum = options.MinDamage ?? 0.0;
        bool withNetworks = (options.Fields.Shape ?? ShapeRequest.None).Wants(List, "networks");
        WalkRequest walk = WalkRequest.Of(paging);
        if (walk.Continues)
        {
            PlayerOrigin now = PlayerOrigin.Current();
            return ThingListPage<HealthView, DamageCounts>.Later(walk, paging,
                id => Thing.Find(id) is { IsBeingDestroyed: false } thing ? HealthReader.Read(thing, now, withNetworks) : null,
                Cost, (page, cursorId, counts) => Reply(page, cursorId, counts ?? DamageCounts.None, minimum, now));
        }

        List<OptionProblem> problems = selector.PlayerRadiusProblems();
        if (minimum >= 1.0)
        {
            problems.Add(new OptionProblem("min_damage", "is a damage ratio, below 1"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindDamagedOptions.Spec.Label, problems));
        }

        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(selector.AroundPlayerM.HasValue);
        HealthScanner scanner = new HealthScanner(new HealthScanFilter(minimum, options.StructuresOnly == true,
            options.BrokenOnly == true, selector.AroundPlayerM), origin);
        return ThingListPage<HealthView, DamageCounts>.Scan(walk, paging, new ScanBuild(scanner.Scan, scanner.WorstFirst),
            () => new DamageCounts(scanner.Structures, scanner.Broken, scanner.Scanned),
            id => scanner.Listed(id) is { IsBeingDestroyed: false } thing
                ? HealthReader.Read(thing, origin, withNetworks)
                : null,
            Cost, (page, cursorId, counts) => Reply(page, cursorId, counts!, minimum, origin));
    }

    private static ReplyObject Reply(Page<HealthView> page, string? cursorId, DamageCounts counts, double minimum,
        PlayerOrigin origin) =>
        ThingListReply.Of(List, page, cursorId, ("structures", counts.Structures), ("broken", counts.Broken),
            ("scanned", counts.Scanned), ("min_damage_ratio", minimum), ("local_player", origin.View));

    /// <summary>The scan's counts a walk's every page answers.</summary>
    internal sealed class DamageCounts
    {
        internal DamageCounts(int structures, int broken, int scanned)
        {
            Structures = structures;
            Broken = broken;
            Scanned = scanned;
        }

        public int Structures { get; }

        public int Broken { get; }

        public int Scanned { get; }

        internal static DamageCounts None { get; } = new DamageCounts(0, 0, 0);
    }
}
