#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>r8-1: would_overload_when_on sees the devices off behind an APC, transformer or transmitter.</summary>
public sealed class PassThroughLoadsTests
{
    private const double StationBattery = 3600000.0;
    private const double NormalCable = 5000.0;
    private const long Piece = 100;
    private const long Apc = 469;
    private const long Battery = 471;
    private const long ApcOutput = 475;

    private static NetworkBehind? From(Dictionary<long, NetworkBehind> networks, long id) =>
        networks.TryGetValue(id, out NetworkBehind found) ? found : null;

    // Live r8: APC 469 on, its output network 475 holds only empty battery 471, off.
    private static Dictionary<long, NetworkBehind> ApcWithOffBattery() => new Dictionary<long, NetworkBehind>
    {
        [ApcOutput] = new NetworkBehind(new[] { new SwitchedOnDemand(Battery, StationBattery) },
            new PassThrough[0])
    };

    [Fact]
    public void AnApcPassesOnTheOffBatteryBehindIt()
    {
        Dictionary<long, NetworkBehind> networks = ApcWithOffBattery();

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(Apc, ApcOutput, 10.0, null),
            id => From(networks, id), new HashSet<long>());

        Assert.Equal(StationBattery, behind.RequiredW);
        Assert.Equal(new[] { Battery }, behind.OffDevices);
    }

    // The APC's input joined to a normal cable with a generator: the when-on forecast now burns it and names 471.
    [Fact]
    public void JoiningAnApcInputWarnsWhenOnForTheBatteryBehindIt()
    {
        Dictionary<long, NetworkBehind> networks = ApcWithOffBattery();
        LoadBehind behind = PassThroughLoads.Of(new PassThrough(Apc, ApcOutput, 10.0, null),
            id => From(networks, id), new HashSet<long>());
        ForecastPort input = new ForecastPort(Apc, 0, true, null, null);
        ForecastNetwork network = new ForecastNetwork(0);
        network.Ports.Add(input);
        network.NewPieces.Add(Piece);

        PowerAfter whenOn = PowerAfter.Of(network, new Dictionary<long, NetworkPower>(),
            new Dictionary<long, double> { [Piece] = NormalCable }, new HashSet<long>(),
            static _ => new PortPower(20000.0, 10.0, PowerSide.Input),
            _ => new PortPower(0.0, behind.RequiredW, PowerSide.Input));
        LayoutIssue? warning = PowerAfter.WhenOnWarning(whenOn, behind.OffDevices);

        Assert.True(whenOn.Overloads);
        Assert.NotNull(warning);
        Assert.Contains("471", warning!.Message);
    }

    // A transformer at Setting 300 already passing 100 W takes at most 200 W more, however much is off behind it.
    [Fact]
    public void ATransformerPassesOnAtMostItsSetting()
    {
        Dictionary<long, NetworkBehind> networks = new Dictionary<long, NetworkBehind>
        {
            [10] = new NetworkBehind(new[] { new SwitchedOnDemand(Battery, StationBattery) }, new PassThrough[0])
        };

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(278, 10, 100.0, 300.0), id => From(networks, id),
            new HashSet<long>());

        Assert.Equal(200.0, behind.RequiredW);
        Assert.Equal(new[] { Battery }, behind.OffDevices);
    }

    // APC -> network 10 (off light 50 W, transformer Setting 1000) -> network 11 (off battery): 50 + 1000.
    [Fact]
    public void AChainAddsEachHopBoundedByItsOwnCap()
    {
        Dictionary<long, NetworkBehind> networks = new Dictionary<long, NetworkBehind>
        {
            [10] = new NetworkBehind(new[] { new SwitchedOnDemand(5, 50.0) },
                new[] { new PassThrough(278, 11, 0.0, 1000.0) }),
            [11] = new NetworkBehind(new[] { new SwitchedOnDemand(Battery, StationBattery) }, new PassThrough[0])
        };

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(Apc, 10, 0.0, null), id => From(networks, id),
            new HashSet<long>());

        Assert.Equal(1050.0, behind.RequiredW);
        Assert.Equal(new[] { 5L, Battery }, behind.OffDevices);
    }

    // A transformer already at its Setting passes nothing more: the devices behind it are not named.
    [Fact]
    public void ACappedHopNamesNothing()
    {
        Dictionary<long, NetworkBehind> networks = new Dictionary<long, NetworkBehind>
        {
            [10] = new NetworkBehind(new[] { new SwitchedOnDemand(Battery, StationBattery) }, new PassThrough[0])
        };

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(278, 10, 300.0, 300.0), id => From(networks, id),
            new HashSet<long>());

        Assert.Equal(0.0, behind.RequiredW);
        Assert.Empty(behind.OffDevices);
    }

    // Two APCs feeding each other's input networks: the walk stops at a network already seen and counts each once.
    [Fact]
    public void ALoopIsWalkedOnce()
    {
        Dictionary<long, NetworkBehind> networks = new Dictionary<long, NetworkBehind>
        {
            [10] = new NetworkBehind(new[] { new SwitchedOnDemand(5, 50.0) },
                new[] { new PassThrough(2, 11, 0.0, null) }),
            [11] = new NetworkBehind(new[] { new SwitchedOnDemand(6, 70.0) },
                new[] { new PassThrough(1, 10, 0.0, null) })
        };

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(1, 10, 0.0, null), id => From(networks, id),
            new HashSet<long>());

        Assert.Equal(120.0, behind.RequiredW);
        Assert.Equal(new[] { 5L, 6L }, behind.OffDevices);
    }

    // The networks of the edit are counted directly, never walked into.
    [Fact]
    public void TheEditsNetworksAreNotWalked()
    {
        Dictionary<long, NetworkBehind> networks = ApcWithOffBattery();

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(Apc, ApcOutput, 10.0, null),
            id => From(networks, id), new HashSet<long> { ApcOutput });

        Assert.Equal(0.0, behind.RequiredW);
    }

    // A chain longer than MaximumDepth stops there.
    [Fact]
    public void ALongChainStopsAtTheDepthBound()
    {
        Dictionary<long, NetworkBehind> networks = new Dictionary<long, NetworkBehind>();
        for (long id = 0; id < 20; id++)
        {
            networks[id] = new NetworkBehind(new[] { new SwitchedOnDemand(1000 + id, 1.0) },
                new[] { new PassThrough(2000 + id, id + 1, 0.0, null) });
        }

        LoadBehind behind = PassThroughLoads.Of(new PassThrough(1999, 0, 0.0, null), id => From(networks, id),
            new HashSet<long>());

        Assert.Equal(PassThroughLoads.MaximumDepth, behind.RequiredW);
    }
}
