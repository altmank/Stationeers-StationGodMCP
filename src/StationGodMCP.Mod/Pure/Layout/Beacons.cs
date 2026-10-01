#nullable enable

using System;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// Where a point lies from someone standing at another: the straight distance, the distance over the ground, the
/// height difference, and the compass bearing (degrees clockwise from north, +z; east is +x).
/// </summary>
internal readonly struct Heading
{
    private static readonly string[] Compass = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private Heading(double distance, double ground, double rise, double bearing)
    {
        Distance = distance;
        Ground = ground;
        Rise = rise;
        Bearing = bearing;
    }

    internal double Distance { get; }

    /// <summary>The distance over the ground (x and z only).</summary>
    internal double Ground { get; }

    /// <summary>How far the point lies above the one standing (negative below).</summary>
    internal double Rise { get; }

    /// <summary>0 to 360, 0 north (+z), 90 east (+x); 0 for a point straight above or below.</summary>
    internal double Bearing { get; }

    /// <summary>N, NE, E, SE, S, SW, W or NW: the eighth of the compass the bearing falls in.</summary>
    internal string CompassPoint => Compass[(int)Math.Floor((Bearing + 22.5) / 45.0) % 8];

    internal static Heading From(Vec3 from, Vec3 to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dz = to.Z - from.Z;
        double ground = Math.Sqrt(dx * dx + dz * dz);
        double bearing = ground < 1e-9 ? 0.0 : Math.Atan2(dx, dz) * 180.0 / Math.PI;
        if (bearing < 0)
        {
            bearing += 360.0;
        }

        return new Heading(Math.Sqrt(ground * ground + dy * dy), ground, dy, bearing);
    }

    /// <summary>The text a label shows, e.g. "412 m, bearing 73 deg E, 12 m up".</summary>
    internal string Text()
    {
        string rise = Math.Abs(Rise) < 0.5
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, ", {0:0} m {1}", Math.Abs(Rise), Rise > 0 ? "up" : "down");
        return string.Format(CultureInfo.InvariantCulture, "{0:0} m, bearing {1:0} deg {2}{3}", Distance, Bearing,
            CompassPoint, rise);
    }
}

/// <summary>
/// Where a beacon's marker goes on the screen: at the point when it is in view, else pinned to the screen's edge in
/// the direction of the point (a point behind the camera points the other way, as a turn toward it would).
/// Coordinates are the viewport's: 0 to 1 from the bottom left.
/// </summary>
internal readonly struct ScreenMarker
{
    private ScreenMarker(double x, double y, bool onScreen, double angle)
    {
        X = x;
        Y = y;
        OnScreen = onScreen;
        Angle = angle;
    }

    internal double X { get; }

    internal double Y { get; }

    internal bool OnScreen { get; }

    /// <summary>The way to turn, degrees counter-clockwise from screen right; meaningful when off screen.</summary>
    internal double Angle { get; }

    /// <param name="x">The viewport x of the point.</param>
    /// <param name="y">The viewport y of the point.</param>
    /// <param name="depth">Its distance in front of the camera; negative behind.</param>
    /// <param name="margin">How far in from the edges a pinned marker stays (viewport units).</param>
    internal static ScreenMarker Of(double x, double y, double depth, double margin)
    {
        if (depth > 0 && x >= 0 && x <= 1 && y >= 0 && y <= 1)
        {
            return new ScreenMarker(x, y, true, 0);
        }

        double dx = x - 0.5;
        double dy = y - 0.5;
        if (depth <= 0)
        {
            dx = -dx;
            dy = -dy;
        }

        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            // Straight behind: point down, the way a turn either side starts.
            dy = -1;
        }

        double half = 0.5 - margin;
        double scale = half / Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new ScreenMarker(0.5 + dx * scale, 0.5 + dy * scale, false, Math.Atan2(dy, dx) * 180.0 / Math.PI);
    }
}

/// <summary>A pulsing highlight's strength over time: a smooth swell between a floor and full, once a second.</summary>
internal static class PulseCurve
{
    private const double Floor = 0.25;

    internal static double Strength(double seconds) =>
        Floor + (1 - Floor) * 0.5 * (1 + Math.Cos(seconds * 2 * Math.PI));
}
