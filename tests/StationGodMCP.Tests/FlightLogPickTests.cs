#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Rockets;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// read flight_log and write flight_log stop or clear find the log before any live rocket (FlightLogPick), so the log
/// of a rocket that crashed (its recorder stopped, its samples kept) is still read, stopped and cleared: by the rocket's
/// id, by its name (any case), or, with id left out, the only log there is. Anything else falls to the live rocket.
/// </summary>
public sealed class FlightLogPickTests
{
    private static readonly List<(long RocketId, string RocketName)> TwoLogs =
        new() { (4100, "Tanker"), (4200, "Miner") };

    [Theory]
    [InlineData("4100", 0)]
    [InlineData("tanker", 0)]
    [InlineData(" Miner ", 1)]
    [InlineData("4300", null)]
    [InlineData(null, null)]
    public void ALogIsFoundByItsRocketsIdOrNameWhetherTheRocketStandsOrNot(string? given, int? expected)
    {
        Assert.Equal(expected, FlightLogPick.Of(TwoLogs, given));
    }

    [Fact]
    public void LeftOutTheOnlyLogIsTheOne()
    {
        Assert.Equal(0, FlightLogPick.Of(new List<(long, string)> { (4100, "Tanker") }, null));
        Assert.Null(FlightLogPick.Of(new List<(long, string)>(), null));
    }

    [Fact]
    public void TwoLogsOfOneNameNameNeither()
    {
        Assert.Null(FlightLogPick.Of(new List<(long, string)> { (1, "Probe"), (2, "Probe") }, "probe"));
    }
}
