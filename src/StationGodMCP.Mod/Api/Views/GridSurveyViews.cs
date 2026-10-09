#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>
/// grid_survey: one page of 2 m cells of a box or a room, each by what occupies it (SurveyOccupancyView) or in full
/// (SurveyCellView, with cell_detail full), and the pieces, devices and networks standing in the page's cells. A part
/// sections leaves out is absent; count, offset, limit, total and has_more always describe the page of cells.
/// </summary>
internal sealed class GridSurveyView
{
    internal GridSurveyView(Slice<SurveyCell> page, SurveyContents contents, SurveySections sections,
        string? legend)
    {
        Legend = legend;
        Cells = sections.Pick(SurveySection.Cells, page.Items);
        Count = page.Items.Count;
        Offset = page.Offset;
        Limit = page.Limit;
        Total = page.Total;
        HasMore = page.HasMore;
        Pieces = sections.Pick(SurveySection.Pieces, contents.Pieces);
        Devices = sections.Pick(SurveySection.Devices, contents.Devices);
        Networks = sections.Pick(SurveySection.Networks, contents.Networks);
        NetworkVisibility = sections.Pick(SurveySection.NetworkVisibility, contents.NetworkVisibility);
        Doors = sections.Pick(SurveySection.Doors, contents.Doors);
    }

    /// <summary>How to read the cells' strings; left out when sections leaves out cells.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Legend { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyCell>? Cells { get; }

    public int Count { get; }

    public int Offset { get; }

    public int Limit { get; }

    /// <summary>2 m cells in the box or room, over all pages.</summary>
    public int Total { get; }

    public bool HasMore { get; }

    /// <summary>Cables, pipes and chutes with a cell in this page's cells (with network_ids: on one of them).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyPieceView>? Pieces { get; }

    /// <summary>
    /// Devices with a cell in this page's cells, with their cable and pipe ports (with network_ids: those with a port
    /// on one of them).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyDeviceView>? Devices { get; }

    /// <summary>A RunCableNetworkView or RunPipeNetworkView per network of the pieces listed.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<object>? Networks { get; }

    /// <summary>
    /// Per network of the pieces listed: their cells by how visible a piece in them is, the floating ones (air) by
    /// position, and with include_refund what removing those pieces would give back.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyNetworkVisibilityView>? NetworkVisibility { get; }

    /// <summary>Doors on the faces of this page's cells, with their keep-out ('x' in support).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyDoorView>? Doors { get; }
}

/// <summary>grid_survey's legend: how to read each cell, by cell_detail.</summary>
internal static class SurveyLegends
{
    internal const string Occupancy =
        "Each cell is a 2 m cell at its centre (odd metres). empty: true when it holds nothing. frame_id: its frame. " +
        "faces: its faces +x, -x, +y, -y, +z, -z, each '.' nothing, 'w' wall, 'g' window, 'x' door (door over window " +
        "over wall); left out when all are '.'. small: its 64 small cells " +
        "(0.5 m) at -1, -0.5, 0 and +0.5 m from the centre along each axis, character index x + 4y + 16z: '.' empty, " +
        "'c' cable, 'p' pipe, 'b' cable and pipe, 'h' chute, 'd' device, 'o' another small-grid thing, 'r' a rocket's " +
        "empty cell; left out when all are '.'. cell_detail full adds room, frame build state, face structure ids and " +
        "the support string.";

    internal const string Full =
        "Each cell is a 2 m cell at its centre (odd metres). small: its 64 small-grid cells (0.5 m) at -1, -0.5, 0 " +
        "and +0.5 m from the centre along each axis (index 0 to 3; index 0 lies on the cell's minimum face plane, " +
        "shared with the neighbour), character index x + 4y + 16z. '.' empty, 'c' cable, 'p' pipe, 'b' cable and " +
        "pipe, 'h' chute, 'd' device, 'o' another small-grid thing (a mounted item, a rail), 'r' a rocket's empty cell " +
        "(its fuselage decides which kind of piece it takes). " +
        "Frames and walls never block cables or pipes; a device, chute or 'o' blocks both; a pipe blocks a cable " +
        "(and a cable a pipe) only along the axis its ends lie on. A chute needs a cell with no cable, pipe, device, " +
        "chute or 'o'. support: the same 64 cells by what holds a piece there up: 'i' inside a frame (every 2 m cell " +
        "the small cell touches holds a frame: hidden in the frame's body), 'e' a frame edge or corner, 'f' on a " +
        "frame's face (a frame's top face is the minimum plane of the cell above it), 'w' on a wall's plane, 'a' air " +
        "(plan_*_route style supported avoids 'a' cells); over those, 'x' a door's keep-out (its face and the " +
        "configured band either side inside its rectangle: the planners never route there without " +
        "allow_door_keepout; the floor slab under a threshold is not in it) and 'g' a window's face (routes pay " +
        "extra, crosses_window). A door's face is no wall support. network_visibility counts each listed network's cells the " +
        "same way (inside, frame_surface, wall, air) and lists the floating (air) ones.";
}

