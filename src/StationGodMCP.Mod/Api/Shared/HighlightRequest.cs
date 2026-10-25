#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>A colour as red, green, blue and alpha from 0 to 1.</summary>
internal readonly struct Tint
{
    internal Tint(double red, double green, double blue, double alpha = 1.0)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    internal double Red { get; }

    internal double Green { get; }

    internal double Blue { get; }

    internal double Alpha { get; }
}

/// <summary>What one highlight target names, and how it is drawn.</summary>
internal abstract class HighlightTarget
{
    private HighlightTarget(Tint tint, string colorName, string? label, bool pulse)
    {
        Tint = tint;
        ColorName = colorName;
        Label = label;
        Pulse = pulse;
    }

    internal Tint Tint { get; }

    /// <summary>The palette name, or "custom" for an [r, g, b] colour.</summary>
    internal string ColorName { get; }

    internal string? Label { get; }

    internal bool Pulse { get; }

    /// <summary>Things by reference id (id, one or a list): their own meshes.</summary>
    internal sealed class Things : HighlightTarget
    {
        internal Things(List<ThingId> ids, Tint tint, string colorName, string? label, bool pulse)
            : base(tint, colorName, label, pulse)
        {
            Ids = ids;
        }

        internal List<ThingId> Ids { get; }
    }

    /// <summary>A cable, pipe or chute network (its id, or any piece on it): every piece of it.</summary>
    internal sealed class Network : HighlightTarget
    {
        internal Network(ThingId id, Tint tint, string colorName, string? label, bool pulse)
            : base(tint, colorName, label, pulse)
        {
            Id = id;
        }

        internal ThingId Id { get; }
    }

    /// <summary>A world point (at): a beam standing on it, and an arrow to it from the screen's edge.</summary>
    internal sealed class Point : HighlightTarget
    {
        internal Point(Vec3 at, Tint tint, string colorName, string? label, bool pulse)
            : base(tint, colorName, label, pulse)
        {
            At = at;
        }

        internal Vec3 At { get; }
    }

    /// <summary>A board ghost (find ghost's id): a beacon where it stands, and with Lattice the ghost itself lit.</summary>
    internal sealed class Ghost : HighlightTarget
    {
        internal Ghost(long id, Tint tint, string colorName, string? label, bool pulse)
            : base(tint, colorName, label, pulse)
        {
            Id = id;
        }

        internal long Id { get; }
    }
}

/// <summary>
/// show highlight's request: the targets (each one of id, network or at, with its own colour, label and pulse), how
/// long they show and whether what earlier calls drew stays. Read from the call by HighlightOrder; a target without a
/// colour takes the next palette colour in turn, so neighbouring targets differ.
/// </summary>
internal sealed class HighlightRequest
{
    /// <summary>The colour name an [r, g, b] colour has: no palette colour was named.</summary>
    internal const string Custom = "custom";

    private static readonly (string Name, Tint Tint)[] Palette =
    {
        ("cyan", new Tint(0.2, 0.9, 1.0)),
        ("magenta", new Tint(1.0, 0.25, 0.9)),
        ("yellow", new Tint(1.0, 0.9, 0.2)),
        ("green", new Tint(0.2, 1.0, 0.3)),
        ("orange", new Tint(1.0, 0.55, 0.1)),
        ("blue", new Tint(0.25, 0.45, 1.0)),
        ("red", new Tint(1.0, 0.2, 0.2)),
        ("white", new Tint(1.0, 1.0, 1.0))
    };

    private static readonly IReadOnlyList<string> PaletteNames = Names();

    internal HighlightRequest(List<HighlightTarget> targets, double seconds, bool keep)
    {
        Targets = targets;
        Seconds = seconds;
        Keep = keep;
    }

    internal List<HighlightTarget> Targets { get; }

    internal double Seconds { get; }

    /// <summary>Keep what earlier calls drew (default: a new call replaces them).</summary>
    internal bool Keep { get; }

    /// <summary>The palette's colour names, in the order targets without a colour take them.</summary>
    internal static IReadOnlyList<string> ColorNames => PaletteNames;

    /// <summary>The colour a target without one takes: the palette in turn by the target's index.</summary>
    internal static (Tint Tint, string Name) Turn(int index)
    {
        (string name, Tint tint) = Palette[index % Palette.Length];
        return (tint, name);
    }

    /// <summary>A palette colour by the name given (trimmed, any case); null when no palette colour is named so.</summary>
    internal static (Tint Tint, string Name)? ByName(string name)
    {
        foreach ((string known, Tint tint) in Palette)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            {
                return (tint, known);
            }
        }

        return null;
    }

    private static List<string> Names()
    {
        List<string> names = new List<string>(Palette.Length);
        foreach ((string name, Tint _) in Palette)
        {
            names.Add(name);
        }

        return names;
    }
}
