#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// The connectivity around the pieces to swap, three ways. The game's own links now: SmallGrid.FillConnected of every
/// thing in the neighbourhood, kept where either end is a piece to swap. The model's links from the same things as
/// they are, which must equal the game's (else the model cannot be trusted here). The model's links with every
/// replacement in place (its cells and ends at its planned rotation), which must equal the game's links now. The
/// neighbourhood is every occupant of the cells the pieces and their replacements occupy, and of the cells each of
/// their ends sits in and faces; nothing else can link to them (Connectivity). Mounted devices (a fuse, an analyser, a
/// pipe meter) sit in a piece's own cell and hang on its forward axis, tested by the game (IsValidCable,
/// IsValidPipe); a replacement standing at exactly the old rotation keeps that axis bit for bit.
/// </summary>
internal sealed class LinkSurvey
{
    // Swaps read for mounted devices, and links counted, per step.
    private const int SwapsAStep = 32;

    private const int LinksAStep = 512;

    private readonly List<long> _unreadable;

    private LinkSurvey(HashSet<Link> gameBefore, SurveyGroups groups, LinkDiff modelCheck, LinkDiff change,
        int predictedCount, LinkSurveyParts parts)
    {
        GameBefore = gameBefore;
        GroupOf = groups.GroupOf;
        Removed = groups.Removed;
        Expected = Connectivity.Without(Connectivity.Grouped(gameBefore, groups.GroupOf), groups.Removed);
        ModelCheck = modelCheck;
        Change = change;
        PredictedCount = predictedCount;
        Mounted = parts.Mounted;
        Devices = parts.Devices;
        Things = parts.Things;
        _unreadable = parts.Unreadable;
    }

    internal HashSet<Link> GameBefore { get; }

    /// <summary>Every old piece's id to its swap's group id.</summary>
    internal Dictionary<long, long> GroupOf { get; }

    /// <summary>The group ids of removed pieces.</summary>
    internal HashSet<long> Removed { get; }

    /// <summary>
    /// The links there must be after the swap: the game's links now, counted by group, less every link of a removed
    /// piece.
    /// </summary>
    internal HashSet<Link> Expected { get; }

    internal LinkDiff ModelCheck { get; }

    internal LinkDiff Change { get; }

    internal int PredictedCount { get; }

    internal List<MountedRecord> Mounted { get; }

    internal List<DeviceRecord> Devices { get; }

    /// <summary>Every thing in the neighbourhood by id, for naming links.</summary>
    internal Dictionary<long, SmallGrid> Things { get; }

