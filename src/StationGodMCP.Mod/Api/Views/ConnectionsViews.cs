#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Views;

/// <summary>inspect connections for a thing: its connection ends and what is attached at each.</summary>
internal sealed class ConnectionsView
{
    internal ConnectionsView(ThingView thing, PositionView position, NetworkRefView? ownNetwork,
        List<ConnectionEndView> ends, OrientationView? rotation = null, ThingColorView? color = null)
    {
        Color = color;
        Rotation = rotation;
        Thing = thing;
        Position = position;
        OwnNetwork = ownNetwork;
        Ends = ends;
        Count = ends.Count;
    }

    public ThingView Thing { get; }

    public PositionView Position { get; }

    /// <summary>
    /// How the thing stands turned: facing (its front), up and Euler degrees, the forms build structure takes, so it
    /// can be placed again as it stands or turned (facing reversed: 180 degrees).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientationView? Rotation { get; }

    /// <summary>A pipe, cable or chute's own network; null for a device.</summary>
    public NetworkRefView? OwnNetwork { get; }

    /// <summary>Its colour {index, name, is_default} as write paint reads it; left out for a thing with none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingColorView? Color { get; }

    public List<ConnectionEndView> Ends { get; }

    public int Count { get; }
}

/// <summary>One connection end (SmallGrid.OpenEnds).</summary>
internal sealed class ConnectionEndView
{
    internal ConnectionEndView(int index, ConnectionKind kind, PositionView? position, NetworkRefView? network,
        List<ThingView> connected, PositionView? at = null, string? pieceEnd = null)
    {
        At = at;
        PieceEnd = pieceEnd;
        Index = index;
        Type = kind.Type;
        TypeName = kind.TypeName;
        Role = kind.Role;
        RoleName = kind.RoleName;
        Position = position;
        Network = network;
        Connected = connected;
    }

    public int Index { get; }

    /// <summary>NetworkType: Pipe, PipeLiquid, Power, Data, PowerAndData, Chute...</summary>
    public string Type { get; }

    public string? TypeName { get; }

    /// <summary>ConnectionRole: None, Input, Input2, Output, Output2, Waste.</summary>
    public string Role { get; }

    public string? RoleName { get; }

    public PositionView? Position { get; }

    /// <summary>The centre of the small cell a piece joining this end stands in, as build reports a port's at.</summary>
    public PositionView? At { get; }

    /// <summary>The end a piece standing at at needs to join this one (+x, -y, ...): it points back into the thing.</summary>
    public string? PieceEnd { get; }

    public NetworkRefView? Network { get; }

    public List<ThingView> Connected { get; }
}

/// <summary>An end's type and role, as enum names and as the game displays them.</summary>
internal sealed class ConnectionKind
{
    internal ConnectionKind(string type, string? typeName, string role, string? roleName)
    {
        Type = type;
        TypeName = typeName;
        Role = role;
        RoleName = roleName;
    }

    internal string Type { get; }

    internal string? TypeName { get; }

    internal string Role { get; }

    internal string? RoleName { get; }
}

/// <summary>A network by kind (pipe, cable or chute) and id.</summary>
internal sealed class NetworkRefView
{
    internal NetworkRefView(string kind, ThingId id)
    {
        Kind = kind;
        Id = id;
    }

    public string Kind { get; }

    public ThingId Id { get; }
}

/// <summary>
/// survey network's member filters: prefab (the verbs' prefab filter) and open_ends (only members with an end of the
/// network's kind that nothing is attached at: a run's loose ends, a device port left unjoined), and an area (a box, or
/// near with radius: only members whose position lies in it). All are applied before paging, so total counts the
/// members kept.
/// </summary>
internal sealed class NetworkMemberFilter
{
    private NetworkMemberFilter(PrefabFilter? prefab, bool openEndsOnly, PointArea area)
    {
        Prefab = prefab;
        OpenEndsOnly = openEndsOnly;
        Area = area;
    }

    internal static NetworkMemberFilter None { get; } = new NetworkMemberFilter(null, false, PointArea.Anywhere);

    /// <summary>The prefabs kept; null keeps every prefab.</summary>
    internal PrefabFilter? Prefab { get; }

    internal bool OpenEndsOnly { get; }

