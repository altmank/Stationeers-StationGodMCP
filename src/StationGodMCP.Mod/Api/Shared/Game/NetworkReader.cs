#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A network's members, paged, and its summary (CODE): ids share the thing id space (Referencable.Find). PipeNetwork
/// keeps pipes in StructureList and devices in DeviceList; its Atmosphere is null on a multiplayer client. CableNetwork
/// keeps CableList and DeviceList; each power tick (CableNetwork.OnPowerTick) sets RequiredLoad, PotentialLoad,
/// CurrentLoad and ShortfallLoad, the values the Cable Analyser shows. PowerTick takes actual = min(Potential, Required)
/// and each tick breaks one fuse whose PowerBreak and one cable whose MaxVoltage is below it: that is "overloaded".
/// ChuteNetwork keeps chutes in StructureList and devices in DeviceList. Not reported: per-device draw, per-cable
/// ratings (only the lowest), and elevator, landing pad and robotic arm networks. Both network forms count the broken
/// pieces touching the network at its members' ends without being on it (BrokenNeighbours), whatever the filters keep.
/// </summary>
internal static class NetworkReader
{
    internal const long MaximumBoxCells = 262144;

    /// <summary>The members the filter keeps, as one page, with the network's summary and counts.</summary>
    internal static MembersPage Read(string kind, ThingId id, Paging paging, NetworkMemberFilter filter)
    {
        Found network = Find(kind, id);
        return Page(kind, id, network.Members, network.Summary, paging, filter);
    }

    /// <summary>The network's pieces, then its devices when devices asks, each once.</summary>
    internal static List<Thing> Things(string kind, ThingId id, bool devices)
    {
        NetworkMembers members = Find(kind, id).Members;
        return members.All.GetRange(0, devices ? members.All.Count : members.StructureCount);
    }

    /// <summary>
    /// Every piece of the kinds named (all three when kind is null) standing in the box with an end of its kind that
    /// nothing is attached at, across every network: cells by y, then z, then x, kinds cable, pipe, chute.
    /// </summary>
    internal static List<NetworkMemberView> OpenEndsIn(BoxCorners corners, string? kind, NetworkMemberFilter filter)
    {
        PieceSelection.Box box = BoxOf(corners);
        List<NetworkMemberView> found = new List<NetworkMemberView>();
        List<ColorSwatch> swatches = ThingColors.Swatches();
        foreach (UpgradeFamily family in new UpgradeFamily[] { new CableFamily(), new PipeFamily(), new ChuteFamily() })
        {
            if (kind != null && family.NetworkKind != kind)
            {
                continue;
            }

            foreach (SmallGrid piece in UpgradePlanner.InBox(family, box))
            {
                if (!filter.KeepsPrefab(piece.PrefabName, piece.PrefabHash))
                {
                    continue;
                }

                List<int> open = OpenEnds(piece, family.NetworkKind);
                if (open.Count > 0)
                {
                    IReferencable? owner = family.NetworkOf(piece);
                    found.Add(new NetworkMemberView(GameLookup.ViewOf(piece), family.NetworkKind,
                        GameLookup.ViewOf(piece.Position), open,
                        owner != null ? new ThingId(owner.ReferenceId) : (ThingId?)null,
                        ThingColors.ShownColorOf(piece, swatches)));
                }
            }
        }

        return found;
    }

    // Two corners in metres as the half-metre cells between them; refused over MaximumBoxCells (a 32 m cube).
    private static PieceSelection.Box BoxOf(BoxCorners corners)
    {
        GridCell a = Runs.RunArgs.CellOf(new UnityEngine.Vector3((float)corners.First.X, (float)corners.First.Y,
            (float)corners.First.Z));
        GridCell b = Runs.RunArgs.CellOf(new UnityEngine.Vector3((float)corners.Second.X, (float)corners.Second.Y,
            (float)corners.Second.Z));
        GridCell min = new GridCell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        GridCell max = new GridCell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        long cells = SmallCellCode.SmallCountIn(min, max);
        return cells <= MaximumBoxCells
            ? new PieceSelection.Box(min, max)
            : throw ApiErrors.InvalidArgument(
                $"box: holds {cells} half-metre cells; at most {MaximumBoxCells} (a 32 m cube).");
    }

