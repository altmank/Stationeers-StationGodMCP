#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>The surface a piece rests on and the rectangle it covers there.</summary>
internal sealed class MountView
{
    internal MountView(MountRect rect)
    {
        Plane = rect.Plane.ToString();
        Outward = rect.Outward.Name;
        Axes = new List<string> { "xyz"[rect.U].ToString(), "xyz"[rect.V].ToString() };
        Min = new List<double> { System.Math.Round(rect.MinU, 2), System.Math.Round(rect.MinV, 2) };
        Max = new List<double> { System.Math.Round(rect.MaxU, 2), System.Math.Round(rect.MaxV, 2) };
    }

    /// <summary>The face plane behind it ("z=668").</summary>
    public string Plane { get; }

    /// <summary>The way it stands out of the plane (its front when mounted, its top when standing).</summary>
    public string Outward { get; }

    /// <summary>The plane's two in-plane axes, in the order of min and max.</summary>
    public List<string> Axes { get; }

    public List<double> Min { get; }

    public List<double> Max { get; }
}

/// <summary>Where a placed piece would take room: its small cells, 2 m cells, body and mount.</summary>
internal sealed class FootprintView
{
    internal FootprintView(CellListView smallCells, List<PositionView> largeCells, BodyView body, MountView? mount)
    {
        SmallCells = smallCells;
        LargeCells = largeCells;
        Body = body;
        Mount = mount;
    }

    /// <summary>The small cells the game would register it in (GridBounds turned and moved).</summary>
    public CellListView SmallCells { get; }

    /// <summary>The 2 m cells a grid-placed structure takes; empty for small-grid pieces.</summary>
    public List<PositionView> LargeCells { get; }

    public BodyView Body { get; }

    /// <summary>The face plane it rests on and the rectangle it covers there; null when it rests on none.</summary>
    public MountView? Mount { get; }
}

/// <summary>A face structure a mounted piece rests on.</summary>
internal sealed class SectionWallView
{
    internal SectionWallView(PointView face, ThingView? wall, string kind)
    {
        Face = face;
        ReferenceId = wall?.ReferenceId;
        PrefabName = wall?.PrefabName;
        Kind = kind;
    }

    /// <summary>The 2 m face's centre.</summary>
    public PointView Face { get; }

    /// <summary>The wall, window or door there; null for an empty face.</summary>
    public ThingId? ReferenceId { get; }

    public string? PrefabName { get; }

    /// <summary>wall, window, door or none.</summary>
    public string Kind { get; }
}

/// <summary>The wall sections (2 m faces) a mounted piece spans, and whether it crosses a seam between them.</summary>
internal sealed class SectionsView
{
    internal SectionsView(List<SectionWallView> walls, bool crossesSeam)
    {
        Walls = walls;
        Count = walls.Count;
        CrossesSeam = crossesSeam;
    }

    public List<SectionWallView> Walls { get; }

    public int Count { get; }

    /// <summary>It spans more than one 2 m face: a warning (crosses_section_seam), never a refusal.</summary>
    public bool CrossesSeam { get; }
}

/// <summary>One layout finding: code, level (info, warning, problem), message and the other thing involved.</summary>
internal sealed class ConflictView
{
    internal ConflictView(LayoutConflict conflict)
    {
        Code = conflict.Code;
        Level = conflict.Level.ToString().ToLowerInvariant();
        Message = conflict.Message;
        ReferenceId = conflict.OtherId.HasValue ? new ThingId(conflict.OtherId.Value) : (ThingId?)null;
    }

    public string Code { get; }

    public string Level { get; }

    public string Message { get; }

    /// <summary>The other thing involved, when there is one.</summary>
    public ThingId? ReferenceId { get; }
}

/// <summary>A port of a planned device checked against what stands at its joining cell now.</summary>
internal sealed class PortCheckView
{
    internal PortCheckView(int index, PositionView at, string toward, string type, string role, string? flow,
        ThingView? occupant, bool joins, ThingId? wouldJoinNetwork, string? blocked, bool inDoorKeepOut)
    {
        Index = index;
        At = at;
        Toward = toward;
        Type = type;
        Role = role;
        Flow = flow;
        Occupant = occupant;
        Joins = joins;
        WouldJoinNetwork = wouldJoinNetwork;
        Blocked = blocked;
        InDoorKeepOut = inDoorKeepOut;
    }