    /// <summary>Where a member must stand (a box, or near with radius); anywhere by default.</summary>
    internal PointArea Area { get; }

    internal static NetworkMemberFilter Of(PrefabFilter? prefab, bool openEndsOnly, PointArea area) =>
        new NetworkMemberFilter(prefab, openEndsOnly, area);

    internal bool KeepsPrefab(string? prefabName, int prefabHash) =>
        Prefab == null || Prefab.Keeps(prefabName, prefabHash);

    internal bool KeepsPosition(Vec3 position) => Area.Contains(position);
}

internal sealed class NetworkMemberView
{
    internal NetworkMemberView(ThingView thing, string member, PositionView position, List<int>? openEnds = null,
        ThingId? networkId = null, ThingColorView? color = null)
    {
        Color = color;
        NetworkId = networkId;
        OpenEnds = openEnds;
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Member = member;
        Position = position;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>pipe, cable, chute or device.</summary>
    public string Member { get; }

    public PositionView Position { get; }

    /// <summary>Its colour {index, name, is_default} as write paint reads it; left out for a thing with none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingColorView? Color { get; }

    /// <summary>With open_ends_only: the indexes (as inspect connections lists ends) of its open ends of the network's kind.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<int>? OpenEnds { get; }

    /// <summary>The box form: the network the piece is on; left out in the network form, which names it once.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? NetworkId { get; }
}

/// <summary>
/// connections' network form with summarize: what is on the network in one reply. Its members counted by prefab (and
/// by colour), its devices and its members with an open end, each list cut at limit; the filters apply first.
/// </summary>
internal sealed class NetworkOverviewView
{
    internal NetworkOverviewView(NetworkRefView network, object? summary, int structureCount, int deviceCount,
        List<PrefabCountView> byPrefab, List<NetworkMemberView> devices, List<NetworkMemberView> openEnds,
        int openEndCount, BrokenNeighbourReport broken)
    {
        Warning = broken.Warning;
        BrokenNeighbourCount = broken.Count;
        BrokenNeighbours = broken.OrNull;
        Network = network;
        Summary = summary;
        StructureCount = structureCount;
        DeviceCount = deviceCount;
        ByPrefab = byPrefab;
        Devices = devices;
        OpenEnds = openEnds;
        OpenEndCount = openEndCount;
    }

    public NetworkRefView Network { get; }

    /// <summary>As the network form's summary.</summary>
    public object? Summary { get; }

    /// <summary>Broken pieces touching the network that the counts leave out; left out when none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Warning { get; }

    /// <summary>How many broken pieces touch the network at its ends without being on it (0: none).</summary>
    public int BrokenNeighbourCount { get; }

    /// <summary>The first of them (see broken_neighbour_count); left out when none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<BrokenNeighbourView>? BrokenNeighbours { get; }

    /// <summary>The members the filters kept that are pieces.</summary>
    public int StructureCount { get; }

    /// <summary>The members the filters kept that are devices; devices lists the first limit of them.</summary>
    public int DeviceCount { get; }

    /// <summary>Most first, then by prefab name.</summary>
    public List<PrefabCountView> ByPrefab { get; }

    public List<NetworkMemberView> Devices { get; }

    /// <summary>Members with an end of the network's kind that nothing is attached at, each with open_ends.</summary>
    public List<NetworkMemberView> OpenEnds { get; }

    public int OpenEndCount { get; }
}

/// <summary>How many members of one prefab a network holds, and how many show each colour.</summary>
internal sealed class PrefabCountView
{
    internal PrefabCountView(MemberPrefabCount count)
    {
        PrefabName = count.Prefab;
        DisplayName = count.DisplayName;
        Member = count.Member;
        Count = count.Count;
        Colors = count.Colors;
    }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>pipe, cable, chute or device.</summary>
    public string Member { get; }

    public int Count { get; }

    /// <summary>Colour name to how many show it; left out when none of them has a colour.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public SortedDictionary<string, int>? Colors { get; }
}

internal sealed class PipeSummaryView
{
    internal PipeSummaryView(string content, double volumeL, double pressureKpa, double temperatureK, double totalMol,
        double liquidVolumeL, List<NetworkGasView> gases)
    {
        Content = content;
        VolumeL = volumeL;
        PressureKpa = pressureKpa;
        TemperatureK = temperatureK;
        TotalMol = totalMol;
        LiquidVolumeL = liquidVolumeL;
        Gases = gases;
    }

