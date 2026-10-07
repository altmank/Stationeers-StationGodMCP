#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// What the game had queued for its next tick when a job came to settle a piece: the two atmospheric event queues,
/// the grids awaiting an event, and whether any pipe network's atmosphere awaits one. Unreadable when a game member
/// it is read through is missing or reading it failed.
/// </summary>
internal readonly struct QueuedGas
{
    private QueuedGas(bool readable, int networkEvents, int atmosphereEvents, int awaitingGrids,
        bool atmosphereAwaiting)
    {
        Readable = readable;
        NetworkEvents = networkEvents;
        AtmosphereEvents = atmosphereEvents;
        AwaitingGrids = awaitingGrids;
        AtmosphereAwaiting = atmosphereAwaiting;
    }

    internal bool Readable { get; }

    internal int NetworkEvents { get; }

    internal int AtmosphereEvents { get; }

    internal int AwaitingGrids { get; }

    internal bool AtmosphereAwaiting { get; }

    internal static QueuedGas Of(int networkEvents, int atmosphereEvents, int awaitingGrids, bool atmosphereAwaiting) =>
        new QueuedGas(true, networkEvents, atmosphereEvents, awaitingGrids, atmosphereAwaiting);

    internal static QueuedGas Unreadable { get; } = new QueuedGas(false, 0, 0, 0, false);
}

/// <summary>
/// Whether a job's settle after a piece may skip its hop to a pool thread: only when the game provably has nothing
/// to apply (both event queues and the awaiting grids empty, no pipe network atmosphere awaiting an event), in which
/// case applying the queues would have done nothing. Anything unreadable settles: the gate fails closed. Counts the
/// settles run and skipped, and those run because the queues could not be read. Main thread only.
/// </summary>
internal static class SettleGate
{
    internal static long Run { get; private set; }

    internal static long Skipped { get; private set; }

    /// <summary>Settles run because the game's queues could not be read.</summary>
    internal static long Unchecked { get; private set; }

    internal static bool MaySkip(QueuedGas queued) =>
        queued.Readable && queued.NetworkEvents == 0 && queued.AtmosphereEvents == 0 && queued.AwaitingGrids == 0 &&
        !queued.AtmosphereAwaiting;

    /// <summary>The gate's decision, counted: true when the settle is skipped.</summary>
    internal static bool Skip(QueuedGas queued)
    {
        if (MaySkip(queued))
        {
            Skipped++;
            return true;
        }

        Run++;
        if (!queued.Readable)
        {
            Unchecked++;
        }

        return false;
    }
}
