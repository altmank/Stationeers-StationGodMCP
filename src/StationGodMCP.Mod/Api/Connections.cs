#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// connections: every connection end of a thing and what is attached there, or a pipe, cable or chute network's
/// members and state. Read only.
///
/// Connection ends (CODE, Assets.Scripts.Objects): a SmallGrid (pipe, cable, chute, device) keeps its ends in
/// SmallGrid.OpenEnds. A Connection has ConnectionType (NetworkType flags), ConnectionRole, a Transform, and two 0.5 m
/// small-grid cells, LocalGrid and FacingGrid (Connection.SetGrids). Another SmallGrid is attached at an end when one
/// of its ends of a shared type has LocalGrid equal to this end's FacingGrid (SmallGrid.IsConnected; a Pipe also needs
/// matching gas or liquid content). The game looks in the SmallCell at the end's LocalGrid
/// (Connection.GetSmallGridOccupant), which keeps one Chute, Pipe, Device, Cable, Other and Rail; this checks every
/// slot of both cells with IsConnected, so two things sharing a cell are both found. The network at an end is the one
/// Device.PrintDebugInfo prints: the attached pipe's PipeNetwork, cable's CableNetwork or chute's ChuteNetwork; a pipe,
/// cable or chute's own ends are on its own network. type_name and role_name are the game's display names
/// (LocalizedEnumCollections.GetName).
///
/// Networks (CODE): ids share the thing id space (Referencable.Find). PipeNetwork keeps pipes in StructureList and
/// devices in DeviceList; its Atmosphere is null on a multiplayer client. CableNetwork keeps CableList and DeviceList;
/// each power tick (CableNetwork.OnPowerTick) sets RequiredLoad, PotentialLoad, CurrentLoad and ShortfallLoad, the
/// values the Cable Analyser shows. PowerTick takes actual = min(Potential, Required) and each tick breaks one fuse
/// whose PowerBreak and one cable whose MaxVoltage is below it: that is "overloaded". ChuteNetwork keeps chutes in
/// StructureList and devices in DeviceList. Not reported: per-device draw, per-cable ratings (only the lowest), and
/// elevator, landing pad and robotic arm networks. Both network forms add the broken pieces touching the network at its
/// members' ends without being on it (BrokenNeighbours), whatever the filters keep.
/// </summary>
internal static class ConnectionsApi
{
    private const int DefaultLimit = ReplyDefaults.ConnectionMembers;
    private const int MaximumLimit = 1000;

    internal static object Handle(Args args)
    {
        bool thing = args.Has("reference_id");
        bool network = args.Has("network_id");
        if (!thing && !network && (args.Has("min") || args.Has("max")))
        {
            return OpenEndsInArea(args);
        }

        if (thing == network)
        {
            throw ApiErrors.InvalidArgument(
                "Pass reference_id, network_id with kind, or min and max (open ends in a box).");
        }

        if (thing)
        {
            args.Reject("reference_id", "kind", "limit", "offset", "prefab_contains", "open_ends_only", "min", "max",
                "near", "radius_m", "summarize");
            return EndsReader.Read(GameLookup.RequireThing(args.ThingId("reference_id")));
        }

        string kind = args.String("kind").Trim().ToLowerInvariant();
        UpgradeFamily family = kind switch
        {
            "cable" => new CableFamily(),
            "pipe" => new PipeFamily(),
            "chute" => new ChuteFamily(),
            _ => throw ApiErrors.InvalidArgument("Argument 'kind' must be pipe, cable or chute.")
        };
        ThingId id = NetworkHandles.Resolve(args, "network_id", family);
        if (args.OptionalBool("summarize") ?? false)
        {
            args.Reject("summarize", "offset", "open_ends_only");
            return NetworkReader.Overview(kind, id,
                PageRequest.From(args, ReplyDefaults.NetworkOverviewLists, MaximumLimit), NetworkMemberFilter.Parse(args));
        }

        return NetworkReader.Read(kind, id, PageRequest.From(args, DefaultLimit, MaximumLimit),
            NetworkMemberFilter.Parse(args));
    }

