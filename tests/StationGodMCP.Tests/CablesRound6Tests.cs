#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 6 of the live test of 1.4.4.</summary>
public sealed class CablesRound6Tests
{
    private const double StationBattery = 3600000.0;
    private const double NormalCable = 5000.0;
    private const long Piece = 100;

    // Two station batteries, both off, joined by a normal cable: A's output and B's input on one new network.
    private static PowerAfter TwoOffBatteries()
    {
        ForecastPort output = new ForecastPort(1, 1, true, null, null);
        ForecastPort input = new ForecastPort(2, 0, true, null, null);
        ForecastNetwork network = new ForecastNetwork(0);
        network.Ports.Add(output);
        network.Ports.Add(input);
        network.NewPieces.Add(Piece);
        Dictionary<ForecastPort, PortPower> dormant = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(SwitchedOnLoads.BatteryOutput(StationBattery), 0.0),
            [input] = new PortPower(0.0, SwitchedOnLoads.BatteryInput(StationBattery)),
        };
        return PowerAfter.Of(network, new Dictionary<long, NetworkPower>(),
            new Dictionary<long, double> { [Piece] = NormalCable }, new HashSet<long>(),
            static _ => new PortPower(0.0, 0.0), port => dormant.TryGetValue(port, out PortPower found) ? found : null);
    }

    // r6 note: two nearly full batteries forecast 12200 W in one call and 5550 W in the next; switched-on loads are
    // now the game's ceilings, not the drifting charge, so the warning cannot vanish between dry run and real run.
    [Fact]
    public void TwoOffBatteriesOverloadWhenOnWhateverTheirCharge()
    {
        PowerAfter whenOn = TwoOffBatteries();

        Assert.True(whenOn.Overloads);
        Assert.Equal(StationBattery, whenOn.FlowW);
    }

    // The flow is min(potential, required): a battery input's ceiling beside a small supply carries only that supply.
    [Fact]
    public void ABatteryInputCeilingIsBoundedByTheSupply()
    {
        ForecastPort input = new ForecastPort(2, 0, true, null, null);
        ForecastNetwork network = new ForecastNetwork(0);
        network.Ports.Add(input);
        network.NewPieces.Add(Piece);
        PowerAfter whenOn = PowerAfter.Of(network,
            new Dictionary<long, NetworkPower>(), new Dictionary<long, double> { [Piece] = NormalCable },
            new HashSet<long>(), static _ => new PortPower(1200.0, 0.0),
            static _ => new PortPower(0.0, SwitchedOnLoads.BatteryInput(StationBattery)));

        Assert.False(whenOn.Overloads);
        Assert.Equal(1200.0, whenOn.FlowW);
    }

    [Fact]
    public void ApcAndTransmitterCeilings()
    {
        Assert.Equal(2000.0 + 72000.0, SwitchedOnLoads.ApcOutput(2000.0, 72000.0));
        Assert.Equal(2000.0, SwitchedOnLoads.ApcOutput(2000.0, null));
        Assert.Equal(5000.0, SwitchedOnLoads.TransmitterInput(5000.0));
        Assert.Equal(5000.0, SwitchedOnLoads.TransmitterOutput(9000.0, 5000.0));
        Assert.Equal(300.0, SwitchedOnLoads.TransmitterOutput(300.0, 5000.0));
    }
}
