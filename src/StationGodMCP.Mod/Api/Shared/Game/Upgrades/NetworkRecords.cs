#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// One network a piece to swap is in, as it is before the swap and as the swap would leave it; after the swap, the
/// same network read again and checked against what was recorded.
/// </summary>
internal abstract class NetworkRecord
{
    protected NetworkRecord(ThingId id, List<PlannedSwap> swaps, List<Device> devices, List<SmallGrid> members)
    {
        Id = id;
        Swaps = swaps;
        DevicesBefore = IdsOf(devices);
        Staying = members.FindAll(member => !IsSwapped(member, out _));
    }

    internal ThingId Id { get; }

    internal List<PlannedSwap> Swaps { get; }

    protected HashSet<long> DevicesBefore { get; }

    /// <summary>The members no swap takes, as recorded before the swap.</summary>
    private List<SmallGrid> Staying { get; }

    /// <summary>
    /// The network now: the recorded one while the game still lists it; once it no longer does, the network the swap's
    /// replacements and the members it left in place are on, when the game lists that one (a part that registered
    /// with no connected neighbour got a network of its own, and the part linking it to the old one merged the old
    /// one into it: the game renumbered the network). Null when neither is found.
    /// </summary>
    protected IReferencable? Survivor(Func<long, IReferencable?> find, Func<SmallGrid, IReferencable?> networkOf,
        Dictionary<long, List<SmallGrid>> replacements)
    {
        IReferencable? now = find(Id.Value);
        if (now != null)
        {
            return now;
        }

        List<SmallGrid> standing = new List<SmallGrid>(Staying);
        foreach (PlannedSwap swap in Swaps)
        {
            if (replacements.TryGetValue(swap.GroupId, out List<SmallGrid> built))
            {
                standing.AddRange(built);
            }
        }

        foreach (SmallGrid member in standing)
        {
            IReferencable? network = member != null && !member.IsBeingDestroyed ? networkOf(member) : null;
            if (network != null && ReferenceEquals(find(network.ReferenceId), network))
            {
                return network;
            }
        }

        return null;
    }

    /// <summary>The recorded id when the network now has another; null when it kept its id.</summary>
    protected ThingId? RenumberedFrom(IReferencable now) => now.ReferenceId != Id.Value ? Id : null;

    /// <summary>
    /// How many more members the network holds after the swap: a split adds its singles less the long piece, a merge
    /// takes its singles less the long pieces, a removal takes its piece.
    /// </summary>
    protected int AddedMembers
    {
        get
        {
            int added = 0;
            foreach (PlannedSwap swap in Swaps)
            {
                added += swap.Parts.Count - swap.Olds.Count;
            }

            return added;
        }
    }

    // Only the planned additions and removals: any other count means part of the network split off or another joined.
    protected void VerifyCount(List<UpgradeProblemView> problems, string what, int before, int now)
    {
        int expected = before + AddedMembers;
        if (now != expected)
        {
            problems.Add(new UpgradeProblemView("member_count_changed",
                $"Network {Id} held {before} {what} before the swap, {expected} were expected after it and it holds " +
                $"{now}; part of it may have split off or another network joined.", Id));
        }
    }

    /// <summary>The report before the swap, with the predicted state after it.</summary>
    internal abstract object Report();

    internal abstract void AddProblems(UpgradePlan plan);

    /// <summary>The same report read again now; null when the network is gone.</summary>
    internal abstract object? ReportNow();

    internal abstract void Verify(List<UpgradeProblemView> problems, Dictionary<long, List<SmallGrid>> replacements);