    // The area form: every cable, pipe and chute piece (or those of kind) standing in the box with an end of its kind
    // that nothing is attached at, across every network; cells by y, then z, then x, kinds in that order.
    private static AreaOpenEndsView OpenEndsInArea(Args args)
    {
        args.Reject("the box form (it lists open ends in a box)", "near", "radius_m", "summarize");
        if (args.OptionalBool("open_ends_only") == false)
        {
            throw ApiErrors.InvalidArgument(
                "The box form lists open ends only; drop open_ends_only false, or name a network_id for all members.");
        }

        PieceSelection.Box box = UpgradeApi.BoxOf(args);
        NetworkMemberFilter filter = NetworkMemberFilter.Parse(args);
        string? only = args.OptionalString("kind")?.Trim().ToLowerInvariant();
        List<UpgradeFamily> families = new List<UpgradeFamily>(3);
        foreach (UpgradeFamily family in new UpgradeFamily[] { new CableFamily(), new PipeFamily(), new ChuteFamily() })
        {
            if (only == null || family.NetworkKind == only)
            {
                families.Add(family);
            }
        }

        if (families.Count == 0)
        {
            throw ApiErrors.InvalidArgument("Argument 'kind' must be pipe, cable or chute.");
        }

        List<NetworkMemberView> found = new List<NetworkMemberView>();
        List<ColorSwatch> swatches = PaintApi.Swatches();
        foreach (UpgradeFamily family in families)
        {
            foreach (SmallGrid piece in UpgradePlanner.InBox(family, box))
            {
                if (!filter.KeepsPrefab(piece.PrefabName))
                {
                    continue;
                }

                List<int> open = NetworkReader.OpenEnds(piece, family.NetworkKind);
                if (open.Count > 0)
                {
                    IReferencable? owner = family.NetworkOf(piece);
                    found.Add(new NetworkMemberView(GameLookup.ViewOf(piece), family.NetworkKind,
                        GameLookup.ViewOf(piece.Position), open,
                        owner != null ? new ThingId(owner.ReferenceId) : (ThingId?)null,
                        PaintApi.ShownColorOf(piece, swatches)));
                }
            }
        }

        PageRequest page = PageRequest.From(args, ReplyDefaults.AreaOpenEnds, MaximumLimit);
        Slice<NetworkMemberView> slice = Slice<NetworkMemberView>.Of(found, page);
        page.Note("members", slice.Items.Count, found.Count);
        return new AreaOpenEndsView(Slice<NetworkMemberView>.Page(slice.Items, page, found.Count));
    }
}

/// <summary>A thing's connection ends and what is attached at each.</summary>
internal static class EndsReader
{
    internal static ConnectionsView Read(Thing thing)
    {
        if (!(thing is SmallGrid grid))
        {
            throw ApiErrors.Refused(
                "not_connectable",
                $"{Names.Of(thing)} ({thing.GetType().Name}) is not a pipe, cable, chute or device, so it has no "
                + "connection ends.");
        }

        List<Connection> ends = grid.OpenEnds != null ? new List<Connection>(grid.OpenEnds) : new List<Connection>();
        List<ConnectionEndView> views = new List<ConnectionEndView>(ends.Count);
        for (int index = 0; index < ends.Count; index++)
        {
            if (ends[index] != null)
            {
                views.Add(ReadEnd(grid, ends[index], index));
            }
        }

        return new ConnectionsView(
            GameLookup.ViewOf(thing), GameLookup.ViewOf(thing.Position), OwnNetwork(grid), views,
            Orientations.Of(thing), PaintApi.ShownColorOf(thing, PaintApi.Swatches()));
    }

    private static ConnectionEndView ReadEnd(SmallGrid grid, Connection end, int index)
    {
        List<Thing> attached = AttachedAt(grid, end);
        List<ThingView> connected = new List<ThingView>(attached.Count);
        foreach (Thing thing in attached)
        {
            connected.Add(GameLookup.ViewOf(thing));
        }

        ConnectionKind kind = new ConnectionKind(
            end.ConnectionType.ToString(), TypeName(end.ConnectionType), end.ConnectionRole.ToString(),
            RoleName(end.ConnectionRole));
        PositionView? position = end.Transform != null ? GameLookup.ViewOf(end.Transform.position) : null;
        return new ConnectionEndView(index, kind, position, EndNetwork(grid, end, attached), connected);
    }