    /// <summary>
    /// summarize: the members the filters keep counted by prefab and colour, and the first limit of its devices and
    /// of its members with an open end.
    /// </summary>
    internal static NetworkOverviewView Overview(string kind, ThingId id, int limit, NetworkMemberFilter filter)
    {
        Found network = Find(kind, id);
        List<ColorSwatch> swatches = ThingColors.Swatches();
        MemberTally tally = new MemberTally();
        List<NetworkMemberView> devices = new List<NetworkMemberView>();
        List<NetworkMemberView> openEnds = new List<NetworkMemberView>();
        int structureCount = 0;
        int deviceCount = 0;
        int openEndCount = 0;
        foreach (Thing member in network.Members.All)
        {
            if (!Keeps(filter, member))
            {
                continue;
            }

            string role = member is Device ? "device" : kind;
            ThingColorView? color = ThingColors.ShownColorOf(member, swatches);
            tally.Add(member.PrefabName, () => GameLookup.ViewOf(member).DisplayName, role, color?.Name);
            if (member is Device)
            {
                deviceCount++;
                if (devices.Count < limit)
                {
                    devices.Add(MemberView(member, role, null, color));
                }
            }
            else
            {
                structureCount++;
            }

            List<int> open = OpenEnds(member, kind);
            if (open.Count > 0)
            {
                openEndCount++;
                if (openEnds.Count < limit)
                {
                    openEnds.Add(MemberView(member, role, open, color));
                }
            }
        }

        return new NetworkOverviewView(new NetworkRefView(kind, id), network.Summary, structureCount, deviceCount,
            tally.MostFirst().ConvertAll(static count => new PrefabCountView(count)), devices, openEnds, openEndCount,
            BrokenNeighbours.Around(kind, network.Members.All, "by_prefab"));
    }

    private static Found Find(string kind, ThingId id)
    {
        switch (kind)
        {
            case "pipe":
                PipeNetwork pipes = Referencable.Find<PipeNetwork>(id.Value) ?? throw NotFound(kind, id);
                return new Found(Members(pipes.StructureList, pipes.DeviceList), PipeSummary(pipes));
            case "cable":
                CableNetwork cables = Referencable.Find<CableNetwork>(id.Value) ?? throw NotFound(kind, id);
                return OfCables(cables);
            case "chute":
                ChuteNetwork chutes = Referencable.Find<ChuteNetwork>(id.Value) ?? throw NotFound(kind, id);
                NetworkMembers members = Members(chutes.StructureList, chutes.DeviceList);
                return new Found(members, new ChuteSummaryView(members.All.Count));
            default:
                throw ApiErrors.InvalidArgument("kind must be pipe, cable or chute.");
        }
    }

    private static bool Keeps(NetworkMemberFilter filter, Thing member) =>
        filter.KeepsPrefab(member.PrefabName, member.PrefabHash) &&
        filter.KeepsPosition(new Vec3(member.Position.x, member.Position.y, member.Position.z));

    private static NetworkMemberView MemberView(Thing member, string role, List<int>? openEnds, ThingColorView? color) =>
        new NetworkMemberView(GameLookup.ViewOf(member), role, GameLookup.ViewOf(member.Position), openEnds, null,
            color);

    private static ApiException NotFound(string kind, ThingId id) =>
        ApiErrors.Refused("network_not_found", $"No {kind} network has id {id}.");

    private static PipeSummaryView? PipeSummary(PipeNetwork network)
    {
        Atmosphere atmosphere = network.Atmosphere;
        if (atmosphere == null)
        {
            return null;
        }

        GasMixture mixture = atmosphere.GasMixture;
        List<NetworkGasView> gases = new List<NetworkGasView>();
        foreach (Chemistry.GasType type in GasTypes.All)
        {
            double moles = mixture.GetMoleValue(type).Quantity.ToDouble();
            if (moles > 0.0)
            {
                string state = Mole.MatterState(type) == AtmosphereHelper.MatterState.Liquid ? "liquid" : "gas";
                gases.Add(new NetworkGasView(type.ToString(), state, moles));
            }
        }

        return new PipeSummaryView(
            network.NetworkContentType.ToString(), atmosphere.Volume.ToDouble(),
            atmosphere.PressureGassesAndLiquids.ToDouble(), atmosphere.Temperature.ToDouble(),
            mixture.GetTotalMolesGassesAndLiquids.ToDouble(), mixture.VolumeLiquids.ToDouble(), gases);
    }

    private static Found OfCables(CableNetwork network)
    {
        List<Cable> cables = NonNull(network.CableList);
        List<CableFuse> fuses = NonNull(network.FuseList);
        CableLoads loads = new CableLoads(
            network.RequiredLoad, network.PotentialLoad, network.CurrentLoad, network.ShortfallLoad);
        CableSummaryView summary = new CableSummaryView(
            loads, Lowest(cables), Lowest(fuses), cables.Count, fuses.Count);
        return new Found(Members(cables, network.DeviceList), summary);
    }

    private static List<T> NonNull<T>(List<T> list) where T : Thing
    {
        List<T> copy;
        lock (list)
        {
            copy = new List<T>(list);
        }

        copy.RemoveAll(static item => (UnityEngine.Object)item == null);
        return copy;
    }

    private static float? Lowest(List<Cable> cables)
    {
        float? lowest = null;
        foreach (Cable cable in cables)
        {
            lowest = lowest.HasValue ? Math.Min(lowest.Value, cable.MaxVoltage) : cable.MaxVoltage;
        }

        return lowest;
    }

