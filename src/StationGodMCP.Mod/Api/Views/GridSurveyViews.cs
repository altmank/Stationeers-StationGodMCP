#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// grid_survey: one page of 2 m cells of a box or a room, each with its frame, walls, room and its 64 small cells as
/// one string (legend), and the pieces, devices and networks standing in the page's cells.
/// </summary>
internal sealed class GridSurveyView
{
    internal GridSurveyView(Slice<SurveyCellView> page, SurveyContents contents, string legend)
    {
        Legend = legend;
        Cells = page.Items;
        Count = page.Items.Count;
        Offset = page.Offset;
        Limit = page.Limit;
        Total = page.Total;
        HasMore = page.HasMore;
        Pieces = contents.Pieces;
        Devices = contents.Devices;
        Networks = contents.Networks;
    }

    public string Legend { get; }

    public List<SurveyCellView> Cells { get; }

    public int Count { get; }

    public int Offset { get; }

    public int Limit { get; }

    /// <summary>2 m cells in the box or room, over all pages.</summary>
    public int Total { get; }

    public bool HasMore { get; }

    /// <summary>Cables, pipes and chutes with a cell in this page's cells.</summary>
    public List<SurveyPieceView> Pieces { get; }

    /// <summary>Devices with a cell in this page's cells, with their cable and pipe ports.</summary>
    public List<SurveyDeviceView> Devices { get; }

    /// <summary>A RunCableNetworkView or RunPipeNetworkView per network of the pieces listed.</summary>
    public List<object> Networks { get; }
}

internal sealed class SurveyContents
{
    internal SurveyContents(List<SurveyPieceView> pieces, List<SurveyDeviceView> devices, List<object> networks)
    {
        Pieces = pieces;
        Devices = devices;
        Networks = networks;
    }

    internal List<SurveyPieceView> Pieces { get; }

    internal List<SurveyDeviceView> Devices { get; }

    internal List<object> Networks { get; }
}

/// <summary>One 2 m cell: its centre, room (null outside), frame, face structures and small cells.</summary>
internal sealed class SurveyCellView
{
    internal SurveyCellView(PositionView at, string? roomId, SurveyFrameView? frame, List<SurveyWallView> walls,
        string small, string support)
    {
        Support = support;
        At = at;
        RoomId = roomId;
        Frame = frame;
        Walls = walls;
        Small = small;
    }

    public PositionView At { get; }

    public string? RoomId { get; }

    public SurveyFrameView? Frame { get; }

    public List<SurveyWallView> Walls { get; }

    /// <summary>64 characters, index x + 4y + 16z (see legend).</summary>
    public string Small { get; }

    /// <summary>
    /// 64 characters in the same order: what holds a piece in each small cell up. 'e' a frame edge or corner, 'f' on
    /// or inside a frame, 'w' on a wall's plane, 'a' air (the planners' frames_first avoids these).
    /// </summary>
    public string Support { get; }
}

internal sealed class SurveyFrameView
{
    internal SurveyFrameView(ThingView frame, int buildState, int buildStates, bool blocksAir, bool blocksGravity)
    {
        ReferenceId = frame.ReferenceId;
        PrefabName = frame.PrefabName;
        BuildState = buildState;
        BuildStates = buildStates;
        BlocksAir = blocksAir;
        BlocksGravity = blocksGravity;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public int BuildState { get; }

    public int BuildStates { get; }

    public bool BlocksAir { get; }

    public bool BlocksGravity { get; }
}

/// <summary>A face structure on one of the cell's faces (+x, -x, +y, -y, +z, -z).</summary>
internal sealed class SurveyWallView
{
    internal SurveyWallView(string face, ThingView wall, bool blocksAir)
    {
        Face = face;
        ReferenceId = wall.ReferenceId;
        PrefabName = wall.PrefabName;
        BlocksAir = blocksAir;
    }

    public string Face { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public bool BlocksAir { get; }
}

/// <summary>
/// A cable, pipe or chute piece: where it stands, its cells when more than one, its ends as world axes from its own
/// cells, its network and grade.
/// </summary>
internal sealed class SurveyPieceView
{
    internal SurveyPieceView(ThingView piece, string kind, PositionView at, List<PositionView>? cells,
        List<string> ends, ThingId? networkId, string? grade, RunFlowView? flow = null, ThingView? carries = null)
    {
        ReferenceId = piece.ReferenceId;
        Kind = kind;
        PrefabName = piece.PrefabName;
        At = at;
        Cells = cells;
        Ends = ends;
        NetworkId = networkId;
        Grade = grade;
        Flow = flow;
        Carries = carries;
    }

    public ThingId ReferenceId { get; }

    /// <summary>cable, pipe or chute.</summary>
    public string Kind { get; }

    public string? PrefabName { get; }

    public PositionView At { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PositionView>? Cells { get; }

    public List<string> Ends { get; }

    public ThingId? NetworkId { get; }

    /// <summary>normal, heavy, super_heavy; gas, liquid, insulated_gas, insulated_liquid; null otherwise.</summary>
    public string? Grade { get; }

    /// <summary>Chutes: which ends take items in and which let them out, where anything fixes it.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunFlowView? Flow { get; }

    /// <summary>Chutes: the item riding in the piece now.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingView? Carries { get; }
}

internal sealed class SurveyDeviceView
{
    internal SurveyDeviceView(ThingView device, PositionView at, List<SurveyPortView> ports)
    {
        ReferenceId = device.ReferenceId;
        PrefabName = device.PrefabName;
        DisplayName = device.DisplayName;
        At = at;
        Ports = ports;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView At { get; }

    public List<SurveyPortView> Ports { get; }
}

/// <summary>
/// A device end: the small cell a piece joining it stands in (at), the direction that piece needs an end towards
/// (toward, into the device), the end's NetworkType and role, and the network joined to it now (null for none).
/// </summary>
internal sealed class SurveyPortView
{
    internal SurveyPortView(int index, PositionView at, string toward, string type, string role, ThingId? networkId)
    {
        Index = index;
        At = at;
        Toward = toward;
        Type = type;
        Role = role;
        NetworkId = networkId;
    }

    public int Index { get; }

    public PositionView At { get; }

    public string Toward { get; }

    public string Type { get; }

    public string Role { get; }

    public ThingId? NetworkId { get; }
}
