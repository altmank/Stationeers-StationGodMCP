#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>connections for a thing: its connection ends and what is attached at each.</summary>
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
    /// How the thing stands turned: facing (its front), up and Euler degrees, the forms place_structure takes, so it
    /// can be placed again as it stands or turned (facing reversed: 180 degrees).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientationView? Rotation { get; }

    /// <summary>A pipe, cable or chute's own network; null for a device.</summary>
    public NetworkRefView? OwnNetwork { get; }

    /// <summary>Its colour {index, name, is_default} as paint reads it; left out for a thing with none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingColorView? Color { get; }

    public List<ConnectionEndView> Ends { get; }

    public int Count { get; }
}

/// <summary>One connection end (SmallGrid.OpenEnds).</summary>
internal sealed class ConnectionEndView
{
    internal ConnectionEndView(int index, ConnectionKind kind, PositionView? position, NetworkRefView? network,
        List<ThingView> connected)
    {
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

/// <summary>connections for a network: its summary and one page of its members, structures first.</summary>
internal sealed class NetworkMembersView
{
    internal NetworkMembersView(NetworkRefView network, object? summary, Slice<NetworkMemberView> page,
        int structureCount, int deviceCount)
    {
        Network = network;
        Summary = summary;
        Members = page.Items;
        Count = page.Items.Count;
        StructureCount = structureCount;
        DeviceCount = deviceCount;
        Offset = page.Offset;
        Limit = page.Limit;
        Total = page.Total;
        HasMore = page.HasMore;
    }

    public NetworkRefView Network { get; }

    /// <summary>A PipeSummaryView, CableSummaryView or ChuteSummaryView; null for a pipe network without air.</summary>
    public object? Summary { get; }

    public List<NetworkMemberView> Members { get; }

    public int Count { get; }

    public int StructureCount { get; }

    public int DeviceCount { get; }

    public int Offset { get; }

    public int Limit { get; }

    public int Total { get; }

    public bool HasMore { get; }
}

/// <summary>
/// connections' network-form filters: prefab_contains (a case-insensitive part of the prefab name) and open_ends_only
/// (only members with an end of the network's kind that nothing is attached at: a run's loose ends, a device port left
/// unjoined), and an area (min and max, or near with radius_m: only members whose position lies in it). All are applied
/// before paging, so total counts the members kept.
/// </summary>
internal sealed class NetworkMemberFilter
{
    private NetworkMemberFilter(string? prefabContains, bool openEndsOnly, PointArea area)
    {
        PrefabContains = prefabContains;
        OpenEndsOnly = openEndsOnly;
        Area = area;
    }

    internal static NetworkMemberFilter None { get; } = new NetworkMemberFilter(null, false, PointArea.Anywhere);

    internal string? PrefabContains { get; }

    internal bool OpenEndsOnly { get; }

    /// <summary>Where a member must stand (AreaArgs: min/max box, or near with radius_m); anywhere by default.</summary>
    internal PointArea Area { get; }

    internal static NetworkMemberFilter Parse(Args args)
    {
        string? prefab = args.OptionalString("prefab_contains")?.Trim();
        return new NetworkMemberFilter(string.IsNullOrEmpty(prefab) ? null : prefab,
            args.OptionalBool("open_ends_only") ?? false, AreaArgs.Parse(args));
    }

    internal bool KeepsPrefab(string? prefabName) =>
        PrefabContains == null ||
        (prefabName != null && prefabName.IndexOf(PrefabContains, System.StringComparison.OrdinalIgnoreCase) >= 0);

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

    /// <summary>Its colour {index, name, is_default} as paint reads it; left out for a thing with none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingColorView? Color { get; }

    /// <summary>With open_ends_only: the indexes (as connections lists ends) of its open ends of the network's kind.</summary>
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
        int openEndCount)
    {
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

/// <summary>connections over a box: one page of the pieces of every network in it with an open end of their kind.</summary>
internal sealed class AreaOpenEndsView
{
    internal AreaOpenEndsView(Slice<NetworkMemberView> page)
    {
        Members = page.Items;
        Count = page.Items.Count;
        Offset = page.Offset;
        Limit = page.Limit;
        Total = page.Total;
        HasMore = page.HasMore;
    }

    public List<NetworkMemberView> Members { get; }

    public int Count { get; }

    public int Offset { get; }

    public int Limit { get; }

    public int Total { get; }

    public bool HasMore { get; }
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