/// <summary>One network's listed pieces: their cells by visibility, where they float, and their removal refund.</summary>
internal sealed class SurveyNetworkVisibilityView
{
    internal SurveyNetworkVisibilityView(ThingId? networkId, string kind, int pieces, RouteVisibilityView cells,
        List<PositionView>? airAt, List<UpgradeAmountView>? refund)
    {
        NetworkId = networkId;
        Kind = kind;
        Pieces = pieces;
        Cells = cells;
        AirAt = airAt;
        Refund = refund;
    }

    /// <summary>Null for pieces on no network.</summary>
    public ThingId? NetworkId { get; }

    /// <summary>cable, pipe or chute.</summary>
    public string Kind { get; }

    public int Pieces { get; }

    public RouteVisibilityView Cells { get; }

    /// <summary>The cells in air (floating), up to 32; left out when none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PositionView>? AirAt { get; }

    /// <summary>include_refund: what remove_* would give back for these pieces; left out otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeAmountView>? Refund { get; }
}

internal sealed class SurveyContents
{
    internal SurveyContents(List<SurveyPieceView> pieces, List<SurveyDeviceView> devices, List<object> networks,
        List<SurveyNetworkVisibilityView> networkVisibility, List<SurveyDoorView>? doors = null)
    {
        Doors = doors ?? new List<SurveyDoorView>();
        Pieces = pieces;
        Devices = devices;
        Networks = networks;
        NetworkVisibility = networkVisibility;
    }

    internal List<SurveyNetworkVisibilityView> NetworkVisibility { get; }

    internal List<SurveyDoorView> Doors { get; }

    internal List<SurveyPieceView> Pieces { get; }

    internal List<SurveyDeviceView> Devices { get; }

    internal List<object> Networks { get; }
}

/// <summary>How much of each 2 m cell a grid_survey page tells: what occupies it, or everything known about it.</summary>
internal enum SurveyCellDetail
{
    /// <summary>Its frame, the kind of structure on each face, and its small cells when any holds something.</summary>
    Occupancy,

    /// <summary>Also its room, the frame's build state, each face structure by id and prefab, and the support string.</summary>
    Full
}

/// <summary>One 2 m cell of a grid_survey page, in the form cell_detail asks for.</summary>
internal abstract class SurveyCell
{
    private protected SurveyCell(PositionView at) => At = at;

    /// <summary>The cell's centre (odd metres); first in each cell.</summary>
    [JsonProperty(Order = -2)]
    public PositionView At { get; }
}

/// <summary>
/// One 2 m cell by what occupies it: empty when it holds nothing (no frame, no face structure, nothing in its small
/// cells: what occupied_only skips), its frame's id, its six faces as one string (FaceCode) and its small string
/// unless every small cell is '.'.
/// </summary>
internal sealed class SurveyOccupancyView : SurveyCell
{
    internal SurveyOccupancyView(PositionView at, ThingId? frameId, IReadOnlyList<SurveyFace> faces, string small)
        : base(at)
    {
        FrameId = frameId;
        Faces = faces.Count > 0 ? FaceCode.Encode(faces) : null;
        Small = SmallCellCode.ShowsAny(small) ? small : null;
        Empty = frameId == null && Faces == null && !SmallCellCode.HoldsAny(small) ? true : null;
    }

    /// <summary>true when the cell holds nothing; left out otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Empty { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? FrameId { get; }

    /// <summary>6 characters, faces +x, -x, +y, -y, +z, -z (see legend); left out when no face holds a structure.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Faces { get; }

    /// <summary>64 characters, index x + 4y + 16z (see legend); left out when every one is '.'.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Small { get; }
}

/// <summary>A structure on one face of a 2 m cell: the face (GridStep index) and the kind of opening it is.</summary>
internal readonly struct SurveyFace
{
    internal SurveyFace(GridStep face, OpeningKind kind)
    {
        Face = face;
        Kind = kind;
    }

    internal GridStep Face { get; }

    internal OpeningKind Kind { get; }
}

/// <summary>
/// A 2 m cell's six faces as one string, in GridStep order (+x, -x, +y, -y, +z, -z): '.' nothing, 'w' a wall, 'g' a
/// window, 'x' a door (the letters support uses for them). A face holding several shows the strongest: door over
/// window over wall.
/// </summary>
internal static class FaceCode
{
    internal static string Encode(IReadOnlyList<SurveyFace> faces)
    {
        OpeningKind?[] strongest = new OpeningKind?[GridStep.All.Length];
        foreach (SurveyFace face in faces)
        {
            OpeningKind? held = strongest[face.Face.Index];
            if (held == null || face.Kind > held)
            {
                strongest[face.Face.Index] = face.Kind;
            }
        }

        char[] code = new char[strongest.Length];
        for (int index = 0; index < code.Length; index++)
        {
            code[index] = strongest[index] switch
            {
                null => '.',
                OpeningKind.Door => 'x',
                OpeningKind.Window => 'g',
                _ => 'w'
            };
        }

        return new string(code);
    }
}