    private static float? Lowest(List<CableFuse> fuses)
    {
        float? lowest = null;
        foreach (CableFuse fuse in fuses)
        {
            lowest = lowest.HasValue ? Math.Min(lowest.Value, fuse.PowerBreak) : fuse.PowerBreak;
        }

        return lowest;
    }

    // Structures first, then devices, each once.
    private static NetworkMembers Members<TStructure>(IEnumerable<TStructure> structureList, List<Device> deviceList)
    {
        List<TStructure> structures;
        lock (structureList)
        {
            structures = new List<TStructure>(structureList);
        }

        List<Device> devices;
        lock (deviceList)
        {
            devices = new List<Device>(deviceList);
        }

        NetworkMembers members = new NetworkMembers();
        foreach (TStructure member in structures)
        {
            members.Add(member is INetworkedStructure networked ? networked.GetAsThing : member as Thing);
        }

        members.StructureCount = members.All.Count;
        foreach (Device device in devices)
        {
            members.Add(device);
        }

        return members;
    }

    private static MembersPage Page(string kind, ThingId id, NetworkMembers members, object? summary,
        Paging paging, NetworkMemberFilter filter)
    {
        List<KeptMember> kept = new List<KeptMember>(members.All.Count);
        foreach (Thing member in members.All)
        {
            if (!Keeps(filter, member))
            {
                continue;
            }

            List<int>? open = filter.OpenEndsOnly ? OpenEnds(member, kind) : null;
            if (open == null || open.Count > 0)
            {
                kept.Add(new KeptMember(member, open));
            }
        }

        Page<KeptMember> page = paging.Of(kept);
        List<NetworkMemberView> views = new List<NetworkMemberView>(page.Entries.Count);
        List<ColorSwatch> swatches = ThingColors.Swatches();
        foreach (KeptMember member in page.Entries)
        {
            views.Add(MemberView(member.Thing, member.Thing is Device ? "device" : kind, member.OpenEnds,
                ThingColors.ShownColorOf(member.Thing, swatches)));
        }

        return new MembersPage(new NetworkRefView(kind, id), summary,
            new Page<NetworkMemberView>(views, page.Total, page.Offset, page.Limit, page.HasMore),
            members.StructureCount, members.All.Count - members.StructureCount,
            BrokenNeighbours.WarningFor(kind, members.All, "members", "broken_neighbours with summarize true"));
    }

    // The indexes of a member's ends of the network's kind that nothing is attached at (EndsReader.AttachedAt).
    internal static List<int> OpenEnds(Thing member, string kind)
    {
        List<int> open = new List<int>();
        if (!(member is SmallGrid grid) || grid.OpenEnds == null)
        {
            return open;
        }

        NetworkType types = EndTypesOf(kind);
        for (int index = 0; index < grid.OpenEnds.Count; index++)
        {
            Connection end = grid.OpenEnds[index];
            if (end != null && (end.ConnectionType & types) != NetworkType.None &&
                EndsReader.AttachedAt(grid, end).Count == 0)
            {
                open.Add(index);
            }
        }

        return open;
    }

    /// <summary>The NetworkType bits a member of a network of the kind joins it by.</summary>
    internal static NetworkType EndTypesOf(string kind) => kind switch
    {
        "pipe" => NetworkType.Pipe | NetworkType.PipeLiquid,
        "cable" => NetworkType.PowerAndData,
        _ => NetworkType.Chute
    };

    /// <summary>A network's members and its summary.</summary>
    private readonly struct Found
    {
        internal Found(NetworkMembers members, object? summary)
        {
            Members = members;
            Summary = summary;
        }

        internal NetworkMembers Members { get; }

        internal object? Summary { get; }
    }

    private readonly struct KeptMember
    {
        internal KeptMember(Thing thing, List<int>? openEnds)
        {
            Thing = thing;
            OpenEnds = openEnds;
        }

        internal Thing Thing { get; }

        internal List<int>? OpenEnds { get; }
    }

    /// <summary>A network's things, each once (a HashSet of ids), in the order found.</summary>
    private sealed class NetworkMembers
    {
        private readonly HashSet<long> _seen = new HashSet<long>();

        internal List<Thing> All { get; } = new List<Thing>();

        internal int StructureCount { get; set; }

        internal void Add(Thing? thing)
        {
            if (thing != null && _seen.Add(thing.ReferenceId))
            {
                All.Add(thing);
            }
        }
    }
}

/// <summary>One page of a network's members, its network, its summary, its structure and device counts and its warning.</summary>
internal sealed class MembersPage
{
    internal MembersPage(NetworkRefView network, object? summary, Page<NetworkMemberView> page, int structures,
        int devices, string? warning)
    {
        Network = network;
        Summary = summary;
        Page = page;
        Structures = structures;
        Devices = devices;
        Warning = warning;
    }

    public NetworkRefView Network { get; }

    public object? Summary { get; }

    public Page<NetworkMemberView> Page { get; }

    public int Structures { get; }

    public int Devices { get; }

    public string? Warning { get; }
}