    /// <summary>
    /// The links around the swaps as the game has them and as the model predicts them, spread over frames (a Work body):
    /// a swap, a neighbourhood cell, a thing, a model or a device per step.
    /// </summary>
    internal static IEnumerable<object?> Taking(List<PlannedSwap> swaps, UpgradeFamily family,
        Answer<LinkSurvey> answer)
    {
        HashSet<long> focus = new HashSet<long>();
        Dictionary<long, PlannedSwap> byId = new Dictionary<long, PlannedSwap>();
        SurveyGroups groups = new SurveyGroups();
        List<PieceModel> reach = new List<PieceModel>(swaps.Count * 2);
        foreach (PlannedSwap swap in swaps)
        {
            groups.Add(swap);
            foreach (OldPiece old in swap.Olds)
            {
                focus.Add(old.Piece.ReferenceId);
                byId[old.Piece.ReferenceId] = swap;
                reach.Add(old.Live);
            }

            foreach (Twin part in swap.Parts)
            {
                reach.Add(part.Model);
            }

            yield return null;
        }

        Dictionary<long, SmallGrid> things = new Dictionary<long, SmallGrid>();
        foreach (object? step in NeighbourhoodSteps(reach, things))
        {
            yield return step;
        }

        List<PieceModel> live = new List<PieceModel>(things.Count);
        List<PieceModel> predicted = new List<PieceModel>(things.Count);
        foreach (SmallGrid thing in things.Values)
        {
            PlannedSwap? swap = byId.TryGetValue(thing.ReferenceId, out PlannedSwap found) ? found : null;
            live.Add(swap != null ? LiveOf(swap, thing) : PieceShapes.Live(thing));
            if (swap == null)
            {
                predicted.Add(live[live.Count - 1]);
            }
            else if (swap.GroupId == thing.ReferenceId)
            {
                // Every part carries the group id: links among them are no links, links out are the group's.
                foreach (Twin part in swap.Parts)
                {
                    predicted.Add(part.Model);
                }
            }

            yield return null;
        }

        List<long> unreadable = new List<long>();
        HashSet<Link> game = new HashSet<Link>();
        foreach (object? step in GameLinksSteps(things, focus, unreadable, game))
        {
            yield return step;
        }

        HashSet<Link> after = new HashSet<Link>();
        foreach (object? step in Connectivity.LinksTouchingSteps(predicted, focus, after))
        {
            yield return step;
        }

        HashSet<Link> liveLinks = new HashSet<Link>();
        foreach (object? step in Connectivity.LinksTouchingSteps(live, focus, liveLinks))
        {
            yield return step;
        }

        List<MountedRecord> mounted = new List<MountedRecord>();
        foreach (object? step in MountingOn(swaps, family, mounted))
        {
            yield return step;
        }

        LinkSurveyParts parts = new LinkSurveyParts(things, unreadable, mounted, new List<DeviceRecord>());
        // The link sets hold thousands on a long network: each pass over them is a step of its own.
        HashSet<Link> grouped = Connectivity.Grouped(game, groups.GroupOf);
        yield return null;
        HashSet<Link> expected = Connectivity.Without(grouped, groups.Removed);
        yield return null;
        LinkDiff modelCheck = Connectivity.Compare(game, liveLinks);
        yield return null;
        LinkDiff change = Connectivity.Compare(expected, after);
        yield return null;
        LinkSurvey survey = new LinkSurvey(game, groups, modelCheck, change, after.Count, parts);
        foreach (object? step in DevicesAround(things, game, survey.Mounted, family, survey.Devices))
        {
            yield return step;
        }

        answer.Value = survey;
    }

    [Profiled]
    private static PieceModel LiveOf(PlannedSwap swap, SmallGrid thing)
    {
        foreach (OldPiece old in swap.Olds)
        {
            if (old.Piece == thing)
            {
                return old.Live;
            }
        }

        return PieceShapes.Live(thing);
    }

    /// <summary>Every occupant of the models' cells and of their ends' own and facing cells.</summary>
    internal static Dictionary<long, SmallGrid> Neighbourhood(List<PieceModel> models)
    {
        Dictionary<long, SmallGrid> things = new Dictionary<long, SmallGrid>();
        foreach (object? _ in NeighbourhoodSteps(models, things))
        {
        }

        return things;
    }

    /// <summary>Neighbourhood spread over frames: a model's cells, then a cell's occupants, per step.</summary>
    internal static IEnumerable<object?> NeighbourhoodSteps(List<PieceModel> models, Dictionary<long, SmallGrid> things)
    {
        HashSet<GridCell> cells = new HashSet<GridCell>();
        foreach (PieceModel model in models)
        {
            foreach (GridCell cell in model.Cells)
            {
                cells.Add(cell);
            }

            foreach (PieceEnd end in model.Ends)
            {
                cells.Add(end.Local);
                cells.Add(end.Facing);
            }

            yield return null;
        }

        GridController world = GridController.World;
        foreach (GridCell cell in cells)
        {
            SmallCell? small = world.GetSmallCell(PieceShapes.Grid(cell));
            if (small != null)
            {
                AddOccupants(small, things);
            }

            yield return null;
        }
    }

    private static void AddOccupants(SmallCell cell, Dictionary<long, SmallGrid> things)
    {
        SmallGrid?[] occupants = { cell.Cable, cell.Pipe, cell.Device, cell.Chute, cell.Other, cell.Rail as SmallGrid };
        foreach (SmallGrid? occupant in occupants)
        {
            if (occupant != null && !occupant.IsBeingDestroyed)
            {
                things[occupant.ReferenceId] = occupant;
            }
        }
    }

