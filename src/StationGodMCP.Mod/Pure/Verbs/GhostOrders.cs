#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.RemoteView;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// show ghost's call, checked without the game: placements (as show preview reads them: the list, or one at the top
/// level; a top-level label goes to every placement without its own), the tint, ghost ids to take off (remove) and
/// clear (agent: every ghost agents placed; all: every ghost), and ghost ids to restyle in place (restyle: the look
/// options, tint and label given change, the rest of each ghost's look is kept). The request the game plans is free, as
/// a preview's. Look options with nothing placed or restyled are refused, never dropped.
/// </summary>
internal sealed class GhostOrder
{
    private const int MaximumIds = 256;

    private static readonly string[] ClearWords = { "agent", "all" };

    private GhostOrder(PlaceRequest arguments, Dictionary<int, Rgba> tints, Rgba tint, List<long> remove, string? clear,
        string? label, Dictionary<int, string> labels, RunBuildOrder? run, GhostLook look, GhostLookChange change,
        List<long> restyle, Rgba? restyleTint)
    {
        Look = look;
        LookChange = change;
        Restyle = restyle;
        RestyleTint = restyleTint;
        Run = run;
        Labels = labels;
        Arguments = arguments;
        Tints = tints;
        Tint = tint;
        Remove = remove;
        Clear = clear;
        Label = label;
    }

    internal PlaceRequest Arguments { get; }

    internal Dictionary<int, Rgba> Tints { get; }

    internal Rgba Tint { get; }

    internal List<long> Remove { get; }

    /// <summary>agent, all, or null.</summary>
    internal string? Clear { get; }

    /// <summary>The call's label (each placement's own label wins; the place readers carry those).</summary>
    internal string? Label { get; }

    internal bool HasPlacements => Arguments.Placements.Count > 0;

    /// <summary>run: build cable, pipe or chute's options (as show preview reads them), its new pieces the ghosts.</summary>
    internal RunBuildOrder? Run { get; }

    /// <summary>The look options: how placed ghosts are drawn in Lattice (a placement with problems is marked as it goes on).</summary>
    internal GhostLook Look { get; }

    /// <summary>The look options as given, for restyle: each ghost's own look with these changed.</summary>
    internal GhostLookChange LookChange { get; }

    /// <summary>Ghost ids to restyle in place.</summary>
    internal List<long> Restyle { get; }

    /// <summary>The tint restyled ghosts take; null keeps each one's.</summary>
    internal Rgba? RestyleTint { get; }

    internal Rgba TintOf(int index) => Tints.TryGetValue(index, out Rgba own) ? own : Tint;

    /// <summary>The placements' labels by index (the call's label already in each placement without its own).</summary>
    internal Dictionary<int, string> Labels { get; }

    internal string? LabelOf(int index) => Labels.TryGetValue(index, out string? label) ? label : null;

    internal static GhostOrder? Of(ShowGhostOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        Dictionary<int, Rgba> tints = new Dictionary<int, Rgba>();
        Dictionary<int, string> labels = new Dictionary<int, string>();
        List<PlacementRequest> placements = PreviewOrder.Placements(GivenPlacements.Of(options), tints, labels,
            problems);
        Rgba tint = GhostTints.Default;
        if (options.Tint != null && GhostTints.TryRead(options.Tint, "tint", problems, out Rgba given))
        {
            tint = given;
        }

        List<long> remove = Ids(options.Remove, "remove", problems);
        List<long> restyle = Ids(options.Restyle, "restyle", problems);
        RunBuildOrder? run = PreviewOrder.RunOf(options.Run, problems);
        GhostLookChange change = new GhostLookChange(options.Look, options.Xray, options.XrayStrength, options.XrayRange,
            options.Markers, options.Style, options.XrayOpacity);
        GhostLook? look = GhostLook.Plain.Change(change, problems);
        if (change.Any && placements.Count == 0 && options.Run == null && restyle.Count == 0)
        {
            problems.Add(new OptionProblem("look", "and the other look options draw placed or restyled ghosts: give " +
                "placements, run or restyle (ghost ids)"));
        }

        if (restyle.Count > 0 && !change.Any && options.Tint == null && options.Label == null)
        {
            problems.Add(new OptionProblem("restyle", "changes look, xray, xray_strength, xray_range, xray_opacity, markers, style, " +
                "tint or label: give at least one"));
        }

        string? clear = options.Clear?.Trim().ToLowerInvariant();
        if (clear != null && Array.IndexOf(ClearWords, clear) < 0)
        {
            problems.Add(new OptionProblem("clear", "is agent or all", NearestNames.Of(clear, ClearWords)));
        }

        if (problems.Count == before && placements.Count == 0 && remove.Count == 0 && clear == null &&
            options.Run == null && restyle.Count == 0)
        {
            problems.Add(new OptionProblem("placements",
                "is required unless run, remove, restyle or clear is given: placements, or prefab and at"));
        }

        return problems.Count == before
            ? new GhostOrder(new PlaceRequest(placements, null, true, Allow.Lifts(options.Allow, "door_keepout"),
                detail: new ReportDetail(false, true)), tints, tint, remove, clear, options.Label, labels, run,
                look!, change, restyle, options.Tint != null ? tint : (Rgba?)null)
            : null;
    }

