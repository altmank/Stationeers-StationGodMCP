#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests.Routing;

/// <summary>
/// A run end at a port no built device holds yet ({at, toward}, {ghost, port}): its piece is given the end into the
/// port, so the last piece turns into it (LIVE 2026-10-25: header pipes to ghost pumps came out as straights with an
/// open end). A built device's port joins by itself, so its end adds nothing.
/// </summary>
public sealed class TurnIntoPortTests
{
    private static readonly GridCell Port = new GridCell(-2985, 980, 9225);

    private static RouteTree Tree() =>
        new RouteTree(new List<GridCell> { Port, new GridCell(-2985, 980, 9230), new GridCell(-2985, 980, 9235) }, 2, 3);

    [Fact]
    public void AnEndAtAFreePortGetsItsEndIntoThePort()
    {
        GridStep.TryParse("-y", out GridStep down);
        RouteEndpoint free = new RouteEndpoint(RouteEnd.Open(Port), new List<long>(), down, turnsInto: true);

        ExtraEnd turned = Assert.IsType<ExtraEnd>(free.TurnedEnd(Tree()));

        Assert.Equal(Port, turned.Cell);
        Assert.Equal("-y", turned.Step.Name);
    }

    [Fact]
    public void ABuiltDevicesPortOrACellOffTheRunAddsNothing()
    {
        GridStep.TryParse("-y", out GridStep down);
        RouteEndpoint device = new RouteEndpoint(RouteEnd.Open(Port), new List<long>(), down);
        RouteEndpoint elsewhere = new RouteEndpoint(RouteEnd.Open(new GridCell(0, 0, 0)), new List<long>(), down,
            turnsInto: true);
        RouteEndpoint noWay = new RouteEndpoint(RouteEnd.Open(Port), new List<long>(), null, turnsInto: true);

        Assert.Null(device.TurnedEnd(Tree()));
        Assert.Null(elsewhere.TurnedEnd(Tree()));
        Assert.False(noWay.TurnsInto);
    }
}