/// <summary>One 2 m cell in full: its centre, room (null outside), frame, face structures and small cells.</summary>
internal sealed class SurveyCellView : SurveyCell
{
    internal SurveyCellView(PositionView at, string? roomId, SurveyFrameView? frame, List<SurveyWallView> walls,
        string? small, string? support) : base(at)
    {
        Support = support;
        RoomId = roomId;
        Frame = frame;
        Walls = walls;
        Small = small;
    }

    public string? RoomId { get; }

    public SurveyFrameView? Frame { get; }

    public List<SurveyWallView> Walls { get; }

    /// <summary>64 characters, index x + 4y + 16z (see legend).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Small { get; }

    /// <summary>
    /// 64 characters in the same order: what holds a piece in each small cell up. 'i' inside a frame (every 2 m cell
    /// it touches holds one), 'e' a frame edge or corner, 'f' on a frame's face, 'w' on a wall's plane, 'a' air (the
    /// planners' frames_first avoids these); over those, 'x' in a door's keep-out (the planners never use it without
    /// allow_door_keepout) and 'g' on a window's face.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Support { get; }
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
    internal SurveyWallView(string face, ThingView wall, bool blocksAir, string kind = "wall")
    {
        Kind = kind;
        Face = face;
        ReferenceId = wall.ReferenceId;
        PrefabName = wall.PrefabName;
        BlocksAir = blocksAir;
    }

    public string Face { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public bool BlocksAir { get; }

    /// <summary>wall, window (any see-through face) or door (a doorway: it supports nothing, routes keep out).</summary>
    public string Kind { get; }
}

/// <summary>
/// A door seen in the page: the 2 m faces it covers, its face plane, and the keep-out the planners and place tools
/// apply around it (band_m either side of the plane inside its rectangle; its own port cells released).
/// </summary>
internal sealed class SurveyDoorView
{
    internal SurveyDoorView(ThingView door, List<PositionView> faces, string plane, double bandM,
        List<PositionView> portCells)
    {
        ReferenceId = door.ReferenceId;
        PrefabName = door.PrefabName;
        DisplayName = door.DisplayName;
        Faces = faces;
        Plane = plane;
        BandM = bandM;
        PortCells = portCells;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The centre of each 2 m face it covers.</summary>
    public List<PositionView> Faces { get; }

    /// <summary>Its face plane, e.g. "z=678".</summary>
    public string Plane { get; }

    public double BandM { get; }

    /// <summary>The cells a piece joining its ports stands in: released from its keep-out.</summary>
    public List<PositionView> PortCells { get; }
}

/// <summary>
/// A cable, pipe or chute piece: where it stands, when it fills more than one small cell their count and box (and with
/// include_piece_cells each cell), its ends as world axes from its own cells, its network and grade.
/// </summary>
internal sealed class SurveyPieceView
{
    internal SurveyPieceView(ThingView piece, string kind, PositionView at, PieceCellsView? extent,
        List<PositionView>? cells,
        List<string> ends, ThingId? networkId, string? grade, RunFlowView? flow = null, ThingView? carries = null,
        List<UpgradeAmountView>? refund = null)
    {
        Refund = refund;
        ReferenceId = piece.ReferenceId;
        Kind = kind;
        PrefabName = piece.PrefabName;
        At = at;
        CellCount = extent?.Count;
        CellBox = extent?.Box;
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

    /// <summary>The small cells a piece of more than one fills; left out for a one-cell piece.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? CellCount { get; }

    /// <summary>The box those cells fill; left out for a one-cell piece.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public BoxView? CellBox { get; }

    /// <summary>include_piece_cells: each cell's centre, for a piece of more than one cell.</summary>
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

    /// <summary>include_refund: what removing the piece gives back (remove_* refunds the same).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeAmountView>? Refund { get; }
}

/// <summary>How many small cells a piece fills, and the box they fill.</summary>
internal sealed class PieceCellsView
{
    internal PieceCellsView(int count, BoxView box)
    {
        Count = count;
        Box = box;
    }

    internal int Count { get; }

    internal BoxView Box { get; }
}

internal sealed class SurveyDeviceView
{
    internal SurveyDeviceView(ThingView device, PositionView at, List<SurveyPortView> ports,
        OrientationView? rotation = null, UmbilicalView? umbilical = null)
    {
        Rotation = rotation;
        Umbilical = umbilical;
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

    /// <summary>
    /// How the device stands turned: facing (its front), up and Euler degrees, the forms place_structure takes, so it
    /// can be placed again as it stands or turned (facing reversed: 180 degrees).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientationView? Rotation { get; }

    public List<SurveyPortView> Ports { get; }

    /// <summary>Its pairing when it is a rocket umbilical; absent otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public UmbilicalView? Umbilical { get; }
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