    /// <summary>Groups the planned swaps by network and records each network.</summary>
    internal static void RecordAll(UpgradePlan plan)
    {
        UpgradeFamily family = plan.Request.Family;
        Dictionary<long, List<PlannedSwap>> grouped = new Dictionary<long, List<PlannedSwap>>();
        Dictionary<long, IReferencable> networks = new Dictionary<long, IReferencable>();
        foreach (PlannedSwap swap in plan.Swaps)
        {
            IReferencable? network = family.NetworkOf(swap.Old);
            if (network == null)
            {
                plan.Problem("no_network", $"{swap.Old.PrefabName} is in no {family.NetworkKind} network.", swap.Old);
                continue;
            }

            if (!grouped.TryGetValue(network.ReferenceId, out List<PlannedSwap> group))
            {
                group = new List<PlannedSwap>();
                grouped[network.ReferenceId] = group;
                networks[network.ReferenceId] = network;
            }

            group.Add(swap);
        }

        foreach (KeyValuePair<long, List<PlannedSwap>> entry in grouped)
        {
            NetworkRecord record = family.Record(networks[entry.Key], entry.Value);
            record.AddProblems(plan);
            plan.Networks.Add(record);
        }
    }

    protected static HashSet<long> IdsOf(List<Device> devices)
    {
        HashSet<long> ids = new HashSet<long>();
        lock (devices)
        {
            foreach (Device device in devices)
            {
                if (device != null)
                {
                    ids.Add(device.ReferenceId);
                }
            }
        }

        return ids;
    }

    protected static List<ThingView> ViewsOf(List<Device> devices)
    {
        List<Device> copy;
        lock (devices)
        {
            copy = new List<Device>(devices);
        }

        List<ThingView> views = new List<ThingView>(copy.Count);
        foreach (Device device in copy)
        {
            if (device != null)
            {
                views.Add(GameLookup.ViewOf(device));
            }
        }

        return views;
    }

    protected bool IsSwapped(Thing member, out PlannedSwap? swap)
    {
        foreach (PlannedSwap planned in Swaps)
        {
            if (planned.Takes(member))
            {
                swap = planned;
                return true;
            }
        }

        swap = null;
        return false;
    }

    // The same devices on the network, and every replacement of this network's pieces in it.
    protected void VerifyMembers(List<UpgradeProblemView> problems, List<Device> devicesNow,
        Dictionary<long, List<SmallGrid>> replacements, Func<SmallGrid, IReferencable?> networkOf, IReferencable now)
    {
        HashSet<long> after = IdsOf(devicesNow);
        foreach (long id in DevicesBefore)
        {
            if (!after.Contains(id))
            {
                problems.Add(new UpgradeProblemView("device_left_network",
                    $"Device {id} was on network {Id} before the swap and is not now.", new ThingId(id)));
            }
        }

        foreach (long id in after)
        {
            if (!DevicesBefore.Contains(id))
            {
                problems.Add(new UpgradeProblemView("device_joined_network",
                    $"Device {id} is on network {Id} now and was not before the swap.", new ThingId(id)));
            }
        }

        foreach (PlannedSwap swap in Swaps)
        {
            if (!replacements.TryGetValue(swap.GroupId, out List<SmallGrid> built))
            {
                continue;
            }

            foreach (SmallGrid replacement in built)
            {
                if (replacement != null && networkOf(replacement) != now)
                {
                    problems.Add(new UpgradeProblemView("network_changed",
                        $"A replacement of {swap.GroupId} is not in network {now.ReferenceId}.",
                        new ThingId(replacement.ReferenceId)));
                }
            }
        }
    }

    protected ApiException Gone() => new ApiException("network_gone", $"Network {Id} no longer exists.");

    // A network whose every member is removed is gone after the swap, as planned.
    protected void Missing(List<UpgradeProblemView> problems, int membersBefore)
    {
        if (membersBefore + AddedMembers > 0)
        {
            ApiException gone = Gone();
            problems.Add(new UpgradeProblemView(gone.Code, gone.Message, Id));
        }
    }
}

/// <summary>
/// A cable network: its loads, its weakest cable before and after, its fuses and devices, and how many cables it
/// holds (VerifyCount).
/// </summary>
internal sealed class CableNetworkRecord : NetworkRecord
{
    private readonly CableNetwork _network;
    private readonly int _cablesBefore;

    // The weakest cable when the plan was made: the job's report after the swap still says what it was before.
    private readonly double? _lowestBefore;

