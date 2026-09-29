#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// final-1 (live test r7): ports count by the game's own power roles. A Data-only port carries no load, and a device
/// side is counted once per network however many of its ports are on it.
/// </summary>
public sealed class PowerRolesTests
{
    private const int Data = 4;
    private const int Power = 2;
    private const int PowerAndData = 6;
    private const double NormalCable = 5000.0;
    private const double StationBattery = 3600000.0;
    private const long Piece = 100;

    // One device port as PortLoads sees it: its NetworkType, which input/output connection it is, and its load.
    private sealed class GamePort
    {
        internal GamePort(ForecastPort port, int type, bool inputOutput, bool output, bool input, double potentialW,
            double requiredW)
        {
            Port = port;
            Type = type;
            InputOutput = inputOutput;
            Output = output;
            Input = input;
            PotentialW = potentialW;
            RequiredW = requiredW;
        }

        internal ForecastPort Port { get; }

        internal int Type { get; }

        internal bool InputOutput { get; }

        internal bool Output { get; }

        internal bool Input { get; }

        internal double PotentialW { get; }

        internal double RequiredW { get; }

        internal PortPower? Load()
        {
            PowerSide? side = PortSides.PowerOf(Type, InputOutput, Output, Input);
            return side == null ? null : new PortPower(PotentialW, RequiredW, side.Value);
        }
    }

    private static System.Func<ForecastPort, PortPower?> Loads(params GamePort[] ports)
    {
        Dictionary<ForecastPort, GamePort> byPort = new Dictionary<ForecastPort, GamePort>();
        foreach (GamePort port in ports)
        {
            byPort[port.Port] = port;
        }

        return port => byPort.TryGetValue(port, out GamePort found) ? found.Load() : null;
    }

    private static ForecastNetwork Network(IEnumerable<long> before, params GamePort[] ports)
    {
        ForecastNetwork network = new ForecastNetwork(0);
        network.NetworksBefore.AddRange(before);
        network.NewPieces.Add(Piece);
        foreach (GamePort port in ports)
        {
            network.Ports.Add(port.Port);
        }

        return network;
    }

    private static readonly Dictionary<long, double> Normal = new Dictionary<long, double> { [Piece] = NormalCable };

    [Theory]
    [InlineData(Data, false, false, false, null)]
    [InlineData(Data, true, false, false, null)]
    [InlineData(Power, false, false, false, "Device")]
    [InlineData(PowerAndData, false, false, false, "Device")]
    [InlineData(Power, true, true, false, "Output")]
    [InlineData(Power, true, false, true, "Input")]
    [InlineData(PowerAndData, true, false, true, "Input")]
    [InlineData(Power, true, false, false, null)]
    public void APortMovesPowerOnlyInItsDevicesPowerRole(int type, bool inputOutput, bool output, bool input,
        string? side)
    {
        Assert.Equal(side, PortSides.PowerOf(type, inputOutput, output, input)?.ToString());
    }

    [Theory]
    [InlineData(true, null, ChuteRoles.None, true)]
    [InlineData(false, null, ChuteRoles.Input, false)]
    [InlineData(null, null, ChuteRoles.Input2, true)]
    [InlineData(null, null, ChuteRoles.None, false)]
    public void APortIsTheInputByItsSharedComponentsElseItsRole(bool? sameTransform, bool? sameCollider, int role,
        bool input)
    {
        Assert.Equal(input, PortSides.IsInput(sameTransform, sameCollider, role));
    }

    // Repro A: battery 399's data port joined to net 423, which its own output is on. Was would_overload 41900 W.
    [Fact]
    public void ABatteryDataPortOnItsOwnOutputNetworkBringsNoLoad()
    {
        GamePort dataPort = new GamePort(new ForecastPort(399, 0, false, null, Piece), Data, true, false, false,
            0.0, StationBattery - 3558150.0);
        GamePort outputPort = new GamePort(new ForecastPort(399, 2, true, 423, 423), Power, true, true, false,
            3558150.0, 0.0);
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [423] = new NetworkPower(3558100.0, 15.0, NormalCable, null)
        };
        ForecastNetwork after = Network(new long[] { 423 }, dataPort, outputPort);

        PowerAfter power = PowerAfter.Of(after, before, Normal, new HashSet<long>(), Loads(dataPort, outputPort));

