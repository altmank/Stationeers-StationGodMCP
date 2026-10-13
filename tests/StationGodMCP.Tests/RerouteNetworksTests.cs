#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// reroute.between with a device on several networks of the kind: an APC's input (port 0) on the feed network and
/// its output (port 1) on the base network. The 2026-09-27 cable-bus report: between an APC and a cable on the base
/// network was refused not_on_one_network, because the APC's two networks made it "cannot say which".
/// </summary>
public sealed class RerouteNetworksTests
{
    private const long Feed = 1001;
    private const long Base = 2002;
    private const long Other = 3003;

    private static RerouteEndNetworks Apc(params EndNetwork[] ports) =>
        new RerouteEndNetworks("StructureAreaPowerControl 11", ports);

    private static RerouteEndNetworks TwoSidedApc() => Apc(new EndNetwork(0, Feed), new EndNetwork(1, Base));

    private static RerouteEndNetworks Cable(long network) =>
        new RerouteEndNetworks("StructureCableSuperHeavyStraight 22", new[] { new EndNetwork(null, network) });

    private static RerouteNetworkChoice.Refused Refused(RerouteNetworkChoice choice) =>
        Assert.IsType<RerouteNetworkChoice.Refused>(choice);

    [Fact]
    public void AnApcAndACableOnItsOutputShareTheOutputNetwork()
    {
        RerouteNetworkChoice choice = RerouteNetworks.Shared(TwoSidedApc(), Cable(Base), "cable");
        Assert.Equal(Base, Assert.IsType<RerouteNetworkChoice.Chosen>(choice).Network);
        choice = RerouteNetworks.Shared(Cable(Feed), TwoSidedApc(), "cable");
        Assert.Equal(Feed, Assert.IsType<RerouteNetworkChoice.Chosen>(choice).Network);
    }

    [Fact]
    public void TwoPiecesOnOneNetworkShareIt()
    {
        RerouteNetworkChoice choice = RerouteNetworks.Shared(Cable(Base), Cable(Base), "cable");
        Assert.Equal(Base, Assert.IsType<RerouteNetworkChoice.Chosen>(choice).Network);
    }

    [Fact]
    public void NoSharedNetworkListsEveryPort()
    {
        RerouteNetworkChoice.Refused refused = Refused(RerouteNetworks.Shared(
            Apc(new EndNetwork(0, Feed), new EndNetwork(1, Base), new EndNetwork(2, null)), Cable(Other), "cable"));
        Assert.Equal(RerouteNetworks.NotOnOneNetwork, refused.Code);
        Assert.Contains("port 0: network 1001", refused.Message);
        Assert.Contains("port 1: network 2002", refused.Message);
        Assert.Contains("port 2: nothing joined", refused.Message);
        Assert.Contains("network 3003", refused.Message);
    }

    [Fact]
    public void AnEndWithNothingJoinedSharesNothing()
    {
        RerouteNetworkChoice.Refused refused = Refused(RerouteNetworks.Shared(
            Apc(new EndNetwork(0, null)), new RerouteEndNetworks("Battery 33", new[] { new EndNetwork(0, null) }),
            "cable"));
        Assert.Equal(RerouteNetworks.NotOnOneNetwork, refused.Code);
    }

    [Fact]
    public void TwoDevicesOnTheSameTwoNetworksAreAmbiguousAndNameTheCandidatePorts()
    {
        // An APC and a transformer both bridging the feed and the base network: either could be the old run.
        RerouteEndNetworks transformer = new RerouteEndNetworks("StructureTransformer 44",
            new[] { new EndNetwork(0, Base), new EndNetwork(1, Feed) });
        RerouteNetworkChoice.Refused refused = Refused(RerouteNetworks.Shared(TwoSidedApc(), transformer, "cable"));
        Assert.Equal(RerouteNetworks.AmbiguousPort, refused.Code);
        Assert.Contains("share 2 cable networks", refused.Message);
        Assert.Contains("network 1001 (StructureAreaPowerControl 11 port 0, StructureTransformer 44 port 1)",
            refused.Message);
        Assert.Contains("network 2002 (StructureAreaPowerControl 11 port 1, StructureTransformer 44 port 0)",
            refused.Message);
        Assert.Contains("reroute.between: [{id, port}, ...]", refused.Message);
    }

    [Fact]
    public void NamingThePortSettlesTheAmbiguity()
    {
        RerouteEndNetworks transformer = new RerouteEndNetworks("StructureTransformer 44",
            new[] { new EndNetwork(0, Base), new EndNetwork(1, Feed) });
        RerouteEndNetworks outputOnly = new RerouteEndNetworks("StructureAreaPowerControl 11 port 1",
            new[] { new EndNetwork(1, Base) });
        RerouteNetworkChoice choice = RerouteNetworks.Shared(outputOnly, transformer, "cable");
        Assert.Equal(Base, Assert.IsType<RerouteNetworkChoice.Chosen>(choice).Network);
    }

    [Fact]
    public void TwoPortsOfOneEndOnTheSharedNetworkAreStillOneNetwork()
    {
        RerouteNetworkChoice choice = RerouteNetworks.Shared(
            Apc(new EndNetwork(0, Base), new EndNetwork(1, Base)), Cable(Base), "cable");
        Assert.Equal(Base, Assert.IsType<RerouteNetworkChoice.Chosen>(choice).Network);
    }

    [Fact]
    public void BetweenTakesIdsAndDevicePorts()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        List<RerouteEndArg> ends = RerouteArgs.Between(JArray.Parse(
            "[\"123456789012345678\", {\"id\": \"42\", \"port\": 1}]"), problems)!;
        Assert.Equal(123456789012345678L, ends[0].Id.Value);
        Assert.Null(ends[0].Port);
        Assert.Equal(42L, ends[1].Id.Value);
        Assert.Equal(1, ends[1].Port);
        ends = RerouteArgs.Between(JArray.Parse("[{\"id\": \"7\"}, 8]"), problems)!;
        Assert.Null(ends[0].Port);
        Assert.Equal(8L, ends[1].Id.Value);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("[\"1\"]", "reroute.between")]
    [InlineData("[\"1\", \"2\", \"3\"]", "reroute.between")]
    [InlineData("{\"id\": \"1\"}", "reroute.between")]
    [InlineData("[\"1\", true]", "reroute.between[1]")]
    [InlineData("[\"1\", {\"port\": 1}]", "reroute.between[1]")]
    [InlineData("[\"1\", {\"reference_id\": \"2\"}]", "reroute.between[1]")]
    [InlineData("[\"1\", {\"id\": \"2\", \"port\": -1}]", "reroute.between[1].port")]
    [InlineData("[\"1\", {\"id\": \"2\", \"port\": \"one\"}]", "reroute.between[1].port")]
    [InlineData("[\"1\", {\"id\": \"2\", \"port\": 99}]", "reroute.between[1].port")]
    public void BetweenRefusesAnythingElseNamingTheEnd(string json, string path)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(RerouteArgs.Between(JToken.Parse(json), problems));
        Assert.Equal(path, Assert.Single(problems).Path);
    }
}