    public string Content { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double TemperatureK { get; }

    public double TotalMol { get; }

    public double LiquidVolumeL { get; }

    public List<NetworkGasView> Gases { get; }
}

internal sealed class NetworkGasView
{
    internal NetworkGasView(string gas, string state, double amountMol)
    {
        Gas = gas;
        State = state;
        AmountMol = amountMol;
    }

    public string Gas { get; }

    public string State { get; }

    public double AmountMol { get; }
}

internal sealed class CableSummaryView
{
    internal CableSummaryView(CableLoads loads, float? lowestCableMaxW, float? lowestFuseBreakW, int cableCount,
        int fuseCount)
    {
        RequiredW = loads.Required;
        PotentialW = loads.Potential;
        ActualW = loads.Actual;
        ShortfallW = loads.Shortfall;
        LowestCableMaxW = lowestCableMaxW;
        LowestFuseBreakW = lowestFuseBreakW;
        float delivered = System.Math.Min(loads.Potential, loads.Required);
        Overloaded = lowestCableMaxW.HasValue && lowestCableMaxW.Value < delivered;
        FuseOverloaded = lowestFuseBreakW.HasValue && lowestFuseBreakW.Value < delivered;
        CableCount = cableCount;
        FuseCount = fuseCount;
    }

    public float RequiredW { get; }

    public float PotentialW { get; }

    public float ActualW { get; }

    public float ShortfallW { get; }

    public float? LowestCableMaxW { get; }

    public float? LowestFuseBreakW { get; }

    /// <summary>min(potential, required) above the weakest cable's rating: the game burns one per tick.</summary>
    public bool Overloaded { get; }

    public bool FuseOverloaded { get; }

    public int CableCount { get; }

    public int FuseCount { get; }
}

/// <summary>A cable network's loads at the last power tick (CableNetwork.OnPowerTick), in watts.</summary>
internal sealed class CableLoads
{
    internal CableLoads(float required, float potential, float actual, float shortfall)
    {
        Required = required;
        Potential = potential;
        Actual = actual;
        Shortfall = shortfall;
    }

    internal float Required { get; }

    internal float Potential { get; }

    internal float Actual { get; }

    internal float Shortfall { get; }
}

internal sealed class ChuteSummaryView
{
    internal ChuteSummaryView(int memberCount)
    {
        MemberCount = memberCount;
    }

    public int MemberCount { get; }
}

/// <summary>A broken piece that touches a network at its ends without being on it.</summary>
internal sealed class BrokenNeighbourView
{
    internal BrokenNeighbourView(ThingView thing, PositionView position, string? pipeBurst, NetworkRefView? network,
        ThingId touches)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        Position = position;
        PipeBurst = pipeBurst;
        Network = network;
        Touches = touches;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView Position { get; }

    /// <summary>A pipe's burst cause (pressure, liquid, solid, or none when it is broken but not burst); left out otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? PipeBurst { get; }

    /// <summary>The network it is on itself; null when it is on none.</summary>
    public NetworkRefView? Network { get; }

    /// <summary>The member (or broken piece nearer the network) it is attached to.</summary>
    public ThingId Touches { get; }
}

/// <summary>
/// The broken pieces touching a network that its member list does not hold: how many, the first of them, and the
/// reply's warning. Every network form reports it, whatever its filters, since the list it warns about cannot show them.
/// </summary>
internal sealed class BrokenNeighbourReport
{
    internal BrokenNeighbourReport(List<BrokenNeighbourView> listed, int count, string? warning)
    {
        Listed = listed;
        Count = count;
        Warning = warning;
    }

    internal static BrokenNeighbourReport None { get; } =
        new BrokenNeighbourReport(new List<BrokenNeighbourView>(), 0, null);

    internal List<BrokenNeighbourView> Listed { get; }

    internal int Count { get; }

    internal string? Warning { get; }

    /// <summary>The list as the reply carries it: left out when empty.</summary>
    internal List<BrokenNeighbourView>? OrNull => Count == 0 ? null : Listed;
}
