using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// The hub's one game connection (docs/architecture/hub.md, Streams and C# seams): calls tagged by stream, the world id
/// on every call, resume across a break, admission round robin by stream with stream 0 first, held restores, stream
/// costs and job waits on the jobs topic.
/// </summary>
public interface IGameLink
{
    /// <summary>The world id from the last welcome or world_changed; null while the game is unreachable.</summary>
    string? WorldId { get; }

    /// <summary>Raised with the new world id (null: no world) when the world changes.</summary>
    event Action<string?>? WorldChanged;

    /// <summary>A new stream number: 1, 2, ..., never reused while the hub runs.</summary>
    long NewStream();

    /// <summary>One call on its stream, carrying the current world id. Never throws for game outcomes.</summary>
    Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation);

    /// <summary>stream_end for the stream. The caller reads its stream_costs first.</summary>
    Task EndStreamAsync(long stream);

    /// <summary>session_end: the mod cancels this connection's waiting calls and applies its held restores.</summary>
    Task SessionEndAsync();

    /// <summary>hold_restore on the stream with the change object ({set?, clear?, clear_all?}).</summary>
    Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation);

    /// <summary>stream_costs for the streams asked, in the order asked.</summary>
    Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation);

    /// <summary>The job's job_ended facts from the jobs topic, or null when the timeout passes first.</summary>
    Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation);
}

/// <summary>
/// What a hub session needs of the link beyond the verbs' calls (hub.md, The hub protocol): the mod's last welcome to
/// answer hello with, the events to relay, and the messages a caller's session passes through.
/// </summary>
public interface IHubLink : IGameLink
{
    /// <summary>The mod's last welcome; null before the first connection.</summary>
    JsonElement? Welcome { get; }

    /// <summary>Every event but ping, raised on the link's reader.</summary>
    event Action<JsonElement>? EventReceived;

    /// <summary>Connects now if not connected; false when the game could not be reached.</summary>
    Task<bool> ConnectAsync(CancellationToken cancellation);

    /// <summary>cursor_close for a walk's cursor, when the mod lists cursors. No reply.</summary>
    Task CursorCloseAsync(string cursorId);

    /// <summary>release of the stream's job pins, when the mod lists release. No reply.</summary>
    Task ReleaseAsync(long stream);
}

/// <summary>One call as the hub sends it. The link adds the world id.</summary>
public sealed class GameCall
{
    public GameCall(string method, JsonElement parameters, long stream = 0)
    {
        Method = method;
        Parameters = parameters;
        Stream = stream;
    }

    public string Method { get; }

    public JsonElement Parameters { get; }

    public long Stream { get; }

    public JsonElement? Shape { get; init; }

    /// <summary>The rename envelope ({args, tools}) so the mod prints the caller's names; null for none.</summary>
    public JsonElement? Rename { get; init; }

    public int? DeadlineMs { get; init; }
}

/// <summary>How a game call ended: answered, refused, or unreachable. None is thrown.</summary>
public abstract class GameReply
{
    private GameReply()
    {
    }

    public sealed class Ok : GameReply
    {
        public Ok(JsonElement result, long frame, double elapsedMs, bool budgetHit)
        {
            Result = result;
            Frame = frame;
            ElapsedMs = elapsedMs;
            BudgetHit = budgetHit;
        }

        public JsonElement Result { get; }

        public long Frame { get; }

        public double ElapsedMs { get; }

        public bool BudgetHit { get; }
    }

    public sealed class Refused : GameReply
    {
        public Refused(string code, string message, JsonElement? data)
        {
            Code = code;
            Message = message;
            Data = data;
        }

        public string Code { get; }

        public string Message { get; }

        public JsonElement? Data { get; }
    }

    /// <summary>No answer. MaybeRan is true when the call was written before the break and may have acted.</summary>
    public sealed class Unreachable : GameReply
    {
        public Unreachable(string message, bool maybeRan)
        {
            Message = message;
            MaybeRan = maybeRan;
        }

        public string Message { get; }

        public bool MaybeRan { get; }
    }
}

/// <summary>One stream's main-thread cost from stream_costs.</summary>
public sealed class StreamCost
{
    public StreamCost(long stream, long calls, double mainThreadMs, long overBudgetFrames)
    {
        Stream = stream;
        Calls = calls;
        MainThreadMs = mainThreadMs;
        OverBudgetFrames = overBudgetFrames;
    }

    public long Stream { get; }

    public long Calls { get; }

    public double MainThreadMs { get; }

    public long OverBudgetFrames { get; }
}
