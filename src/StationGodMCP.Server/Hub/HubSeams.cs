using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The pipeline methods a session does not answer itself (hub.md, The hub protocol): ref, more, wait and hold. Each
/// answers as a verb does: the result, or the hub error object.
/// </summary>
public interface IPipelineExtras
{
    Task<VerbReply> RefAsync(PipelineRequest request, CancellationToken cancellation);

    Task<VerbReply> MoreAsync(PipelineRequest request, CancellationToken cancellation);

    Task<VerbReply> WaitAsync(PipelineRequest request, CancellationToken cancellation);

    Task<VerbReply> HoldAsync(PipelineRequest request, CancellationToken cancellation);
}

/// <summary>One call of a pipeline method as the session received it.</summary>
public sealed class PipelineRequest
{
    public PipelineRequest(string method, JsonObject parameters, Face face, long stream, string clientId, int? line)
    {
        Method = method;
        Parameters = parameters;
        Face = face;
        Stream = stream;
        ClientId = clientId;
        Line = line;
    }

    public string Method { get; }

    /// <summary>The call's params, line included.</summary>
    public JsonObject Parameters { get; }

    public Face Face { get; }

    /// <summary>The hub stream the caller's stream maps to.</summary>
    public long Stream { get; }

    /// <summary>The session's client_id.</summary>
    public string ClientId { get; }

    /// <summary>params.line: the code line (1-based) of a run's call.</summary>
    public int? Line { get; }
}

/// <summary>Until refs, cursors, waits and holds are served, each answers not_available.</summary>
public sealed class NoPipelineExtras : IPipelineExtras
{
    public static readonly NoPipelineExtras Instance = new();

    private NoPipelineExtras()
    {
    }

    public Task<VerbReply> RefAsync(PipelineRequest request, CancellationToken cancellation) => NotAvailable(request);

    public Task<VerbReply> MoreAsync(PipelineRequest request, CancellationToken cancellation) => NotAvailable(request);

    public Task<VerbReply> WaitAsync(PipelineRequest request, CancellationToken cancellation) => NotAvailable(request);

    public Task<VerbReply> HoldAsync(PipelineRequest request, CancellationToken cancellation) => NotAvailable(request);

    private static Task<VerbReply> NotAvailable(PipelineRequest request) =>
        Task.FromResult<VerbReply>(new VerbReply.Failed(VerbFailure.NotAvailable,
            $"{request.Method} is not served by this hub yet."));
}

/// <summary>
/// The run a worker's session serves (hub.md, Runs): it takes the worker's run_analysis (the first only; the session
/// refuses later ones) and its run_end. A session without one refuses both messages with protocol_error.
/// </summary>
public interface IRunChannel
{
    Task AnalysisAsync(JsonElement message);

    Task EndedAsync(JsonElement message);
}

/// <summary>How a session is set up by whoever creates it.</summary>
public sealed class HubSessionSettings
{
    /// <summary>Program for a --hub caller, Script for a run's worker.</summary>
    public Face Face { get; init; } = Face.Program;

    /// <summary>The hub's name for the caller, sent as welcome.client_id; a fresh hub-N when null.</summary>
    public string? ClientId { get; init; }

    /// <summary>The welcome's run key (hub.md, Runs); none when null.</summary>
    public JsonObject? Run { get; init; }

    /// <summary>Where run_analysis and run_end go; without one they are refused.</summary>
    public IRunChannel? RunChannel { get; init; }

    public IPipelineExtras Extras { get; init; } = NoPipelineExtras.Instance;

    /// <summary>
    /// The hub stream the caller's stream 0 maps to (a run's stream, which its creator reads the costs of and ends);
    /// a fresh one the session ends on close when null.
    /// </summary>
    public long? MainStream { get; init; }

    /// <summary>
    /// True when the caller's session_end ends the link's session too: the --hub caller, the only one on its link. A
    /// worker's session_end ends only its own session.
    /// </summary>
    public bool EndsLinkSession { get; init; }
}

/// <summary>Why a session stopped reading.</summary>
public enum HubSessionEnd
{
    /// <summary>The caller closed its side (EOF).</summary>
    InputEnded,

    /// <summary>The caller sent bye.</summary>
    Bye,

    /// <summary>The caller broke the protocol; the session answered protocol_error (or unsupported_protocol).</summary>
    ProtocolError
}
