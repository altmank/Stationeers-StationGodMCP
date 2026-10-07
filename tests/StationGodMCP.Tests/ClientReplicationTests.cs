#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// LIVE 2026-10-07: a client joined to the dedicated server threw at StructureNetwork.DeserializeDeltaState every
/// packet after StationGod jobs removed and rebuilt pipe networks. A job's next step now waits until a state packet
/// written after the end of its last step's frame has gone out (ClientReplication), and a job's own merge keeps the
/// network the clients keep (NetworkUnionRule).
/// </summary>
public sealed class ClientReplicationTests
{
    private static HostSending Host(int frame, long writes, double seconds = 0.0, bool watching = true) =>
        new HostSending(frame, writes, watching, seconds);

    [Fact]
    public void NothingChangedLetsTheJobGoOn()
    {
        ClientReplication replication = new ClientReplication();

        Assert.Equal(ReplicationPass.Open, replication.Pass(Host(10, 5)));
    }

    [Fact]
    public void AStepWaitsOutTheEndOfItsOwnFrame()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);

        Assert.Equal(ReplicationPass.Waiting, replication.Pass(Host(10, 6)));
    }

    // A packet written in the step's own frame can go out before Unity destroys the removed pieces, so before their
    // rebuilds are queued: it must not count.
    [Fact]
    public void APacketWrittenBeforeTheFrameEndedDoesNotCount()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(10, 5));

        Assert.Equal(ReplicationPass.Waiting, replication.Pass(Host(11, 6)));
    }

    [Fact]
    public void APacketWrittenAfterTheFrameEndedOpensTheGate()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(11, 6));

        Assert.Equal(ReplicationPass.Open, replication.Pass(Host(12, 7)));
    }

    [Fact]
    public void NoPacketYetKeepsTheJobWaiting()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(11, 6));

        Assert.Equal(ReplicationPass.Waiting, replication.Pass(Host(14, 6, 0.2)));
    }

    [Fact]
    public void AnotherStepWaitsForAnotherPacket()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(11, 6));
        replication.Pass(Host(12, 7));
        replication.Changed(12);

        Assert.Equal(ReplicationPass.Waiting, replication.Pass(Host(13, 7)));
    }

    [Fact]
    public void WithoutClientsNothingWaits()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);

        Assert.Equal(ReplicationPass.Open, replication.Pass(Host(10, 5, watching: false)));
    }

    [Fact]
    public void TheLastClientLeavingOpensTheGate()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(11, 6));

        Assert.Equal(ReplicationPass.Open, replication.Pass(Host(12, 6, watching: false)));
    }

    [Fact]
    public void NoPacketWithinTheLimitLetsTheJobGoOnAndSaysSo()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(11, 6, 1.0));

        Assert.Equal(ReplicationPass.TimedOut,
            replication.Pass(Host(900, 6, 1.0 + ClientReplication.TimeoutSeconds)));
    }

    [Fact]
    public void ATimedOutWaitDoesNotHoldTheNextStep()
    {
        ClientReplication replication = new ClientReplication();
        replication.Changed(10);
        replication.Pass(Host(11, 6, 1.0));
        replication.Pass(Host(900, 6, 1.0 + ClientReplication.TimeoutSeconds));

        Assert.Equal(ReplicationPass.Open, replication.Pass(Host(901, 6, 12.0)));
    }

    [Fact]
    public void ANetworkOfOnlyTheNewPiecesGoesIntoTheKeptOne() =>
        Assert.Equal(NetworkUnion.IntoKept, NetworkUnionRule.For(theirsHoldsOnlyNewPieces: true));

    [Fact]
    public void ANetworkWithPiecesThatStoodBeforeTakesTheKeptOneIn() =>
        Assert.Equal(NetworkUnion.IntoTheirs, NetworkUnionRule.For(theirsHoldsOnlyNewPieces: false));
}