    // remove, restyle: ghost ids, whole numbers above 0.
    private static List<long> Ids(JToken? given, string name, List<OptionProblem> problems)
    {
        List<long> ids = new List<long>();
        if (given == null)
        {
            return ids;
        }

        if (!(given is JArray items) || items.Count > MaximumIds)
        {
            problems.Add(new OptionProblem(name, $"is a list of up to {MaximumIds} ghost ids"));
            return ids;
        }

        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Type == JTokenType.Integer && (long)items[index] > 0)
            {
                ids.Add((long)items[index]);
                continue;
            }

            problems.Add(new OptionProblem($"{name}[{index}]", "is a ghost id (find ghost lists them)"));
        }

        return ids;
    }
}

/// <summary>
/// find ghost's filter, without the game: ids, prefab, owner (a player's name, any case, or "agent"), source, and near
/// with radius; nearest first around near, else by id.
/// </summary>
internal sealed class GhostFilter
{
    private readonly HashSet<long>? _ids;
    private readonly PrefabFilter? _prefab;
    private readonly string? _owner;
    private readonly GhostSource? _source;

    internal GhostFilter(IEnumerable<long>? ids, PrefabFilter? prefab, string? owner, GhostSource? source)
    {
        _ids = ids != null ? new HashSet<long>(ids) : null;
        _prefab = prefab;
        _owner = owner?.Trim();
        _source = source;
    }

    /// <summary>The kept ghosts with their distances from near (null without near), in order.</summary>
    internal List<(BoardGhost Ghost, double? Distance)> Apply(IReadOnlyList<BoardGhost> board, Vec3? near,
        double? radius)
    {
        List<(BoardGhost, double?)> kept = new List<(BoardGhost, double?)>();
        foreach (BoardGhost ghost in board)
        {
            if (!Keeps(ghost))
            {
                continue;
            }

            double? distance = near is { } at ? Distance(ghost.Position, at) : (double?)null;
            if (distance.HasValue && radius.HasValue && distance.Value > radius.Value)
            {
                continue;
            }

            kept.Add((ghost, distance));
        }

        kept.Sort((a, b) => a.Item2.HasValue && b.Item2.HasValue && a.Item2.Value != b.Item2.Value
            ? a.Item2.Value.CompareTo(b.Item2.Value)
            : a.Item1.Id.CompareTo(b.Item1.Id));
        return kept;
    }

    private bool Keeps(BoardGhost ghost) =>
        (_ids == null || _ids.Contains(ghost.Id)) &&
        (_prefab == null || _prefab.Keeps(ghost.PrefabName, ghost.PrefabHash)) &&
        (_source == null || ghost.Source == _source) &&
        (string.IsNullOrEmpty(_owner) || string.Equals(ghost.Owner, _owner, StringComparison.OrdinalIgnoreCase));

    private static double Distance(Vec3 a, Vec3 b)
    {
        double x = a.X - b.X;
        double y = a.Y - b.Y;
        double z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>A ghost's build structure options: what, prefab, at, rotation and build_state.</summary>
    internal static JObject BuildArguments(BoardGhost ghost) => new JObject
    {
        ["what"] = "structure",
        ["prefab"] = ghost.PrefabName,
        ["at"] = new JArray(ghost.Position.X, ghost.Position.Y, ghost.Position.Z),
        ["rotation"] = new JArray(Math.Round(ghost.Euler.X, 3), Math.Round(ghost.Euler.Y, 3),
            Math.Round(ghost.Euler.Z, 3)),
        ["build_state"] = ghost.BuildState
    };

    /// <summary>The listed ghosts' build structure options as one build (up to 64 placements); null for none.</summary>
    internal static JObject? BuildArguments(IReadOnlyList<BoardGhost> ghosts)
    {
        if (ghosts.Count == 0)
        {
            return null;
        }

        JArray placements = new JArray();
        for (int index = 0; index < ghosts.Count && index < StructureBuildOrder.MaximumPlacements; index++)
        {
            JObject placement = BuildArguments(ghosts[index]);
            placement.Remove("what");
            placements.Add(placement);
        }

        return new JObject { ["what"] = "structure", ["placements"] = placements };
    }
}