    public int Index { get; }

    /// <summary>The cell a piece joining it stands in.</summary>
    public PositionView At { get; }

    /// <summary>The end that piece needs, into the device.</summary>
    public string Toward { get; }

    public string Type { get; }

    public string Role { get; }

    /// <summary>in (an input), out (an output or waste), or null.</summary>
    public string? Flow { get; }

    /// <summary>What stands in the joining cell now (the piece of the port's kind, or whatever blocks it).</summary>
    public ThingView? Occupant { get; }

    /// <summary>A piece of the port's kind stands there with an end toward the port: it joins on build.</summary>
    public bool Joins { get; }

    /// <summary>The network that piece is on, which the device joins when built.</summary>
    public ThingId? WouldJoinNetwork { get; }

    /// <summary>Why no piece can join the port there, or that the piece there has no end toward it; null when free.</summary>
    public string? Blocked { get; }

    public bool InDoorKeepOut { get; }
}

/// <summary>place_structure's layout preview of one placement.</summary>
internal sealed class PlacementLayoutView
{
    internal PlacementLayoutView(FootprintView footprint, SectionsView? sections, List<ConflictView> conflicts,
        List<PortCheckView>? portChecks)
    {
        Footprint = footprint;
        Sections = sections;
        Conflicts = conflicts;
        PortChecks = portChecks;
    }

    public FootprintView Footprint { get; }

    /// <summary>The wall sections a piece resting on a face plane spans; null otherwise.</summary>
    public SectionsView? Sections { get; }

    public List<ConflictView> Conflicts { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PortCheckView>? PortChecks { get; }
}

/// <summary>One turn orient scored: the turn, its score (lower is better; null when excluded) and why.</summary>
internal sealed class OrientChoiceView
{
    internal OrientChoiceView(OrientationView orientation, double? score, List<string> reasons, bool reversedFlow)
    {
        Facing = orientation.Facing;
        Up = orientation.Up;
        Euler = orientation.Euler;
        Score = score;
        Reasons = reasons;
        ReversedFlow = reversedFlow;
    }

    public string? Facing { get; }

    public string? Up { get; }

    public RotationView Euler { get; }

    public double? Score { get; }

    public List<string> Reasons { get; }

    /// <summary>Scored with the device's flow reversed by its Mode (see mode_flip).</summary>
    public bool ReversedFlow { get; }
}

/// <summary>A logic write that makes the chosen turn meet the flow: a turbo volume pump's Mode.</summary>
internal sealed class ModeFlipView
{
    internal ModeFlipView(string logicType, int value, string reason)
    {
        LogicType = logicType;
        Value = value;
        Reason = reason;
    }

    public string LogicType { get; }

    public int Value { get; }

    public string Reason { get; }
}

/// <summary>orient's result: the turn chosen, the next best, and a Mode flip when the flow needs one.</summary>
internal sealed class OrientResultView
{
    internal OrientResultView(OrientChoiceView? chosen, List<OrientChoiceView> alternatives, int tried,
        ModeFlipView? modeFlip)
    {
        Chosen = chosen;
        Alternatives = alternatives;
        Tried = tried;
        ModeFlip = modeFlip;
    }

    /// <summary>Null when no turn can be built where asked (every one excluded).</summary>
    public OrientChoiceView? Chosen { get; }

    public List<OrientChoiceView> Alternatives { get; }

    /// <summary>How many turns were tried.</summary>
    public int Tried { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ModeFlipView? ModeFlip { get; }
}

/// <summary>A prefab's port in its own frame: its joining cell's offset from the origin and the way a run leaves.</summary>
internal sealed class PrefabPortView
{
    internal PrefabPortView(int index, string type, string role, string? flow, PointView at, string outward)
    {
        Index = index;
        Type = type;
        Role = role;
        Flow = flow;
        At = at;
        Outward = outward;
    }

    public int Index { get; }

    public string Type { get; }

    public string Role { get; }

    public string? Flow { get; }

    /// <summary>The joining cell's offset from the origin, unturned (metres, x right, y up, z forward).</summary>
    public PointView At { get; }

