using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// One handler call a verb call becomes: today's method, its arguments in handler names, the rename envelope that makes
/// the mod print verb names, and what the pipeline does around it (a file argument to read, a part name in a several-part
/// reply, a fallback when the mod refuses with a code, a job to poll, a reshaping the catalogue declares).
/// </summary>
internal sealed class HandlerCall
{
    internal HandlerCall(string handler, JsonObject args, JsonElement rename)
    {
        Handler = handler;
        Args = args;
        Rename = rename;
    }

    internal string Handler { get; }

    internal JsonObject Args { get; }

    internal JsonElement Rename { get; }

    /// <summary>The part this call answers in a reply of several parts ({code, pins, run}).</summary>
    internal string? Part { get; init; }

    /// <summary>Sent instead when the mod refuses this call with FallbackCode.</summary>
    internal HandlerCall? Fallback { get; init; }

    internal string? FallbackCode { get; init; }

    /// <summary>The call is a job poll: polled again until the job is final or max_wait passes.</summary>
    internal bool PollsJob { get; init; }

    /// <summary>File arguments to read into Args before sending (x-file-arguments), in verb names for messages.</summary>
    internal IReadOnlyList<VerbFileArgument> Files { get; init; } = [];

    /// <summary>A reshaping of the reply the catalogue declares (find device's paging and prefab parts).</summary>
    internal Func<JsonNode?, JsonNode?>? Reshape { get; init; }
}

/// <summary>A handler's file argument with the option names a caller uses for it (code_file into code).</summary>
internal sealed class VerbFileArgument(FileArgument handler, string option, string into)
{
    internal FileArgument Handler { get; } = handler;

    internal string Option { get; } = option;

    internal string Into { get; } = into;
}

/// <summary>What LegacyVerbMap made of a checked verb call.</summary>
internal abstract class Translation
{
    private Translation()
    {
    }

    /// <summary>Handler calls in order: one answers its reply as it is; several answer one key per part.</summary>
    internal sealed class Calls : Translation
    {
        internal Calls(List<HandlerCall> list)
        {
            List = list;
        }

        internal Calls(HandlerCall single)
        {
            List = [single];
        }

        internal List<HandlerCall> List { get; }
    }

    /// <summary>Answered without the game: a refusal, or a placeholder for what another piece brings.</summary>
    internal sealed class Answered : Translation
    {
        internal Answered(VerbReply reply)
        {
            Reply = reply;
        }

        internal VerbReply Reply { get; }
    }

    /// <summary>A read the choice of handler needs first (what kind of thing an id is); Then makes the choice.</summary>
    internal sealed class Probe : Translation
    {
        internal Probe(HandlerCall read, Func<GameReply, Translation> then)
        {
            Read = read;
            Then = then;
        }

        internal HandlerCall Read { get; }

        internal Func<GameReply, Translation> Then { get; }
    }
}
