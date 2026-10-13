#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// build blueprint's order (catalogue build.json): the blueprint file (blueprint), where the blueprint's anchor lands
/// (at, a position [x, y, z] in metres) and the turn its pieces get on top of the angle it was copied at (turn, 0, 90,
/// 180 or 270), with the what's report and undo mark. The paste service takes the order, so the option problems of
/// this what are its own, in verb words.
/// </summary>
internal sealed class PasteOrder
{
    /// <summary>The farthest an anchor may lie from the world's origin on any axis, in metres.</summary>
    internal const double MaximumCoordinateM = 100000.0;

    private PasteOrder(string blueprint, Vec3 at, PasteRotation turn, ReportConfirm report, WhatSpec spec)
    {
        Blueprint = blueprint;
        At = at;
        Turn = turn;
        Report = report;
        Label = spec.Label;
        Mark = spec.Write!.UndoMark;
    }

    /// <summary>The blueprint file: a name in BlueprintMod's Blueprints folder, or an absolute path.</summary>
    public string Blueprint { get; }

    /// <summary>Where the blueprint's anchor lands, in metres.</summary>
    public Vec3 At { get; }

    /// <summary>The turn added to the angle the blueprint was copied at.</summary>
    public PasteRotation Turn { get; }

    /// <summary>confirm: whether this call acts, and how many rows a report lists.</summary>
    public ReportConfirm Report { get; }

    /// <summary>The call's label, as its refusals prefix it: "build blueprint".</summary>
    public string Label { get; }

    /// <summary>The what's undo mark, as every piece of a report carries it.</summary>
    public string Mark { get; }

    /// <summary>The order the call's options give; null with every problem added.</summary>
    internal static PasteOrder? Of(BuildBlueprintOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (string.IsNullOrEmpty(options.Blueprint))
        {
            problems.Add(new OptionProblem("blueprint",
                "is a blueprint's file name in BlueprintMod's folder, or an absolute path"));
        }

        bool point = AtOf(options.At, problems, out Vec3 at);
        bool turned = TurnOf(options.Turn, problems, out PasteRotation turn);
        return problems.Count == before
            ? new PasteOrder(options.Blueprint!, at, turn, options.Report, BuildBlueprintOptions.Spec)
            : null;
    }

    // A position in metres: [x, y, z] of finite numbers inside the world (Point is the shared reader).
    private static bool AtOf(JToken? given, List<OptionProblem> problems, out Vec3 at)
    {
        at = default;
        if (given is not JArray list || Point.Of(list) is not { } point || !Finite(point))
        {
            problems.Add(new OptionProblem("at", "is the anchor's position [x, y, z] in metres"));
            return false;
        }

        if (Math.Abs(point.X) > MaximumCoordinateM || Math.Abs(point.Y) > MaximumCoordinateM ||
            Math.Abs(point.Z) > MaximumCoordinateM)
        {
            problems.Add(new OptionProblem("at", $"is beyond {MaximumCoordinateM:0} m of the world origin"));
            return false;
        }

        at = point;
        return true;
    }

    private static bool Finite(Vec3 point) =>
        !double.IsNaN(point.X) && !double.IsInfinity(point.X) && !double.IsNaN(point.Y) &&
        !double.IsInfinity(point.Y) && !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);

    // The quarter turn the blueprint is pasted at, as the catalogue's enum offers it.
    private static bool TurnOf(long? given, List<OptionProblem> problems, out PasteRotation turn)
    {
        long degrees = given ?? 0;
        PasteRotation? quarter = degrees is 0 or 90 or 180 or 270 ? PasteRotation.FromDegrees((int)degrees) : null;
        turn = quarter ?? PasteRotation.None;
        if (quarter == null)
        {
            problems.Add(new OptionProblem("turn", "is 0, 90, 180 or 270 degrees"));
            return false;
        }

        return true;
    }
}