    /// <summary>The way a run leaves the port, unturned.</summary>
    public string Outward { get; }
}

/// <summary>Which of a prefab's own axes reads as its top, and how that is known.</summary>
internal sealed class VisualUpView
{
    internal VisualUpView(string local, bool lyingAllowed, string source, bool verified)
    {
        Local = local;
        LyingAllowed = lyingAllowed;
        Source = source;
        Verified = verified;
    }

    public string Local { get; }

    public bool LyingAllowed { get; }

    public string Source { get; }

    /// <summary>false: a guess not yet seen in a live game.</summary>
    public bool Verified { get; }
}

/// <summary>describe_prefab: a buildable prefab in its own frame.</summary>
internal sealed class DescribePrefabView
{
    internal DescribePrefabView(PlacementPrefabView prefab, string runtimeType, string placement, float gridSizeM,
        bool smallGrid, string rotationAxes, List<OrientationView> allowedRotations, List<PointView> smallCells,
        BoxView renderBox, BoxView? gridBox, List<PrefabPortView> ports, VisualUpView visualUp,
        ModeFlipView? reversibleFlow, bool hasCursor)
    {
        PrefabName = prefab.PrefabName;
        PrefabHash = prefab.PrefabHash;
        DisplayName = prefab.DisplayName;
        BuildStates = prefab.BuildStates;
        RuntimeType = runtimeType;
        Placement = placement;
        GridSizeM = gridSizeM;
        SmallGrid = smallGrid;
        RotationAxes = rotationAxes;
        AllowedRotations = allowedRotations;
        SmallCells = smallCells;
        RenderBox = renderBox;
        GridBox = gridBox;
        Ports = ports;
        VisualUp = visualUp;
        ReversibleFlow = reversibleFlow;
        HasCursor = hasCursor;
    }

    public string? PrefabName { get; }

    public int? PrefabHash { get; }

    public string? DisplayName { get; }

    public int? BuildStates { get; }

    public string RuntimeType { get; }

    /// <summary>grid, face or face_mount: how the cursor snaps it.</summary>
    public string Placement { get; }

    public float GridSizeM { get; }

    public bool SmallGrid { get; }

    /// <summary>The axes the cursor turns a grid-placed prefab about.</summary>
    public string RotationAxes { get; }

    /// <summary>The turns place_structure accepts (a face-mounted piece: every turn; the cursor check decides).</summary>
    public List<OrientationView> AllowedRotations { get; }

    /// <summary>The small cells it takes, as offsets from its origin, unturned.</summary>
    public List<PointView> SmallCells { get; }

    /// <summary>The box its meshes fill, relative to its origin, unturned.</summary>
    public BoxView RenderBox { get; }

    /// <summary>The small cells' box relative to its origin (its real footprint); null for 2 m structures.</summary>
    public BoxView? GridBox { get; }

    public List<PrefabPortView> Ports { get; }

    public VisualUpView VisualUp { get; }

    /// <summary>A logic Mode that reverses its flow (turbo volume pumps); null otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ModeFlipView? ReversibleFlow { get; }

    /// <summary>The game has a placement cursor for it (origin snapped as placing snaps it).</summary>
    public bool HasCursor { get; }
}

/// <summary>A relative at or a named facing as the world resolved it, and how.</summary>
internal sealed class ResolvedPlacementView
{
    internal ResolvedPlacementView(PointView at, string atHow, string? facing, string? facingHow)
    {
        At = at;
        AtHow = atHow;
        Facing = facing;
        FacingHow = facingHow;
    }

    /// <summary>The point the placement is aimed at, before the cursor snaps it.</summary>
    public PointView At { get; }

