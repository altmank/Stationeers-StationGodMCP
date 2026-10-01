#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
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

    /// <summary>Things by reference id (reference_id or reference_ids): their own meshes.</summary>
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
}

/// <summary>
/// highlight's arguments: targets (each exactly one of reference_id, reference_ids, network_id or at, with an
/// optional color, label and pulse), seconds, keep and clear. A target without a colour takes the next palette colour
/// in turn, so neighbouring targets differ.
/// </summary>
internal sealed class HighlightRequest
{
    internal const double DefaultSeconds = 60.0;
    internal const double MaximumSeconds = 600.0;
    internal const int MaximumTargets = 64;
    internal const int MaximumIds = 1024;
    private const int MaximumLabel = 80;

    private static readonly string[] TargetForms = { "reference_id", "reference_ids", "network_id", "at" };

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

    private HighlightRequest(List<HighlightTarget> targets, double seconds, bool keep)
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
    internal static IEnumerable<string> ColorNames
    {
        get
        {
            foreach ((string name, Tint _) in Palette)
            {
                yield return name;
            }
        }
    }

    internal static HighlightRequest Parse(Args args)
    {
        double seconds = args.OptionalPositiveDouble("seconds") ?? DefaultSeconds;
        if (seconds > MaximumSeconds)
        {
            throw ApiErrors.InvalidArgument($"seconds is at most {MaximumSeconds}.");
        }

        List<Args?> items = args.Objects("targets", MaximumTargets);
        List<HighlightTarget> targets = new List<HighlightTarget>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            string path = $"targets[{index}]";
            targets.Add(Target(items[index] ?? throw ApiErrors.InvalidArgument($"{path} must be an object."), path,
                index));
        }

        return new HighlightRequest(targets, seconds, args.OptionalBool("keep") ?? false);
    }

    private static HighlightTarget Target(Args item, string path, int index)
    {
        int forms = 0;
        foreach (string form in TargetForms)
        {
            forms += item.Has(form) ? 1 : 0;
        }

        if (forms != 1)
        {
            throw ApiErrors.InvalidArgument($"{path} takes exactly one of {string.Join(", ", TargetForms)}.");
        }

        (Tint tint, string name) = ColorOf(item.Optional("color"), path, index);
        string? label = Label(item.OptionalString("label"), path);
        bool pulse = item.OptionalBool("pulse") ?? false;
        if (item.Has("at"))
        {
            Metres at = BuildArgs.PositionOf(item.Optional("at")!, $"{path}.at");
            return new HighlightTarget.Point(new Vec3(at.X, at.Y, at.Z), tint, name, label, pulse);
        }

        if (item.Has("network_id"))
        {
            return new HighlightTarget.Network(item.ThingId("network_id"), tint, name, label, pulse);
        }

        List<ThingId> ids = item.Has("reference_id")
            ? new List<ThingId> { item.ThingId("reference_id") }
            : item.ThingIds("reference_ids", MaximumIds);
        return new HighlightTarget.Things(ids, tint, name, label, pulse);
    }

    private static string? Label(string? text, string path)
    {
        if (text == null)
        {
            return null;
        }

        string trimmed = text.Trim();
        if (trimmed.Length > MaximumLabel)
        {
            throw ApiErrors.InvalidArgument($"{path}.label is at most {MaximumLabel} characters.");
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    // A palette name, or [r, g, b] (0 to 1); none: the palette in turn by the target's index.
    private static (Tint, string) ColorOf(JToken? token, string path, int index)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return (Palette[index % Palette.Length].Tint, Palette[index % Palette.Length].Name);
        }

        if (token.Type == JTokenType.String)
        {
            string name = token.Value<string>()!.Trim();
            foreach ((string known, Tint tint) in Palette)
            {
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
                {
                    return (tint, known);
                }
            }
        }
        else if (token is JArray rgb && rgb.Count == 3 && Channel(rgb[0], out double r) &&
                 Channel(rgb[1], out double g) && Channel(rgb[2], out double b))
        {
            return (new Tint(r, g, b), "custom");
        }

        throw ApiErrors.InvalidArgument(
            $"{path}.color must be one of {string.Join(", ", ColorNames)}, or [r, g, b] with each 0 to 1.");
    }

    private static bool Channel(JToken token, out double value)
    {
        value = 0;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
        {
            return false;
        }

        value = token.Value<double>();
        return value >= 0 && value <= 1;
    }
}
