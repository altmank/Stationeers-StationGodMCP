#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Routing;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>remove_structure gas_to picks the first network that can take a device's gas, and its gas check expects it.</summary>
public sealed class GasHandOverTests
{
    [Fact]
    public void TheFirstNetworkThatTakesItIsChosen()
    {
        List<string> why = new List<string>();
        GasReceiver? chosen = GasHandOver.Choose(new[]
        {
            new GasReceiver(1, 70000.0, 60795.0, false, false),
            new GasReceiver(2, 500.0, 60795.0, false, true),
            new GasReceiver(3, 500.0, 60795.0, false, false)
        }, false, why);

        Assert.Equal(3, chosen!.Network);
        Assert.Equal(2, why.Count);
        Assert.Contains("over its weakest pipe", why[0]);
        Assert.Contains("removed by this job", why[1]);
    }

    [Fact]
    public void LiquidNeedsALiquidNetworkAndNoNetworkIsRefused()
    {
        List<string> why = new List<string>();
        Assert.Null(GasHandOver.Choose(new[] { new GasReceiver(4, 100.0, 6079.0, false, false) }, true, why));
        Assert.Contains("holds liquid", why[0]);
        Assert.NotNull(GasHandOver.Choose(new[] { new GasReceiver(5, 100.0, 6079.0, true, false) }, true, new List<string>()));

        List<string> none = new List<string>();
        Assert.Null(GasHandOver.Choose(new GasReceiver[0], false, none));
        Assert.Equal("it joins no pipe network", Assert.Single(none));
    }

    [Fact]
    public void AHandedOverGainIsExpectedByTheGasCheck()
    {
        GasMix before = new GasMix(new[] { 10.0, 0.0 }, new[] { 1000.0, 0.0 });
        GasMix gain = new GasMix(new[] { 2.0, 1.0 }, new[] { 200.0, 50.0 });
        NetworkGas then = new NetworkGas(7, before, 100.0, new long[] { 70 }, new long[0], new List<GridCell>(), 60795.0);
        NetworkGas now = new NetworkGas(7, before.Plus(gain), 100.0, new long[] { 70 }, new long[0], new List<GridCell>(), 60795.0);

        Assert.False(GasAudit.Of(new[] { then }, new[] { now }, GasTolerance.Default).Ok);
        Assert.True(GasAudit.Of(new[] { then }, new[] { now }, GasTolerance.Default, null,
            new[] { new PlannedGasGain(7, gain) }).Ok);
    }

    [Fact]
    public void CutPortsCollapseToOneLinePerDevice()
    {
        List<KeyValuePair<long, List<string>>> devices = PortSplitSummary.Of(new[]
        {
            new ForecastPort(9, 0, false, 100, null), new ForecastPort(9, 2, false, 101, null),
            new ForecastPort(8, 1, false, null, null)
        });

        Assert.Equal(2, devices.Count);
        Assert.Equal(new[] { "0 (network 100)", "2 (network 101)" }, devices[0].Value);
        Assert.Equal("remove_pipes's check: Radiator would lose its connection on ports 0 (network 100), 2 (network 101); " +
                     "remove_structure removes it anyway (verbose: each port in full).",
            PortSplitSummary.Message("remove_pipes", "Radiator", devices[0].Value));
    }
}
