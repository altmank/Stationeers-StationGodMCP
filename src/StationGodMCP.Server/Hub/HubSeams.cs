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

    public Task<VerbReply> RefAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);

    public Task<VerbReply> MoreAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);

    public Task<VerbReply> WaitAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);

    public Task<VerbReply> HoldAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);
}

/// <summary>
/// The answer a face gives for a pipeline method nothing there serves (hub.md, The hub protocol): not_available,
/// naming the method the caller sent.
/// </summary>
internal static class PipelineExtras
{
    internal static Task<VerbReply> NotAvailable(PipelineRequest request) =>
        Task.FromResult<VerbReply>(new VerbReply.Failed(VerbFailure.NotAvailable,
            $"{request.Method} is not served by this hub yet."));
}

/// <summary>
/// The program face's pipeline methods (hub.md, Faces): a `--hub` caller walks a reply's main list with `more` through
/// the pipeline's own hub cursors, the same cursors a run walks, so a program pages a list exactly as a run does (the
/// pipeline answers an unknown or freed cursor cursor_expired, and frees the cursor when the walk ends). ref, wait and
/// hold belong to a run and have no program caller, so they keep the shared not_available answer.
/// </summary>
public sealed class ProgramFaceExtras : IPipelineExtras
{
    private readonly VerbPipeline _pipeline;

    public ProgramFaceExtras(VerbPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public Task<VerbReply> RefAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);

    public Task<VerbReply> WaitAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);

    public Task<VerbReply> HoldAsync(PipelineRequest request, CancellationToken cancellation) =>
        PipelineExtras.NotAvailable(request);

    /// <summary>{cursor, limit?}: the next entries of a reply's main list, as a run's more answers.</summary>
    public Task<VerbReply> MoreAsync(PipelineRequest request, CancellationToken cancellation)
    {
        if (JsonNodes.Text(request.Parameters["cursor"]) is not { } cursor)
        {
            return Task.FromResult<VerbReply>(new VerbReply.Failed(VerbFailure.InvalidArgument,
                "more needs cursor: the hub cursor a reply carried."));
        }

        int? limit = JsonNodes.Number(request.Parameters["limit"]) is { } most ? (int)most : null;
        return _pipeline.MoreAsync(request.Stream, cursor, limit, cancellation);
    }
}

/// <summary>
/// The run a worker's session serves (hub.md, Runs): it takes the worker's run_analysis - one for the run's own code
/// and one for each saved script the code loads, each merged into the run by Run.AnalysisAsync - and its run_end. A
/// session refuses run_analysis only after run_end; a session without a channel refuses both messages with
/// protocol_error.
/// </summary>
public interface IRunChannel
{
    Task AnalysisAsync(JsonElement message);

    Task EndedAsync(JsonElement message);

    /// <summary>Whether this run started the job (a job-start reply named it while running). A run hears job_ended
    /// events of its own jobs alone; a session with no run hears every job.</summary>
    bool StartedJob(string jobId);
}

/// <summary>
/// Where a run's session sends its verb calls instead of straight to the pipeline: the run delivers its stop there,
/// gives each call its gate and keeps its write log.
/// </summary>
public interface IVerbCalls
{
    Task<VerbReply> VerbAsync(VerbCall call, CancellationToken cancellation);
}

/// <summary>How a session is set up by whoever creates it.</summary>
public sealed class HubSessionSettings
{
    public HubSessionSettings(Face face)
    {
        Face = face;
    }

    /// <summary>Program for a --hub caller, Script for a run's worker.</summary>
    public Face Face { get; }

    /// <summary>
    /// True when the caller may call mod methods directly (pass-through, the other mod methods, stream_end, release,
    /// cursor_close): the --hub program only. Without it a session takes verb, ref, more, wait, hold, subscribe and
    /// unsubscribe, so a run's writes always meet its gate while its reads (a subscription included) do not.
    /// </summary>
    public bool PassThrough { get; init; }

    /// <summary>Where verb calls go; the pipeline itself when null.</summary>
    public IVerbCalls? Verbs { get; init; }

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
    ProtocolError,

    /// <summary>The caller sent a line over max_request_bytes; the session answered request_too_large.</summary>
    RequestTooLarge,

    /// <summary>The caller stopped reading: the oldest queued line waited past the slow-client bound.</summary>
    SlowClient,

    /// <summary>Writing to the caller failed.</summary>
    OutputBroken
}