    private static string? TypeName(NetworkType type)
    {
        try
        {
            return NonEmpty(type.GetName());
        }
        catch (Exception)
        {
            // LocalizedEnumCollections.GetName has no name for a NetworkType flag combination it does not declare.
            return null;
        }
    }

    private static string? RoleName(ConnectionRole role)
    {
        try
        {
            return NonEmpty(role.GetName());
        }
        catch (Exception)
        {
            // LocalizedEnumCollections.GetName, as for the type.
            return null;
        }
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>
    /// The networks a thing is part of (a cable, pipe or chute piece) or its ends join, each once, as connections
    /// reports them; empty for a thing that is not on the small grid.
    /// </summary>
    internal static List<NetworkRefView> NetworksOf(Thing thing)
    {
        List<NetworkRefView> found = new List<NetworkRefView>();
        if (!(thing is SmallGrid grid))
        {
            return found;
        }

        AddNetwork(found, OwnNetwork(grid));
        if (grid.OpenEnds == null)
        {
            return found;
        }

        foreach (Connection end in grid.OpenEnds)
        {
            if (end != null)
            {
                AddNetwork(found, EndNetwork(grid, end, AttachedAt(grid, end)));
            }
        }

        return found;
    }

    private static void AddNetwork(List<NetworkRefView> found, NetworkRefView? network)
    {
        if (network != null && !found.Exists(known => known.Kind == network.Kind && known.Id.Equals(network.Id)))
        {
            found.Add(network);
        }
    }

    // Each occupant of the end's two small-grid cells that the game's own IsConnected links to it, not the owner.
    internal static List<Thing> AttachedAt(SmallGrid owner, Connection end)
    {
        List<Thing> found = new List<Thing>();
        GridController world = GridController.World;
        if (world == null || end.Transform == null)
        {
            return found;
        }

        AddAttached(world.GetSmallCell(end.GetLocalGrid()), owner, end, found);
        AddAttached(world.GetSmallCell(end.GetFacingGrid()), owner, end, found);
        return found;
    }

    private static void AddAttached(SmallCell? cell, SmallGrid owner, Connection end, List<Thing> found)
    {
        if (cell == null)
        {
            return;
        }

        ISmallGrid[] occupants = { cell.Chute, cell.Pipe, cell.Device, cell.Cable, cell.Other, cell.Rail };
        foreach (ISmallGrid occupant in occupants)
        {
            Thing? thing = occupant as Thing;
            if (thing != null && thing != owner && !found.Contains(thing) && IsConnected(occupant, end))
            {
                found.Add(thing);
            }
        }
    }

    private static bool IsConnected(ISmallGrid occupant, Connection end)
    {
        try
        {
            return occupant.IsConnected(end);
        }
        catch (Exception)
        {
            // SmallGrid.IsConnected reads the occupant's ends' transforms, which a half-built or removed thing lacks.
            return false;
        }
    }

    internal static NetworkRefView? OwnNetwork(SmallGrid grid)
    {
        switch (grid)
        {
            case Pipe pipe:
                return Ref("pipe", pipe.PipeNetwork);
            case Cable cable:
                return Ref("cable", cable.CableNetwork);
            case Chute chute:
                return Ref("chute", chute.ChuteNetwork);
            default:
                return null;
        }
    }

    private static NetworkRefView? EndNetwork(SmallGrid owner, Connection end, List<Thing> attached)
    {
        NetworkType type = end.ConnectionType;
        if ((type & (NetworkType.Pipe | NetworkType.PipeLiquid)) != NetworkType.None)
        {
            PipeNetwork? own = owner is INetworkedPipe pipe ? pipe.PipeNetwork : null;
            return Ref("pipe", own ?? FirstPipeNetwork(attached));
        }

        if ((type & NetworkType.PowerAndData) != NetworkType.None)
        {
            CableNetwork? own = owner is Cable cable ? cable.CableNetwork : null;
            return Ref("cable", own ?? FirstCableNetwork(attached));
        }

        if ((type & NetworkType.Chute) != NetworkType.None)
        {
            ChuteNetwork? own = owner is Chute chute ? chute.ChuteNetwork : null;
            return Ref("chute", own ?? FirstChuteNetwork(attached));
        }

        return null;
    }

    private static PipeNetwork? FirstPipeNetwork(List<Thing> attached)
    {
        foreach (Thing thing in attached)
        {
            if (thing is INetworkedPipe pipe && pipe.PipeNetwork != null)
            {
                return pipe.PipeNetwork;
            }
        }

        return null;
    }

    private static CableNetwork? FirstCableNetwork(List<Thing> attached)
    {
        foreach (Thing thing in attached)
        {
            if (thing is Cable cable && cable.CableNetwork != null)
            {
                return cable.CableNetwork;
            }
        }

        return null;
    }

    private static ChuteNetwork? FirstChuteNetwork(List<Thing> attached)
    {
        foreach (Thing thing in attached)
        {
            if (thing is Chute chute && chute.ChuteNetwork != null)
            {
                return chute.ChuteNetwork;
            }
        }

        return null;
    }

    private static NetworkRefView? Ref(string kind, IReferencable? network) =>
        network != null ? new NetworkRefView(kind, new ThingId(network.ReferenceId)) : null;
}

/// <summary>A network's members, paged, and its summary.</summary>
internal static class NetworkReader
{
    internal static NetworkMembersView Read(string kind, ThingId id, PageRequest page, NetworkMemberFilter filter)
    {
        Found network = Find(kind, id);
        return Page(kind, id, network.Members, network.Summary, page, filter);
    }