    /// <summary>
    /// The game's links among the things that start or end at a focus id: SmallGrid.FillConnected's own loop (for
    /// each end, the cell its transform is in, each occupant slot of it other than the thing, that occupant's own
    /// IsConnected), made here with the game's IsConnected because FillConnected's Span buffer is typed against the
    /// game's mscorlib.
    /// </summary>
    [Profiled]
    internal static HashSet<Link> GameLinks(Dictionary<long, SmallGrid> things, HashSet<long> focus,
        List<long> unreadable)
    {
        HashSet<Link> links = new HashSet<Link>();
        foreach (object? _ in GameLinksSteps(things, focus, unreadable, links))
        {
        }

        return links;
    }

    /// <summary>GameLinks spread over frames (a Work body): one thing's ends per step, into links.</summary>
    internal static IEnumerable<object?> GameLinksSteps(Dictionary<long, SmallGrid> things, HashSet<long> focus,
        List<long> unreadable, HashSet<Link> links)
    {
        foreach (SmallGrid thing in things.Values)
        {
            try
            {
                AddGameLinks(thing, focus, links);
            }
            catch (NullReferenceException)
            {
                // Connection.Transform and IsConnected's own ends: a half-built or removed thing lacks them.
                unreadable.Add(thing.ReferenceId);
            }

            yield return null;
        }
    }

    private static void AddGameLinks(SmallGrid thing, HashSet<long> focus, HashSet<Link> links)
    {
        if (thing.OpenEnds == null)
        {
            return;
        }

        GridController world = GridController.World;
        bool fromFocus = focus.Contains(thing.ReferenceId);
        foreach (Connection end in thing.OpenEnds)
        {
            Grid3 grid = world.WorldToLocalGrid(end.Transform.position, SmallGrid.SmallGridSize,
                SmallGrid.SmallGridOffset);
            SmallCell? cell = world.GetSmallCell(grid);
            if (cell == null)
            {
                continue;
            }

            ISmallGrid?[] occupants = { cell.Cable, cell.Chute, cell.Device, cell.Pipe, cell.Rail, cell.Other };
            foreach (ISmallGrid? other in occupants)
            {
                if (other != null && other.ReferenceId != thing.ReferenceId &&
                    (fromFocus || focus.Contains(other.ReferenceId)) && other.IsConnected(end))
                {
                    links.Add(new Link(thing.ReferenceId, other.ReferenceId));
                }
            }
        }
    }

    // The devices mounted on the swapped pieces, a batch of swaps per step.
    private static IEnumerable<object?> MountingOn(List<PlannedSwap> swaps, UpgradeFamily family,
        List<MountedRecord> mounted)
    {
        GridController world = GridController.World;
        for (int index = 0; index < swaps.Count; index++)
        {
            PlannedSwap swap = swaps[index];
            foreach (OldPiece old in swap.Olds)
            {
                foreach (GridCell cell in old.Live.Cells)
                {
                    Device? device = world.GetSmallCell(PieceShapes.Grid(cell))?.Device;
                    if (device != null && family.IsMountedOn(device))
                    {
                        bool now = family.MountedNow(device, old.Piece);
                        int part = swap.PartIndexAt(cell);
                        bool keeps = part >= 0 && swap.Parts[part].KeepsRotation(old.Piece.ThingTransformRotation);
                        mounted.Add(new MountedRecord(device, swap, old.Piece, part, now, keeps ? now : (bool?)null));
                    }
                }
            }

            if (index % SwapsAStep == SwapsAStep - 1)
            {
                yield return null;
            }
        }

        yield return null;
    }

