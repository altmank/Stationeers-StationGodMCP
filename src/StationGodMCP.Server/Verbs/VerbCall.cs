using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>Who is calling (docs/architecture/hub.md, Faces): it decides the options taken and the reply shapes.</summary>
public enum Face
{
    /// <summary>The MCP client: the tool list's options.</summary>
    Direct,

    /// <summary>A run's worker: the stubs' options (confirm, max_wait, queue, offset refused).</summary>
    Script,

    /// <summary>A --hub caller: the stubs' options plus confirm.</summary>
    Program
}

/// <summary>One verb call as it enters the pipeline: the verb, its what (null for a whatless verb), the options.</summary>
public sealed class VerbCall
{
    public VerbCall(string verb, string? what, JsonObject args, Face face, long stream = 0, int? line = null)
    {
        Verb = verb;
        What = what;
        Args = args;
        Face = face;
        Stream = stream;
        Line = line;
    }

    public string Verb { get; }

    public string? What { get; }

    /// <summary>The options by name, without what.</summary>
    public JsonObject Args { get; }

    public Face Face { get; }

    /// <summary>The game stream the call goes on: 0 for the MCP client's direct calls.</summary>
    public long Stream { get; }

    /// <summary>The script's code line (1-based) for a run's call.</summary>
    public int? Line { get; }

    /// <summary>The gate for this call's writes (a run's route); null: the face's own (the direct write rule).</summary>
    public IWriteGate? Gate { get; init; }

    /// <summary>A line the reply carries (a repeat that sent a spread again says its selection may differ).</summary>
    internal string? Note { get; init; }

    /// <summary>The call was recorded where it arrived (the MCP adapter records refusals before the pipeline sees them).</summary>
    internal bool Recorded { get; init; }
}

/// <summary>How a verb call ended: its reply, or why it failed.</summary>
public abstract class VerbReply
{
    private VerbReply()
    {
    }

    public sealed class Done : VerbReply
    {
        public Done(JsonNode? reply)
        {
            Reply = reply;
        }

        public JsonNode? Reply { get; }
    }

    /// <summary>
    /// A refusal or an unreachable game. Fix says what to send next; MaybeRan that a write may have acted; Stopped that
    /// the write rule refused it; PassEnd that it ends a report pass.
    /// </summary>
    public sealed class Failed : VerbReply
    {
        public Failed(string code, string message, string? fix = null, JsonNode? data = null, bool maybeRan = false,
            bool stopped = false, bool passEnd = false)
        {
            Code = code;
            Message = message;
            Fix = fix;
            Data = data;
            MaybeRan = maybeRan;
            Stopped = stopped;
            PassEnd = passEnd;
        }

        public string Code { get; }

        public string Message { get; }

        public string? Fix { get; }

        public JsonNode? Data { get; }

        public bool MaybeRan { get; }

        public bool Stopped { get; }

        public bool PassEnd { get; }

        /// <summary>The error object: {code, message, fix?, maybe_ran?, stopped?, pass_end?, data?}.</summary>
        public JsonObject ToJson()
        {
            JsonObject error = new() { ["code"] = Code, ["message"] = Message };
            if (Fix != null)
            {
                error["fix"] = Fix;
            }

            if (MaybeRan)
            {
                error["maybe_ran"] = true;
            }

            if (Stopped)
            {
                error["stopped"] = true;
            }

            if (PassEnd)
            {
                error["pass_end"] = true;
            }

            if (Data != null)
            {
                error["data"] = Data.DeepClone();
            }

            return error;
        }
    }
}

/// <summary>The codes the pipeline answers with itself.</summary>
internal static class VerbFailure
{
    internal const string InvalidArgument = "invalid_argument";
    internal const string NotAvailable = "not_available";
    internal const string GameUnavailable = "game_unavailable";
}
