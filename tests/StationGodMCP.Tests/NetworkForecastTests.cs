#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The networks an edit leaves: merges, splits, device bridges and cut ports (NetworkForecaster).</summary>
public sealed class NetworkForecastTests
{
    private const long NetworkA = 1000;
    private const long NetworkB = 2000;
    private const long Apc = 500;

    [Fact]
    public void ARunJoiningOneNetworkMergesNothing()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(NetworkA);
        edit.AddPiece(-1, null);
        edit.AddPiece(-2, null);
        edit.Link(-1, NetworkA);
        edit.Link(-1, -2);
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.Empty(forecast.Merges);
        Assert.Empty(forecast.Splits);
        Assert.Single(forecast.Networks);
        Assert.Equal(new[] { NetworkA }, forecast.Networks[0].NetworksBefore);
        Assert.Equal(2, forecast.Networks[0].NewPieces.Count);
        Assert.Empty(EditGuards.Check(forecast, EditAllowance.Nothing));
    }

    [Fact]
    public void ARunBetweenTwoNetworksIsABridgeUnlessBothAreNamed()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(NetworkA);
        edit.AddNetwork(NetworkB);
        edit.AddPiece(-1, null);
        edit.Link(NetworkA, -1);
        edit.Link(-1, NetworkB);
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        ForecastNetwork merge = Assert.Single(forecast.Merges);
        Assert.Equal(new[] { NetworkA, NetworkB }, merge.NetworksBefore);
        Assert.Contains(EditGuards.Check(forecast, EditAllowance.Nothing),
            problem => problem.Code == EditGuards.WouldBridge);
        Assert.Contains(EditGuards.Check(forecast, new EditAllowance(new HashSet<long> { NetworkA }, false)),
            problem => problem.Code == EditGuards.WouldBridge);
        Assert.Empty(EditGuards.Check(forecast,
            new EditAllowance(new HashSet<long> { NetworkA, NetworkB }, false)));
    }

    [Fact]
    public void AnApcsInputAndOutputOnOneNetworkIsADeviceBridge()
    {
        // The APC's input is on network A; the run joins A to its unconnected output.
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(NetworkA);
        edit.AddPiece(-1, null);
        edit.Link(NetworkA, -1);
        edit.Ports.Add(new ForecastPort(Apc, 0, true, NetworkA, NetworkA));
        edit.Ports.Add(new ForecastPort(Apc, 1, true, null, -1));
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.Empty(forecast.Merges);
        ForecastBridge bridge = Assert.Single(forecast.Bridges);
        Assert.Equal(Apc, bridge.Device);
        Assert.Contains(EditGuards.Check(forecast, EditAllowance.Nothing),
            problem => problem.Code == EditGuards.WouldBridge && problem.Id == Apc);
        Assert.Empty(EditGuards.Check(forecast, new EditAllowance(new HashSet<long> { Apc }, false)));
    }

    [Fact]
    public void ADataPortJoiningThePowerNetworkIsNoBridge()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(NetworkA);
        edit.AddPiece(-1, null);
        edit.Link(NetworkA, -1);
        edit.Ports.Add(new ForecastPort(Apc, 0, true, NetworkA, NetworkA));
        edit.Ports.Add(new ForecastPort(Apc, 2, false, null, -1));
        Assert.Empty(NetworkForecaster.Of(edit, out _).Bridges);
    }

    [Fact]
    public void PortsAlreadyOnOneNetworkStayUnflagged()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(NetworkA);
        edit.AddPiece(-1, null);
        edit.Link(NetworkA, -1);
        edit.Ports.Add(new ForecastPort(Apc, 0, true, NetworkA, NetworkA));
        edit.Ports.Add(new ForecastPort(Apc, 1, true, NetworkA, NetworkA));
        Assert.Empty(NetworkForecaster.Of(edit, out _).Bridges);
    }

    // A line of pieces 1-2-3-4-5 on network A with a device on each end; removing 3 splits it.
    private static NetworkEdit Line(bool removeMiddle)
    {
        NetworkEdit edit = new NetworkEdit();
        for (long piece = 1; piece <= 5; piece++)
        {
            if (removeMiddle && piece == 3)
            {
                edit.Remove(3, NetworkA);
                continue;
            }

            edit.AddPiece(piece, NetworkA);
        }

        for (long piece = 1; piece < 5; piece++)
        {
            edit.Link(piece, piece + 1);
        }

        edit.Ports.Add(new ForecastPort(700, 0, true, NetworkA, 1));
        edit.Ports.Add(new ForecastPort(701, 0, true, NetworkA, 5));
        return edit;
    }

    [Fact]
    public void RemovingTheMiddleOfALineSplitsItUnlessAllowed()
    {
        Forecast forecast = NetworkForecaster.Of(Line(true), out Dictionary<long, int> componentOf);
        ForecastSplit split = Assert.Single(forecast.Splits);
        Assert.Equal(NetworkA, split.Network);
        Assert.Equal(2, split.Parts.Count);
        Assert.NotEqual(componentOf[1], componentOf[5]);
        Assert.Contains(EditGuards.Check(forecast, EditAllowance.Nothing),
            problem => problem.Code == EditGuards.WouldSplit);
        Assert.Empty(EditGuards.Check(forecast, new EditAllowance(new HashSet<long>(), true)));
    }

    [Fact]
    public void ARerouteThatReconnectsBothSidesIsNoSplit()
    {
        NetworkEdit edit = Line(true);
        edit.AddPiece(-1, null);
        edit.AddPiece(-2, null);
        edit.Link(2, -1);
        edit.Link(-1, -2);
        edit.Link(-2, 4);
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.Empty(forecast.Splits);
        Assert.Empty(forecast.Cut);
        Assert.Empty(EditGuards.Check(forecast, EditAllowance.Nothing));
    }

    [Fact]
    public void RemovingTheLastPieceAtAPortCutsIt()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1, NetworkA);
        edit.AddPiece(2, NetworkA);
        edit.Ports.Add(new ForecastPort(700, 0, true, NetworkA, null));
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.Single(forecast.Cut);
        Assert.Contains(EditGuards.Check(forecast, EditAllowance.Nothing),
            problem => problem.Code == EditGuards.WouldSplit);
    }

    [Fact]
    public void RemovingEveryPieceLeavesTheNetworkGone()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1, NetworkA);
        edit.Remove(2, NetworkA);
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.Equal(new[] { NetworkA }, forecast.Gone);
        Assert.Empty(forecast.Splits);
    }
}

