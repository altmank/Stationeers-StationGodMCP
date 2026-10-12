#nullable enable

using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// drop_job on the wire (protocol.md, Protocol methods): a call run on the main thread that drops a building run still
/// waiting in the job queue, answered {job_id, dropped: true} in the reply envelope. The drop itself is the job slot's
/// (HeldTickJobs.DropWaiting); its refusals (job_not_waiting, job_not_found) are the reply's error.
/// </summary>
internal sealed class JobDropCalls
{
    internal const string DropJobMethod = "drop_job";

    private readonly Func<string, object> _drop;

    /// <summary>drop takes a job id and answers the reply's result, or throws ApiException with the refusal.</summary>
    internal JobDropCalls(Func<string, object> drop)
    {
        _drop = drop;
    }

    internal static bool Handles(string method) => method == DropJobMethod;

    /// <summary>drop_job run on the main thread at this frame: the reply message.</summary>
    internal string Run(ProtocolCall call, double queueWaitMs, long frame)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        double queueMs = Math.Round(queueWaitMs, 2);
        ErrorView? error = null;
        object? result = null;
        try
        {
            if (!TryJobId(request.Params, out string jobId, out string problem))
            {
                throw ApiErrors.InvalidArgument(problem);
            }

            result = _drop(jobId);
        }
        catch (ApiException refused)
        {
            error = new ErrorView(refused.Code, refused.Message);
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in the drop answers this call, never the frame.
            error = new ErrorView("internal_error", exception.Message);
        }

        double elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        return ApiJson.WriteShared(error != null
            ? CallReplyView.Failed(request.Id, error, elapsedMs, queueMs, frame, budgetHit)
            : CallReplyView.Of(request.Id, result!, false, elapsedMs, queueMs, frame, budgetHit));
    }

    // drop_job's params: {job_id}, a non-empty string; anything else is named in an invalid_argument message.
    private static bool TryJobId(JObject? parameters, out string jobId, out string problem)
    {
        jobId = string.Empty;
        problem = string.Empty;
        JObject given = parameters ?? new JObject();
        foreach (JProperty property in given.Properties())
        {
            if (property.Name != "job_id")
            {
                problem = $"drop_job has no argument '{property.Name}'; it takes job_id.";
                return false;
            }
        }

        JToken? token = given["job_id"];
        string? text = token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "Argument 'job_id' must be the job_id a queued run answered, such as \"upgrade-7\".";
            return false;
        }

        jobId = text!;
        return true;
    }
}