    private CableNetwork? _now;

    internal CableNetworkRecord(CableNetwork network, List<PlannedSwap> swaps)
        : base(new ThingId(network.ReferenceId), swaps, network.DeviceList,
            Copy(network.CableList).ConvertAll(static cable => (SmallGrid)cable))
    {
        _network = network;
        List<Cable> cables = Copy(network.CableList);
        _cablesBefore = cables.Count;
        foreach (Cable cable in cables)
        {
            _lowestBefore = _lowestBefore.HasValue
                ? Math.Min(_lowestBefore.Value, cable.MaxVoltage)
                : cable.MaxVoltage;
        }
    }

    internal override object Report() => ReportOf(_network, true);

    private CableNetworkReportView ReportOf(CableNetwork network, bool predict)
    {
        List<Cable> cables = Copy(network.CableList);
        double? before = null;
        double? after = null;
        foreach (Cable cable in cables)
        {
            double rating = cable.MaxVoltage;
            before = before.HasValue ? Math.Min(before.Value, rating) : rating;
            PlannedSwap? swap = null;
            if (predict && IsSwapped(cable, out swap) && swap!.First == null)
            {
                continue;
            }

            double future = swap?.First != null ? RatingOf(swap.First.Prefab) : rating;
            after = after.HasValue ? Math.Min(after.Value, future) : future;
        }

        double? fuse = null;
        foreach (CableFuse each in Copy(network.FuseList))
        {
            fuse = fuse.HasValue ? Math.Min(fuse.Value, each.PowerBreak) : each.PowerBreak;
        }

        return new CableNetworkReportView(new ThingId(network.ReferenceId),
            new CableNetworkCounts(cables.Count, Swaps.Count, network.FuseList.Count),
            new CableNetworkRatings(network.RequiredLoad, network.PotentialLoad, predict ? before : _lowestBefore,
                after, fuse),
            ViewsOf(network.DeviceList), predict ? null : RenumberedFrom(network));
    }

    private static double RatingOf(Structure prefab) => prefab is Cable cable ? cable.MaxVoltage : 0.0;

    private static List<T> Copy<T>(List<T> list) where T : UnityEngine.Object
    {
        List<T> copy;
        lock (list)
        {
            copy = new List<T>(list);
        }

        copy.RemoveAll(static item => item == null);
        return copy;
    }

    // Cable networks hold no state of their own beyond membership: nothing to refuse here.
    internal override void AddProblems(UpgradePlan plan)
    {
    }

    internal override object? ReportNow()
    {
        CableNetwork? now = _now ?? Referencable.Find<CableNetwork>(Id.Value);
        return now != null ? ReportOf(now, false) : null;
    }

    internal override void Verify(List<UpgradeProblemView> problems, Dictionary<long, List<SmallGrid>> replacements)
    {
        CableNetwork? now = Survivor(static id => Referencable.Find<CableNetwork>(id),
            static piece => piece is Cable cable ? cable.CableNetwork : null, replacements) as CableNetwork;
        if (now == null)
        {
            Missing(problems, _cablesBefore);
            return;
        }

        _now = now;

        VerifyMembers(problems, now.DeviceList, replacements,
            static piece => piece is Cable cable ? cable.CableNetwork : null, now);
        VerifyCount(problems, "cables", _cablesBefore, Copy(now.CableList).Count);
    }
}

/// <summary>
/// A pipe network: its contents, volume and pressure before, and after as the same contents in the new volume, the
/// lowest pipe rating after, and its devices. The contents stay in the network's own Atmosphere through the swap:
/// the replacement joins the network before the old piece leaves it (AtmosphericsNetwork.Add adds the new piece's
/// volume, Remove takes the old one's), so the network never empties and never splits, and the old piece's
/// Pipe.OnDestroy, finding it in no network, divides nothing.
/// </summary>
internal sealed class PipeNetworkRecord : NetworkRecord
{
    private const double VolumeToleranceL = 0.01;
    private const double RelativeTolerance = 1e-6;