/// <summary>The overload guard's numbers (PowerAfter): the flow is min(potential, required) of the pooled networks.</summary>
public sealed class PowerAfterTests
{
    private static ForecastNetwork Merged(params long[] networks)
    {
        ForecastNetwork after = new ForecastNetwork(0);
        after.NetworksBefore.AddRange(networks);
        after.NewPieces.Add(-1);
        return after;
    }

    [Fact]
    public void JoiningAGeneratorToAHungryNetworkOverNormalCableOverloads()
    {
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [1] = new NetworkPower(20000.0, 0.0, 100000.0, null),
            [2] = new NetworkPower(0.0, 8000.0, 100000.0, null)
        };
        PowerAfter heavy = PowerAfter.Of(Merged(1, 2), before, new Dictionary<long, double> { [-1] = 100000.0 });
        Assert.Equal(8000.0, heavy.FlowW);
        Assert.False(heavy.Overloads);
        PowerAfter normal = PowerAfter.Of(Merged(1, 2), before, new Dictionary<long, double> { [-1] = 5000.0 });
        Assert.Equal(5000.0, normal.LowestCableW);
        Assert.True(normal.Overloads);
    }

    [Fact]
    public void ANetworkWithoutSupplyNeverOverloads()
    {
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [1] = new NetworkPower(0.0, 50000.0, 5000.0, null)
        };
        Assert.False(PowerAfter.Of(Merged(1), before, new Dictionary<long, double>()).Overloads);
    }

    private static ForecastNetwork NewOnly(params ForecastPort[] ports)
    {
        ForecastNetwork after = new ForecastNetwork(0);
        after.NewPieces.Add(-1);
        after.Ports.AddRange(ports);
        return after;
    }

    [Fact]
    public void ARunOfNewPiecesBetweenAFullAndAnEmptyBatteryOverloadsFromTheirOwnState()
    {
        ForecastPort output = new ForecastPort(10, 1, true, null, -1);
        ForecastPort input = new ForecastPort(11, 0, true, null, -1);
        Dictionary<ForecastPort, PortPower> own = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(3600000.0, 0.0, PowerSide.Output),
            [input] = new PortPower(0.0, 3600000.0, PowerSide.Input)
        };
        PowerAfter power = PowerAfter.Of(NewOnly(output, input), new Dictionary<long, NetworkPower>(),
            new Dictionary<long, double> { [-1] = 5000.0 }, new HashSet<long>(),
            port => own.TryGetValue(port, out PortPower found) ? found : null);
        Assert.Equal(3600000.0, power.FlowW);
        Assert.True(power.Overloads);
    }

    [Fact]
    public void ARerouteReplacingEveryPieceKeepsTheOldNetworksLoadOnce()
    {
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [7] = new NetworkPower(6000.0, 2830000.0, null, null)
        };
        ForecastPort source = new ForecastPort(20, 2, true, 7, -1);
        ForecastPort sink = new ForecastPort(21, 0, true, 7, -1);
        PowerAfter power = PowerAfter.Of(NewOnly(source, sink), before,
            new Dictionary<long, double> { [-1] = 5000.0 }, new HashSet<long> { 7 },
            _ => new PortPower(1e9, 1e9, PowerSide.Device));
        Assert.Equal(6000.0, power.PotentialW);
        Assert.Equal(2830000.0, power.RequiredW);
        Assert.True(power.Overloads);
    }

    [Fact]
    public void APortOnANetworkTheResultHoldsIsNotCountedTwice()
    {
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [1] = new NetworkPower(4000.0, 4000.0, 5000.0, null)
        };
        ForecastNetwork after = Merged(1);
        after.Ports.Add(new ForecastPort(30, 0, true, 1, 1));
        PowerAfter power = PowerAfter.Of(after, before, new Dictionary<long, double> { [-1] = 5000.0 },
            new HashSet<long>(), _ => new PortPower(1e9, 1e9, PowerSide.Device));
        Assert.Equal(4000.0, power.FlowW);
        Assert.False(power.Overloads);
    }
}