    /// <summary>
    /// summarize: the members the filters keep counted by prefab and colour, and the first limit of its devices and
    /// of its members with an open end.
    /// </summary>
    internal static NetworkOverviewView Overview(string kind, ThingId id, PageRequest limit, NetworkMemberFilter filter)
    {
        Found network = Find(kind, id);
        List<ColorSwatch> swatches = PaintApi.Swatches();
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
            ThingColorView? color = PaintApi.ShownColorOf(member, swatches);
            tally.Add(member.PrefabName, () => GameLookup.ViewOf(member).DisplayName, role, color?.Name);
            if (member is Device)
            {
                deviceCount++;
                if (devices.Count < limit.Limit)
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
                if (openEnds.Count < limit.Limit)
                {
                    openEnds.Add(MemberView(member, role, open, color));
                }
            }
        }

        limit.Note("devices", devices.Count, deviceCount);
        limit.Note("open_ends", openEnds.Count, openEndCount);
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
                throw ApiErrors.InvalidArgument("Argument 'kind' must be pipe, cable or chute.");
        }
    }

    private static bool Keeps(NetworkMemberFilter filter, Thing member) =>
        filter.KeepsPrefab(member.PrefabName) &&
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

    private static NetworkMembersView Page(string kind, ThingId id, NetworkMembers members, object? summary,
        PageRequest request, NetworkMemberFilter filter)
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

        Slice<KeptMember> page = Slice<KeptMember>.Of(kept, request);
        List<NetworkMemberView> views = new List<NetworkMemberView>(page.Items.Count);
        List<ColorSwatch> swatches = PaintApi.Swatches();
        foreach (KeptMember member in page.Items)
        {
            views.Add(MemberView(member.Thing, member.Thing is Device ? "device" : kind, member.OpenEnds,
                PaintApi.ShownColorOf(member.Thing, swatches)));
        }

        request.Note("members", views.Count, page.Total);
        return new NetworkMembersView(new NetworkRefView(kind, id), summary,
            Slice<NetworkMemberView>.Page(views, request, page.Total), members.StructureCount,
            members.All.Count - members.StructureCount,
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