    public string AtHow { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Facing { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? FacingHow { get; }
}

/// <summary>A device or mounted thing on a wall map, by its key.</summary>
internal sealed class WallThingView
{
    internal WallThingView(string key, ThingView thing, PositionView position)
    {
        Key = key;
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Position = position;
    }

    public string Key { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }
}

/// <summary>One 2 m face of a wall map: its centre, what it shows and the structure there.</summary>
internal sealed class WallSectionView
{
    internal WallSectionView(PointView face, string look, ThingView? structure)
    {
        Face = face;
        Look = look;
        ReferenceId = structure?.ReferenceId;
        PrefabName = structure?.PrefabName;
    }

    public PointView Face { get; }

    /// <summary>wall, window, door, frame or open.</summary>
    public string Look { get; }

    public ThingId? ReferenceId { get; }

    public string? PrefabName { get; }
}

/// <summary>A rectangle of free wall: its top-left row and column on the map, centre and size in small cells.</summary>
internal sealed class FreeRectView
{
    internal FreeRectView(int row, int column, PointView centre, int cellsWide, int cellsHigh)
    {
        Row = row;
        Column = column;
        Centre = centre;
        CellsWide = cellsWide;
        CellsHigh = cellsHigh;
    }

    public int Row { get; }

    public int Column { get; }

    /// <summary>The rectangle's centre on the plane, metres.</summary>
    public PointView Centre { get; }

    public int CellsWide { get; }

    public int CellsHigh { get; }
}

/// <summary>wall_map: the plane, the side it is seen from, the text rows and what they show.</summary>
internal sealed class WallMapView
{
    internal WallMapView(string plane, string side, string right, string up, List<string> rows, PointView topLeft,
        List<WallSectionView> sections, List<WallThingView> things, List<FreeRectView>? freeRects, string legend)
    {
        Plane = plane;
        Side = side;
        Right = right;
        Up = up;
        Rows = rows;
        TopLeft = topLeft;
        Sections = sections;
        Things = things;
        FreeRects = freeRects;
        Legend = legend;
    }

    public string Plane { get; }

    /// <summary>The side it is seen from (the viewer stands on this side).</summary>
    public string Side { get; }

    /// <summary>The world axis a column step runs along (left to right as seen).</summary>
    public string Right { get; }

    /// <summary>The world axis up a row runs along (+y on a wall).</summary>
    public string Up { get; }

    /// <summary>The ruler row (seams) then one row per 0.5 m, top first.</summary>
    public List<string> Rows { get; }

    /// <summary>The coordinates of the first map row's first cell along right and up (x: right, y: up; z unused).</summary>
    public PointView TopLeft { get; }

    public List<WallSectionView> Sections { get; }

    public List<WallThingView> Things { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<FreeRectView>? FreeRects { get; }

    public string Legend { get; }
}

/// <summary>One spot find_spot offers: where, turned how, how far, its layout findings and ready arguments.</summary>
internal sealed class SpotView
{
    internal SpotView(PointView position, OrientationView orientation, double distanceM, int penalty, string plane,
        List<ConflictView> conflicts, Newtonsoft.Json.Linq.JObject placeArguments)
    {
        Position = position;
        Orientation = orientation;
        DistanceM = distanceM;
        Penalty = penalty;
        Plane = plane;
        Conflicts = conflicts;
        PlaceArguments = placeArguments;
    }

    /// <summary>Where it would stand, snapped as the cursor snaps it.</summary>
    public PointView Position { get; }

    public OrientationView Orientation { get; }

    /// <summary>From the point asked (near).</summary>
    public double DistanceM { get; }

    /// <summary>The layout preview's conflicts weighed (warning 10, info 1); lower is better.</summary>
    public int Penalty { get; }

    public string Plane { get; }

    public List<ConflictView> Conflicts { get; }

    /// <summary>place_structure's arguments for it (a dry run by default).</summary>
    public Newtonsoft.Json.Linq.JObject PlaceArguments { get; }
}

/// <summary>find_spot: the spots best first, and how many were tried, filtered and checked.</summary>
internal sealed class FindSpotView
{
    internal FindSpotView(string? prefabName, List<string> planes, List<SpotView> spots, int tried, int filtered,
        int checkedCount, int rejected)
    {
        PrefabName = prefabName;
        Planes = planes;
        Spots = spots;
        Tried = tried;
        Filtered = filtered;
        Checked = checkedCount;
        Rejected = rejected;
    }

    public string? PrefabName { get; }

    public List<string> Planes { get; }

    public List<SpotView> Spots { get; }

    /// <summary>Spots aimed (distinct snapped positions).</summary>
    public int Tried { get; }

    /// <summary>Ruled out on geometry alone (taken cells, door keep-out, sections, height, front).</summary>
    public int Filtered { get; }

    /// <summary>Checked with the game's cursor and the layout preview (at most max_checks).</summary>
    public int Checked { get; }

    /// <summary>Of those, ruled out (cursor refusal, a problem, visual overlap, a seam, an unreachable port).</summary>
    public int Rejected { get; }
}
