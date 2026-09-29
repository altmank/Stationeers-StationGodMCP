#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 4 of the cables live test of 1.4.4, and the pipe-only gas model of remove_structure.</summary>
public sealed class CablesRound4Tests
{
    private static GridStep S(string name) => RunModels.Step(name);

    private static ForecastNetwork NewOnly(params ForecastPort[] ports)
    {
        ForecastNetwork after = new ForecastNetwork(0);
        after.NewPieces.Add(-1);
        after.Ports.AddRange(ports);
        return after;
    }

    // Overload blind spot (items r4 note): two full batteries joined while off forecast 0 W; switched on, the output
    // gives its charge and the input takes its free room, and a normal cable burns at once.
    [Fact]
    public void TwoFullBatteriesJoinedWhileOffOverloadOnlyOnceSwitchedOn()
    {
        ForecastPort output = new ForecastPort(10, 1, true, null, -1);
        ForecastPort input = new ForecastPort(11, 0, true, null, -1);
        Dictionary<ForecastPort, PortPower> switchedOn = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(3593900.0, 0.0),
            [input] = new PortPower(0.0, 6100.0)
        };
        Dictionary<long, double> normal = new Dictionary<long, double> { [-1] = 5000.0 };
        PowerAfter now = PowerAfter.Of(NewOnly(output, input), new Dictionary<long, NetworkPower>(), normal,
            new HashSet<long>(), static _ => new PortPower(0.0, 0.0));
        PowerAfter whenOn = PowerAfter.Of(NewOnly(output, input), new Dictionary<long, NetworkPower>(), normal,
            new HashSet<long>(), static _ => new PortPower(0.0, 0.0),
            port => switchedOn.TryGetValue(port, out PortPower found) ? found : null);
        Assert.False(now.Overloads);
        Assert.Equal(6100.0, whenOn.FlowW);
        Assert.True(whenOn.Overloads);
    }

    // An off device already on a network the edit pools is not in that network's numbers (the game counts it as 0);
    // its switched-on load is added on top.
    [Fact]
    public void AnOffDeviceOnAPooledNetworkIsCountedOnTopOfItsNetwork()
    {
        ForecastPort heater = new ForecastPort(20, 0, true, 7, 7);
        ForecastNetwork after = new ForecastNetwork(0);
        after.NetworksBefore.Add(7);
        after.NewPieces.Add(-1);
        after.Ports.Add(heater);
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [7] = new NetworkPower(20000.0, 4000.0, 5000.0, null)
        };
        PowerAfter whenOn = PowerAfter.Of(after, before, new Dictionary<long, double> { [-1] = 5000.0 }, null,
            null, port => port.DeviceId == 20 ? new PortPower(0.0, 2000.0) : null);
        Assert.Equal(6000.0, whenOn.RequiredW);
        Assert.True(whenOn.Overloads);
    }

    // cables-36: a piece's end pointing into another piece of the job that has no end back stays open and is warned.
    [Fact]
    public void AnEndIntoAPieceWithoutAnEndBackWarnsOpenEnd()
    {
        GridCell a = RunModels.At(0, 0, 0);
        GridCell b = RunModels.At(1, 0, 0);
        RunShape shape = RunShape.Pieces(new List<GridCell> { a, b }, out _)!;
        List<ExtraEnd> extra = new List<ExtraEnd>
        {
            new ExtraEnd(a, S("+x")), new ExtraEnd(a, S("-x")), new ExtraEnd(b, S("+z")), new ExtraEnd(b, S("-z"))
        };
        RunLayout layout = RunLayoutPlanner.Plan(shape, new RunSurroundings(), JoinMode.None, extra, null);
        List<LayoutIssue> open = layout.Warnings.FindAll(static warning => warning.Code == "open_end");
        Assert.Equal(4, open.Count);
        Assert.Contains(open, warning => warning.Cell.HasValue && warning.Cell.Value.Equals(a) &&
                                         warning.Message.Contains("no end back"));
    }

    // The same two pieces with an end back join, and neither end between them is warned.
    [Fact]
    public void AnEndIntoAPieceWithAnEndBackJoins()
    {
        GridCell a = RunModels.At(0, 0, 0);
        GridCell b = RunModels.At(1, 0, 0);
        RunShape shape = RunShape.Pieces(new List<GridCell> { a, b }, out _)!;
        List<ExtraEnd> extra = new List<ExtraEnd>
        {
            new ExtraEnd(a, S("+x")), new ExtraEnd(a, S("-x")), new ExtraEnd(b, S("-x")), new ExtraEnd(b, S("+z"))
        };
        RunLayout layout = RunLayoutPlanner.Plan(shape, new RunSurroundings(), JoinMode.None, extra, null);
        Assert.DoesNotContain(layout.Warnings, static warning => warning.Message.Contains("no end back"));
        Assert.Equal(2, layout.Warnings.FindAll(static warning => warning.Code == "open_end").Count);
    }

    // Gas-model gap: pipe pieces only, P1-P2-P3-P4, removing P3 then P4 splits P4 off with its share by volume, and
    // P4 then goes as the last member of its part, so that share is deleted (planned_loss_mol, not refilled).
    [Fact]
    public void PipePiecesOnlyCanDeleteTheShareOfAPartTheySplitOff()
    {
        List<TakedownMember> pipes = new List<TakedownMember>
        {
            new TakedownMember(1, 10.0, 60795.0),
            new TakedownMember(2, 10.0, 60795.0),
            new TakedownMember(3, 10.0, 60795.0),
            new TakedownMember(4, 10.0, 60795.0)
        };
        List<Link> links = new List<Link>
        {
            new Link(1, 2), new Link(2, 1), new Link(2, 3), new Link(3, 2), new Link(3, 4), new Link(4, 3)
        };
        TakedownOutcome outcome = PipeTakedown.Run(pipes, links, new long[] { 3, 4 }, false, 90.0);
        TakedownPart part = Assert.Single(outcome.Parts);
        Assert.Equal(new List<long> { 1, 2 }, part.Members);
        Assert.Equal(60.0, part.Moles, 6);
        Assert.Equal(30.0, outcome.LostMol, 6);
        PlannedGasLoss loss = PlannedGasLoss.Of(77, outcome.LostMol, outcome.MolesBefore);
        Assert.Equal(1.0 / 3.0, loss.Share, 6);
    }
}
