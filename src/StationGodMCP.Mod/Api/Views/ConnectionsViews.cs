#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>connections for a thing: its connection ends and what is attached at each.</summary>
internal sealed class ConnectionsView
{
    internal ConnectionsView(ThingView thing, PositionView position, NetworkRefView? ownNetwork,
        List<ConnectionEndView> ends, OrientationView? rotation = null)
    {
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

internal sealed class NetworkMemberView
{
    internal NetworkMemberView(ThingView thing, string member, PositionView position)
    {
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