    // The devices among the things with a link (counted once over the links, not per device) or mounted on a swapped
    // piece; a device per step.
    private static IEnumerable<object?> DevicesAround(Dictionary<long, SmallGrid> things, HashSet<Link> links,
        List<MountedRecord> mounted, UpgradeFamily family, List<DeviceRecord> devices)
    {
        Dictionary<long, int> linkCounts = new Dictionary<long, int>();
        int counted = 0;
        foreach (Link link in links)
        {
            linkCounts[link.From] = linkCounts.TryGetValue(link.From, out int from) ? from + 1 : 1;
            if (link.To != link.From)
            {
                linkCounts[link.To] = linkCounts.TryGetValue(link.To, out int to) ? to + 1 : 1;
            }

            if (++counted % LinksAStep == 0)
            {
                yield return null;
            }
        }

        HashSet<long> mountedIds = new HashSet<long>();
        foreach (MountedRecord record in mounted)
        {
            mountedIds.Add(record.Device.ReferenceId);
        }

        yield return null;
        foreach (SmallGrid thing in things.Values)
        {
            if (thing is Device device)
            {
                int count = linkCounts.TryGetValue(device.ReferenceId, out int linked) ? linked : 0;
                bool isMounted = mountedIds.Contains(device.ReferenceId);
                if (count > 0 || isMounted)
                {
                    devices.Add(new DeviceRecord(device, count, isMounted, family.DeviceNetworks(device)));
                }

                yield return null;
            }
        }
    }

    [Profiled]
    internal void AddProblems(UpgradePlan plan)
    {
        foreach (long id in _unreadable)
        {
            plan.Problems.Add(new UpgradeProblemView("links_unreadable",
                $"The game's connections of {NameOf(id)} could not be read.", new ThingId(id)));
        }

        foreach (Link link in ModelCheck.Added)
        {
            plan.Problems.Add(Mismatch(link, "the model links them and the game does not"));
        }

        foreach (Link link in ModelCheck.Lost)
        {
            plan.Problems.Add(Mismatch(link, "the game links them and the model does not"));
        }

        foreach (Link link in Change.Added)
        {
            plan.Problems.Add(new UpgradeProblemView("link_added",
                $"After the swap {NameOf(link.From)} would connect to {NameOf(link.To)}, which it does not now.",
                new ThingId(link.From)));
        }

        foreach (Link link in Connectivity.Grouped(GameBefore, GroupOf))
        {
            bool cut = (Removed.Contains(link.From) && Things.TryGetValue(link.To, out SmallGrid to) && to is Device) ||
                       (Removed.Contains(link.To) && Things.TryGetValue(link.From, out SmallGrid from) &&
                        from is Device);
            if (cut)
            {
                plan.Problems.Add(new UpgradeProblemView("device_link_lost",
                    $"Removing a piece would cut {NameOf(link.From)} from {NameOf(link.To)}.", new ThingId(link.From)));
            }
        }

        foreach (Link link in Change.Lost)
        {
            plan.Problems.Add(new UpgradeProblemView("link_lost",
                $"After the swap {NameOf(link.From)} would no longer connect to {NameOf(link.To)}.",
                new ThingId(link.From)));
        }

        foreach (MountedRecord record in Mounted)
        {
            if (!record.After.HasValue)
            {
                plan.Problems.Add(new UpgradeProblemView("mounted_device_turned",
                    $"{record.Device.PrefabName} is mounted on {record.Piece.PrefabName}, whose replacement " +
                    "stands turned or is none; whether it stays attached cannot be guaranteed.",
                    new ThingId(record.Swap.Old.ReferenceId)));
            }
        }
    }

    private UpgradeProblemView Mismatch(Link link, string how) =>
        new UpgradeProblemView("connectivity_model_mismatch",
            $"{NameOf(link.From)} to {NameOf(link.To)}: {how}; the prediction cannot be trusted here.",
            new ThingId(link.From));

    internal string NameOf(long id) =>
        Things.TryGetValue(id, out SmallGrid thing) ? $"{thing.PrefabName} {id}" : id.ToString();

    internal UpgradeLinkView ViewOf(Link link) => new UpgradeLinkView(Named(link.From), Named(link.To));

