#nullable enable

using System.Globalization;

namespace StationGodMCP.Api.Shared;

/// <summary>A world position in metres as given.</summary>
internal readonly struct Metres
{
    internal Metres(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal double X { get; }

    internal double Y { get; }

    internal double Z { get; }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", X, Y, Z);
}
