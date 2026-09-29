#nullable enable

using System;
using System.IO;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A paste's extra turn about the vertical axis, as the D.B.P.U.'s dropdown offers it: 0, 90, 180 or 270 degrees on
/// top of the angle the blueprint was copied at, so every piece stays on the grid.
/// </summary>
internal sealed class PasteRotation
{
    private PasteRotation(int degrees)
    {
        Degrees = degrees;
    }

    internal static PasteRotation None { get; } = new PasteRotation(0);

    internal int Degrees { get; }

    /// <summary>The turn, or null for anything but 0, 90, 180 or 270.</summary>
    internal static PasteRotation? FromDegrees(int degrees) =>
        degrees == 0 || degrees == 90 || degrees == 180 || degrees == 270 ? new PasteRotation(degrees) : null;
}

/// <summary>Where paste_blueprint looks for a blueprint file, and how long BlueprintMod takes to paste it.</summary>
internal static class BlueprintFiles
{
    internal const string Extension = ".blueprint";

    // BlueprintCommands.TimePerEntry, MinPasteDuration and MaxPasteDuration (BlueprintMod 1.8.0).
    private const double SecondsPerEntry = 0.15;
    private const double MinimumSeconds = 2.0;
    private const double MaximumSeconds = 30.0;
    private const int DurationDecimals = 2;

    /// <summary>
    /// An absolute path as given, with .blueprint added when it names no extension; otherwise a file in the Blueprints
    /// folder, with .blueprint added when the name does not end in it. Null for a relative name when there is no
    /// folder.
    /// </summary>
    internal static string? Resolve(string name, string? directory)
    {
        if (Path.IsPathRooted(name))
        {
            return Path.HasExtension(name) ? name : name + Extension;
        }

        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string file = name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension;
        return Path.Combine(directory!, file);
    }

    /// <summary>BlueprintCommands.ComputePasteDuration: 0.15 s per entry, clamped to 2 to 30 s.</summary>
    internal static double ExpectedDurationSeconds(int entries) =>
        Math.Round(Math.Min(MaximumSeconds, Math.Max(MinimumSeconds, entries * SecondsPerEntry)), DurationDecimals);
}