    private ThingView Named(long id) =>
        Things.TryGetValue(id, out SmallGrid thing)
            ? GameLookup.ViewOf(thing)
            : new ThingView(new ThingId(id), null, null);

    internal UpgradeConnectivityView View()
    {
        List<UpgradeMountedView> mounted = new List<UpgradeMountedView>(Mounted.Count);
        foreach (MountedRecord record in Mounted)
        {
            mounted.Add(new UpgradeMountedView(new ThingId(record.Device.ReferenceId),
                new ThingId(record.Piece.ReferenceId), record.Before, record.After));
        }

        List<UpgradeDeviceView> devices = new List<UpgradeDeviceView>(Devices.Count);
        foreach (DeviceRecord record in Devices)
        {
            devices.Add(record.View());
        }

        List<UpgradeLinkView> differences = ViewsOf(ModelCheck.Added);
        differences.AddRange(ViewsOf(ModelCheck.Lost));
        return new UpgradeConnectivityView(new UpgradeLinkCounts(GameBefore.Count, PredictedCount),
            ViewsOf(Change.Added), ViewsOf(Change.Lost), differences, mounted, devices);
    }

    internal List<UpgradeLinkView> ViewsOf(List<Link> links)
    {
        List<UpgradeLinkView> views = new List<UpgradeLinkView>(links.Count);
        foreach (Link link in links)
        {
            views.Add(ViewOf(link));
        }

        return views;
    }
}

internal sealed class LinkSurveyParts
{
    internal LinkSurveyParts(Dictionary<long, SmallGrid> things, List<long> unreadable, List<MountedRecord> mounted,
        List<DeviceRecord> devices)
    {
        Things = things;
        Unreadable = unreadable;
        Mounted = mounted;
        Devices = devices;
    }

    internal Dictionary<long, SmallGrid> Things { get; }

    internal List<long> Unreadable { get; }

    internal List<MountedRecord> Mounted { get; }

    internal List<DeviceRecord> Devices { get; }
}

/// <summary>A device mounted in a piece's cell: attached now, and after the swap (null: cannot be predicted).</summary>
internal sealed class MountedRecord
{
    internal MountedRecord(Device device, PlannedSwap swap, SmallGrid piece, int part, bool before, bool? after)
    {
        Device = device;
        Swap = swap;
        Piece = piece;
        Part = part;
        Before = before;
        After = after;
    }

    internal Device Device { get; }

    internal PlannedSwap Swap { get; }

    /// <summary>The old piece the device is mounted on.</summary>
    internal SmallGrid Piece { get; }

    /// <summary>The index of the replacement standing in the device's cell; -1 when none does.</summary>
    internal int Part { get; }

    internal bool Before { get; }

    internal bool? After { get; }
}

/// <summary>A device next to or on a piece to swap, and the family's networks it is on now.</summary>
internal sealed class DeviceRecord
{
    internal DeviceRecord(Device device, int links, bool mounted, List<long> networks)
    {
        Device = device;
        Links = links;
        Mounted = mounted;
        Networks = networks;
    }

    internal Device Device { get; }

    internal int Links { get; }

    internal bool Mounted { get; }

    internal List<long> Networks { get; }

    internal UpgradeDeviceView View()
    {
        List<ThingId> ids = new List<ThingId>(Networks.Count);
        foreach (long id in Networks)
        {
            ids.Add(new ThingId(id));
        }

        return new UpgradeDeviceView(GameLookup.ViewOf(Device), Links, Mounted, ids);
    }
}

/// <summary>Which old piece belongs to which swap group, and which groups are removals.</summary>
internal sealed class SurveyGroups
{
    internal Dictionary<long, long> GroupOf { get; } = new Dictionary<long, long>();

    internal HashSet<long> Removed { get; } = new HashSet<long>();

    internal void Add(PlannedSwap swap)
    {
        foreach (OldPiece old in swap.Olds)
        {
            GroupOf[old.Piece.ReferenceId] = swap.GroupId;
        }

        if (swap.Removes)
        {
            Removed.Add(swap.GroupId);
        }
    }
}
