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