    private readonly PipeNetwork _network;
    private readonly Atmosphere? _atmosphere;
    private readonly GasSnapshot? _before;
    private readonly double _volumeAfter;
    private readonly int _membersBefore;

    private PipeNetwork? _now;

    internal PipeNetworkRecord(PipeNetwork network, List<PlannedSwap> swaps)
        : base(new ThingId(network.ReferenceId), swaps, network.DeviceList, MembersOf(network))
    {
        _network = network;
        _membersBefore = MembersOf(network).Count;
        _atmosphere = network.Atmosphere;
        _before = _atmosphere != null ? GasSnapshot.Of(_atmosphere) : null;
        double change = 0.0;
        foreach (PlannedSwap swap in swaps)
        {
            foreach (OldPiece old in swap.Olds)
            {
                change -= PipeFamily.VolumeOf(old.Piece);
            }

            foreach (Twin part in swap.Parts)
            {
                change += PipeFamily.VolumeOf(part.Prefab);
            }
        }

        _volumeAfter = (_before?.VolumeL ?? 0.0) + change;
    }

    /// <summary>Every member is removed: the network and its atmosphere go with them.</summary>
    private bool Emptied => _membersBefore + AddedMembers <= 0;

    private double? LowestRatingAfter()
    {
        double? lowest = null;
        foreach (SmallGrid member in MembersOf(_network))
        {
            if (!(member is Pipe pipe))
            {
                continue;
            }

            PlannedSwap? swap = null;
            if (IsSwapped(member, out swap) && swap!.Removes)
            {
                continue;
            }

            double rating = swap != null ? LowestRatingOf(swap) : pipe.MaxPressure.ToDouble();
            lowest = lowest.HasValue ? Math.Min(lowest.Value, rating) : rating;
        }

        return lowest;
    }

    private static double LowestRatingOf(PlannedSwap swap)
    {
        double lowest = double.MaxValue;
        foreach (Twin part in swap.Parts)
        {
            lowest = Math.Min(lowest, part.Prefab is Pipe pipe ? pipe.MaxPressure.ToDouble() : 0.0);
        }

        return lowest;
    }

    private static List<SmallGrid> MembersOf(PipeNetwork network)
    {
        List<INetworkedStructure> members;
        lock (network.StructureList)
        {
            members = new List<INetworkedStructure>(network.StructureList);
        }

        List<SmallGrid> pieces = new List<SmallGrid>(members.Count);
        foreach (INetworkedStructure member in members)
        {
            if (member != null && member.GetAsThing is SmallGrid grid && grid != null)
            {
                pieces.Add(grid);
            }
        }

        return pieces;
    }

    internal override object Report()
    {
        PipeNetworkAir before = AirOf(_before, _before?.VolumeL ?? 0.0);
        PipeNetworkAir after = Emptied
            ? new PipeNetworkAir(0.0, 0.0, 0.0, 0.0, 0.0)
            : AirOf(_before?.WithVolume(_volumeAfter), _volumeAfter);
        return new PipeNetworkReportView(Id, _network.NetworkContentType.ToString(),
            new PipeNetworkCounts(MembersOf(_network).Count, Swaps.Count), before, after, LowestRatingAfter(),
            ViewsOf(_network.DeviceList));
    }

    private static PipeNetworkAir AirOf(GasSnapshot? snapshot, double volumeL) =>
        snapshot == null
            ? new PipeNetworkAir(0.0, 0.0, 0.0, volumeL, 0.0)
            : new PipeNetworkAir(snapshot.TotalMol(), snapshot.EnergyJ(), snapshot.TemperatureK(), volumeL,
                snapshot.PressureKpa());

