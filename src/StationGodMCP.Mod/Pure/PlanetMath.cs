#nullable enable

namespace StationGodMCP.Pure;

/// <summary>Arithmetic the planet tool does on numbers read from the game.</summary>
internal static class PlanetMath
{
    /// <summary>The gas constant planet has always used for partial pressures, J/(mol K).</summary>
    internal const double GasConstant = 8.314462618;

    /// <summary>Ideal-gas pressure in kPa of moles at a temperature in a gas volume in litres, 0 without one.</summary>
    internal static double PressureKpa(double moles, double kelvin, double gasVolumeL) =>
        gasVolumeL > 0.0 ? moles * GasConstant * kelvin / gasVolumeL : 0.0;
}

/// <summary>
/// The coldest and hottest temperatures seen over a sweep of sun angles, and the angle of each. The first of equal
/// values wins; NaN readings are skipped.
/// </summary>
internal sealed class TemperatureSweep
{
    internal double MinK { get; private set; } = double.PositiveInfinity;

    internal double MinAngle { get; private set; }

    internal double MaxK { get; private set; } = double.NegativeInfinity;

    internal double MaxAngle { get; private set; }

    internal void Add(int angle, double kelvin)
    {
        if (double.IsNaN(kelvin))
        {
            return;
        }

        if (kelvin < MinK)
        {
            MinK = kelvin;
            MinAngle = angle;
        }

        if (kelvin > MaxK)
        {
            MaxK = kelvin;
            MaxAngle = angle;
        }
    }
}
