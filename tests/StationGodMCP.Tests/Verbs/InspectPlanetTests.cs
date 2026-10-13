#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>inspect planet's arithmetic (PlanetMath, TemperatureSweep): the planet's pressure and its temperature sweep.</summary>
public sealed class PlanetMathTests
{
    [Fact]
    public void PressureIsIdealGasAndZeroWithoutVolume()
    {
        Assert.Equal(1.0 * PlanetMath.GasConstant * 300.0 / 10.0, PlanetMath.PressureKpa(1.0, 300.0, 10.0));
        Assert.Equal(0.0, PlanetMath.PressureKpa(1.0, 300.0, 0.0));
    }

    [Fact]
    public void SweepKeepsTheFirstExtremesAndSkipsNaN()
    {
        TemperatureSweep sweep = new TemperatureSweep();
        sweep.Add(0, 250.0);
        sweep.Add(1, double.NaN);
        sweep.Add(2, 200.0);
        sweep.Add(3, 200.0);
        sweep.Add(4, 300.0);
        Assert.Equal(200.0, sweep.MinK);
        Assert.Equal(2.0, sweep.MinAngle);
        Assert.Equal(300.0, sweep.MaxK);
        Assert.Equal(4.0, sweep.MaxAngle);
    }
}