    internal override void AddProblems(UpgradePlan plan)
    {
        if (_atmosphere == null || _before == null)
        {
            plan.Problems.Add(new UpgradeProblemView("no_atmosphere",
                $"Pipe network {Id} has no atmosphere here, so its contents cannot be carried over.", Id));
            return;
        }

        // A game event (AtmosphericEventInstance, e.g. a pump's or the console's) marks the atmosphere; a queued
        // move_gas does not, so its queue is asked too.
        if (_atmosphere.IsAwaitingEvent || GasMoves.Touches(_atmosphere))
        {
            plan.Problems.Add(new UpgradeProblemView("atmosphere_busy",
                $"Pipe network {Id} has a gas change waiting for the next atmospherics tick (a game event or a "
                + "queued move_gas); try again.", Id));
        }

        foreach (PlannedSwap swap in Swaps)
        {
            if (swap.First?.Prefab is Pipe pipe && pipe.PipeContentType != _network.NetworkContentType)
            {
                plan.Problem("content_mismatch",
                    $"{pipe.PrefabName} carries {pipe.PipeContentType}; network {Id} carries " +
                    $"{_network.NetworkContentType}.", swap.Old);
            }
        }

        if (Emptied)
        {
            return;
        }

        double pressure = _before.WithVolume(_volumeAfter).PressureKpa();
        double? lowest = LowestRatingAfter();
        if (lowest.HasValue && pressure > lowest.Value)
        {
            plan.Problems.Add(new UpgradeProblemView("would_burst",
                $"Pipe network {Id} would be at {pressure:0.#} kPa with a pipe rated {lowest.Value:0.#} kPa.", Id));
        }
    }

    internal override object? ReportNow()
    {
        PipeNetwork? now = _now ?? Referencable.Find<PipeNetwork>(Id.Value);
        if (now == null)
        {
            return null;
        }

        GasSnapshot? snapshot = now.Atmosphere != null ? GasSnapshot.Of(now.Atmosphere) : null;
        PipeNetworkAir air = AirOf(snapshot, snapshot?.VolumeL ?? 0.0);
        return new PipeNetworkReportView(new ThingId(now.ReferenceId), now.NetworkContentType.ToString(),
            new PipeNetworkCounts(MembersOf(now).Count, Swaps.Count), air, air, null, ViewsOf(now.DeviceList),
            RenumberedFrom(now));
    }

    internal override void Verify(List<UpgradeProblemView> problems, Dictionary<long, List<SmallGrid>> replacements)
    {
        PipeNetwork? now = Survivor(static id => Referencable.Find<PipeNetwork>(id),
            static piece => piece is Pipe pipe ? pipe.PipeNetwork : null, replacements) as PipeNetwork;
        if (now == null)
        {
            Missing(problems, _membersBefore);
            return;
        }

        _now = now;

        VerifyMembers(problems, now.DeviceList, replacements,
            static piece => piece is Pipe pipe ? pipe.PipeNetwork : null, now);
        VerifyCount(problems, "members", _membersBefore, MembersOf(now).Count);
        VerifyContents(problems, now);
    }

    // A renumbered network holds the old one's contents in its own Atmosphere (the game's merge moved them there):
    // the same moles, energy and predicted volume, in another object.
    private void VerifyContents(List<UpgradeProblemView> problems, PipeNetwork now)
    {
        if (_before == null || now.Atmosphere == null ||
            (ReferenceEquals(now, _network) && !ReferenceEquals(now.Atmosphere, _atmosphere)))
        {
            problems.Add(new UpgradeProblemView("atmosphere_replaced",
                $"Pipe network {Id} has a different atmosphere than before the swap.", Id));
            return;
        }

        GasSnapshot after = GasSnapshot.Of(now.Atmosphere);
        if (Math.Abs(after.VolumeL - _volumeAfter) > VolumeToleranceL)
        {
            problems.Add(new UpgradeProblemView("volume_differs",
                $"Pipe network {Id} holds {after.VolumeL:0.###} L; {_volumeAfter:0.###} L was predicted.", Id));
        }

        if (!Close(after.TotalMol(), _before.TotalMol()) || !Close(after.EnergyJ(), _before.EnergyJ()))
        {
            problems.Add(new UpgradeProblemView("contents_differ",
                $"Pipe network {Id} holds {after.TotalMol():0.######} mol and {after.EnergyJ():0.#} J; it held " +
                $"{_before.TotalMol():0.######} mol and {_before.EnergyJ():0.#} J.", Id));
        }
    }

