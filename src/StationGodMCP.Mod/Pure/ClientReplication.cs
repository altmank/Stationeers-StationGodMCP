#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// The host as the replication gate reads it each frame: the frame, how many state packets the game has written so
/// far (FragmentHandler.WriteStateImmediate), whether any client gets them, and the time in seconds.
/// </summary>
internal readonly struct HostSending
{
    internal HostSending(int frame, long stateWrites, bool clientsWatching, double seconds)
    {
        Frame = frame;
        StateWrites = stateWrites;
        ClientsWatching = clientsWatching;
        Seconds = seconds;
    }

    internal int Frame { get; }

    internal long StateWrites { get; }

    internal bool ClientsWatching { get; }

    internal double Seconds { get; }
}

/// <summary>Where a job's last world change stands on its way to the clients.</summary>
internal abstract class ReplicationWait
{
    private ReplicationWait()
    {
    }

    internal static ReplicationWait Clear { get; } = new Sent();

    /// <summary>Nothing a job changed is still waiting to be written for the clients.</summary>
    internal sealed class Sent : ReplicationWait
    {
    }

    /// <summary>
    /// A job step changed the world in Frame. What it destroyed is destroyed at the end of that frame (Unity's
    /// Object.Destroy), and only then does Pipe.OnDestroy or Chute.OnDestroy queue the network rebuilds for the clients.
    /// </summary>
    internal sealed class Settling : ReplicationWait
    {
        internal Settling(int frame)
        {
            Frame = frame;
        }

        internal int Frame { get; }
    }

    /// <summary>Everything the step queued for the clients is queued; a state packet written after WritesBefore carries it.</summary>
    internal sealed class Sending : ReplicationWait
    {
        internal Sending(long writesBefore, double since)
        {
            WritesBefore = writesBefore;
            Since = since;
        }

        internal long WritesBefore { get; }

        internal double Since { get; }
    }
}

/// <summary>What the gate lets a job do this frame.</summary>
internal enum ReplicationPass
{
    /// <summary>The clients have been sent every change so far (or no client is watching): the job may go on.</summary>
    Open,

    /// <summary>A change is not yet written for the clients: the job waits this frame.</summary>
    Waiting,

    /// <summary>No state packet came within the time limit: the job goes on, and the clients may miss the order.</summary>
    TimedOut,
}

/// <summary>
/// Keeps each frame of a job's world changes in a state packet of its own. A client applies a state packet in a fixed
/// order (FragmentHandler.ReadStateImmediate: new networks, new things, destroyed things, then the network rebuilds),
/// not in the order the host made the changes. A job that destroys pieces in one frame (their rebuilds are queued at
/// the end of that frame) and builds over the split in the next would, in one packet, have the client place the new
/// pieces into the network from before the split and then replay each rebuild over pieces the host's rebuild never
/// saw: a later rebuild takes every member from an earlier one, and the client deregisters a network the host keeps.
/// Every delta the host sends for it then throws on the client (StructureNetwork.DeserializeDeltaState). So after a job
/// step changes anything, the next step waits for a state packet written once that step's frame has ended.
/// </summary>
internal sealed class ClientReplication
{
    internal const double TimeoutSeconds = 10.0;

    private ReplicationWait _wait = ReplicationWait.Clear;

    /// <summary>A job step ran in this frame; anything it changed has to reach the clients before the next one.</summary>
    internal void Changed(int frame) => _wait = new ReplicationWait.Settling(frame);

    internal ReplicationPass Pass(HostSending host)
    {
        if (!host.ClientsWatching)
        {
            _wait = ReplicationWait.Clear;
            return ReplicationPass.Open;
        }

        switch (_wait)
        {
            case ReplicationWait.Sent:
                return ReplicationPass.Open;
            case ReplicationWait.Settling settling when host.Frame > settling.Frame:
                _wait = new ReplicationWait.Sending(host.StateWrites, host.Seconds);
                return ReplicationPass.Waiting;
            case ReplicationWait.Settling:
                return ReplicationPass.Waiting;
            case ReplicationWait.Sending sending when host.StateWrites > sending.WritesBefore:
                _wait = ReplicationWait.Clear;
                return ReplicationPass.Open;
            case ReplicationWait.Sending sending when host.Seconds - sending.Since >= TimeoutSeconds:
                _wait = ReplicationWait.Clear;
                return ReplicationPass.TimedOut;
            case ReplicationWait.Sending:
                return ReplicationPass.Waiting;
            default:
                throw new System.InvalidOperationException($"Unknown replication wait {_wait.GetType().Name}.");
        }
    }
}