        Assert.False(power.Overloads);
        Assert.Equal(15.0, power.FlowW);
    }

    // Repro B: the landing pad's Data And Power piece (off) and battery 326's data port on one new network. The
    // battery's input is on no network; switched on it must not be priced at PowerMaximum.
    [Fact]
    public void PadWiringWithABatteryDataPortWarnsNothingWhenOn()
    {
        GamePort dataPort = new GamePort(new ForecastPort(326, 0, false, null, Piece), Data, true, false, false,
            0.0, StationBattery);
        GamePort padPower = new GamePort(new ForecastPort(325, 1, true, null, Piece), Power, false, false, false,
            0.0, 30.0);
        ForecastNetwork after = Network(new long[0], dataPort, padPower);
        System.Func<ForecastPort, PortPower?> loads = Loads(dataPort, padPower);

        PowerAfter whenOn = PowerAfter.Of(after, new Dictionary<long, NetworkPower>(), Normal, new HashSet<long>(),
            static _ => null, loads);

        Assert.Equal(30.0, whenOn.RequiredW);
        Assert.False(whenOn.Overloads);
        Assert.Null(PowerAfter.WhenOnWarning(whenOn, new List<long> { 325 }));
    }

    // An APC's data port and its input on one network: the input's demand counts once, the data port adds nothing.
    [Fact]
    public void AnApcDataPortBesideItsInputAddsNothing()
    {
        GamePort dataPort = new GamePort(new ForecastPort(50, 0, false, null, Piece), Data, true, false, false,
            0.0, 900.0);
        GamePort input = new GamePort(new ForecastPort(50, 1, true, null, Piece), Power, true, false, true,
            0.0, 900.0);
        GamePort supply = new GamePort(new ForecastPort(51, 0, true, null, Piece), Power, false, false, false,
            20000.0, 0.0);
        ForecastNetwork after = Network(new long[0], dataPort, input, supply);

        PowerAfter power = PowerAfter.Of(after, new Dictionary<long, NetworkPower>(), Normal, new HashSet<long>(),
            Loads(dataPort, input, supply));

        Assert.Equal(900.0, power.RequiredW);
        Assert.False(power.Overloads);
    }

    // A device with two power-and-data ports on one network is one load (PowerTick asks it once per network).
    [Fact]
    public void ADeviceWithTwoPortsOnOneNetworkCountsOnce()
    {
        GamePort first = new GamePort(new ForecastPort(60, 0, true, null, Piece), PowerAndData, false, false, false,
            0.0, 3000.0);
        GamePort second = new GamePort(new ForecastPort(60, 1, true, null, Piece), PowerAndData, false, false, false,
            0.0, 3000.0);
        GamePort supply = new GamePort(new ForecastPort(61, 0, true, null, Piece), Power, false, false, false,
            20000.0, 0.0);
        ForecastNetwork after = Network(new long[0], first, second, supply);
        System.Func<ForecastPort, PortPower?> loads = Loads(first, second, supply);

        PowerAfter power = PowerAfter.Of(after, new Dictionary<long, NetworkPower>(), Normal, new HashSet<long>(),
            loads);
        PowerAfter whenOn = PowerAfter.Of(after, new Dictionary<long, NetworkPower>(), Normal, new HashSet<long>(),
            static _ => null, loads);

        Assert.Equal(3000.0, power.RequiredW);
        Assert.False(power.Overloads);
        Assert.Equal(3000.0, whenOn.RequiredW);
    }

    // A second port of a device already on a pooled network brings nothing: the network's numbers hold the device.
    [Fact]
    public void ASecondPortOfADeviceThePooledNetworkHoldsBringsNothing()
    {
        GamePort held = new GamePort(new ForecastPort(70, 0, true, 7, 7), PowerAndData, false, false, false,
            0.0, 4500.0);
        GamePort joining = new GamePort(new ForecastPort(70, 1, true, null, Piece), PowerAndData, false, false,
            false, 0.0, 4500.0);
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [7] = new NetworkPower(20000.0, 4500.0, NormalCable, null)
        };
        ForecastNetwork after = Network(new long[] { 7 }, held, joining);

        PowerAfter power = PowerAfter.Of(after, before, Normal, new HashSet<long>(), Loads(held, joining));

        Assert.Equal(4500.0, power.RequiredW);
        Assert.False(power.Overloads);
    }

    // A battery's input and output on one network are two sides: the game asks both, so both count.
    [Fact]
    public void ABatterysInputAndOutputOnOneNetworkBothCount()
    {
        GamePort output = new GamePort(new ForecastPort(80, 2, true, null, Piece), Power, true, true, false,
            1000.0, 0.0);
        GamePort input = new GamePort(new ForecastPort(80, 1, true, null, Piece), Power, true, false, true,
            0.0, 2000.0);
        ForecastNetwork after = Network(new long[0], output, input);

        PowerAfter power = PowerAfter.Of(after, new Dictionary<long, NetworkPower>(), Normal, new HashSet<long>(),
            Loads(output, input));

        Assert.Equal(1000.0, power.PotentialW);
        Assert.Equal(2000.0, power.RequiredW);
    }

    // Repro C: a when_on overload with no device off names nobody, so it is not emitted at all.
    [Fact]
    public void NoWhenOnWarningWithoutAnOffDevice()
    {
        PowerAfter overloads = new PowerAfter(3589195.0, 10830.0, NormalCable, null);

        Assert.Null(PowerAfter.WhenOnWarning(overloads, new List<long>()));
        LayoutIssue? named = PowerAfter.WhenOnWarning(overloads, new List<long> { 12, 5 });
        Assert.NotNull(named);
        Assert.Equal(PowerAfter.WouldOverloadWhenOn, named!.Code);
        Assert.Contains("devices now off (5, 12)", named.Message);
        Assert.Equal(5L, named.Id);
    }
}