    private static bool Close(double a, double b) =>
        Math.Abs(a - b) <= RelativeTolerance * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
}

/// <summary>
/// A chute network: how many pieces it holds, how many the run removes or replaces, how many items ride in it, and its
/// devices. The game rebuilds a chute network from each neighbour of a removed piece (Chute.OnDestroy), so it takes a
/// new id after the run and a removal may leave it in parts; what the run must keep is checked by the links, so after
/// the run only each replacement's own network is.
/// </summary>
internal sealed class ChuteNetworkRecord : NetworkRecord
{
    private readonly ChuteNetwork _network;
    private readonly int _piecesBefore;
    private ChuteNetwork? _now;

    internal ChuteNetworkRecord(ChuteNetwork network, List<PlannedSwap> swaps)
        : base(new ThingId(network.ReferenceId), swaps, network.DeviceList, MembersOf(network))
    {
        _network = network;
        _piecesBefore = MembersOf(network).Count;
    }

    internal override object Report() => ReportOf(_network, false);

    private ChuteNetworkReportView ReportOf(ChuteNetwork network, bool after)
    {
        List<SmallGrid> pieces = MembersOf(network);
        int riding = 0;
        foreach (SmallGrid piece in pieces)
        {
            if (ChuteFamily.ItemIn(piece) != null)
            {
                riding++;
            }
        }

        int removed = 0;
        foreach (PlannedSwap swap in Swaps)
        {
            removed += swap.Removes ? swap.Olds.Count : 0;
        }

        return new ChuteNetworkReportView(new ThingId(network.ReferenceId),
            new ChuteNetworkCounts(pieces.Count, removed, Swaps.Count - removed, riding), ViewsOf(network.DeviceList),
            after ? RenumberedFrom(network) : null);
    }

    // A chute network holds nothing of its own (items ride in the pieces): nothing to refuse here.
    internal override void AddProblems(UpgradePlan plan)
    {
    }

    internal override object? ReportNow() => _now != null ? ReportOf(_now, true) : null;

    internal override void Verify(List<UpgradeProblemView> problems, Dictionary<long, List<SmallGrid>> replacements)
    {
        _now = Survivor(static id => Referencable.Find<ChuteNetwork>(id), NetworkOf, replacements) as ChuteNetwork;
        foreach (PlannedSwap swap in Swaps)
        {
            if (!replacements.TryGetValue(swap.GroupId, out List<SmallGrid> built))
            {
                continue;
            }

            foreach (SmallGrid replacement in built)
            {
                if (replacement != null && NetworkOf(replacement) == null)
                {
                    problems.Add(new UpgradeProblemView("network_changed",
                        $"The replacement of {swap.GroupId} is on no chute network.",
                        new ThingId(replacement.ReferenceId)));
                }
            }
        }

        if (_now == null && _piecesBefore + AddedMembers > 0 && AnyReplacement(replacements))
        {
            Missing(problems, _piecesBefore);
        }
    }

    private bool AnyReplacement(Dictionary<long, List<SmallGrid>> replacements)
    {
        foreach (PlannedSwap swap in Swaps)
        {
            if (!swap.Removes && replacements.ContainsKey(swap.GroupId))
            {
                return true;
            }
        }

        return false;
    }

    private static IReferencable? NetworkOf(SmallGrid piece) => piece is Chute chute ? chute.ChuteNetwork : null;

    private static List<SmallGrid> MembersOf(ChuteNetwork network)
    {
        List<SmallGrid> pieces = new List<SmallGrid>();
        foreach (INetworkedStructure member in Runs.RunNetworks.Copy(network.StructureList))
        {
            if (member?.GetAsThing is SmallGrid grid && grid != null)
            {
                pieces.Add(grid);
            }
        }

        return pieces;
    }
}
