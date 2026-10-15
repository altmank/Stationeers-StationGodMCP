#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Protocol;

/// <summary>
/// read sample (catalogue read.json): watches up to 32 logic values for up to 30 s on the real clock and answers the
/// first readings and every change with its time. The options become a SampleRequest (refused with every problem at
/// once); the run is the subscription lane's (SubscriptionHub.StartSample), and the call waits in the slice lane on its
/// watch, past its deadline by the seconds it runs (x-cost duration), until the run's last sample is taken. Every value
/// is read once before the run starts: values that cannot be read refuse the call, all of them at once in the one
/// problems list an option check answers, and nothing is sampled. A value that can no longer be read mid-run (its thing gone) ends its own series; the rest go on.
/// </summary>
internal static class ReadSample
{
    internal static VerbHandler Handler(Func<SubscriptionHub?> hub) =>
        VerbHandler.For(ReadSampleOptions.Spec, ReadSampleOptions.Bind, options => Run(hub(), options));

    internal static VerbOutcome Run(SubscriptionHub? hub, ReadSampleOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        if (SampleRequest.Of(options, problems) is not { } request)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ReadSampleOptions.Spec.Label, problems));
        }

        if (hub == null)
        {
            return new VerbOutcome.Refused(new VerbError("game_unavailable",
                "read sample: the game's sample lane has not started; nothing ran."));
        }

        LogicSampleRead check = hub.CheckSample(request);
        if (!check.Ok)
        {
            return new VerbOutcome.Refused(new VerbError(check.Code!, check.Message ?? check.Code!));
        }

        return check.Misses.Count > 0
            ? new VerbOutcome.Refused(Unreadable(request, check.Misses))
            : new VerbOutcome.Pending(hub.StartSample(request, CallContext.Current));
    }

    /// <summary>
    /// Every value that cannot be read, at once, as one invalid_argument in OptionCheck's form: each value under the
    /// option path that named it, "logic cannot be read: why", in data.problems. Nothing was sampled.
    /// </summary>
    internal static VerbError Unreadable(SampleRequest request, IReadOnlyList<SampleMiss> misses)
    {
        List<OptionProblem> problems = new List<OptionProblem>(misses.Count);
        foreach (SampleMiss miss in misses)
        {
            SampleTarget target = request.Targets[miss.Index];
            string logic = target.Logic.Type == JTokenType.String
                ? (string)target.Logic!
                : target.Logic.ToString(Newtonsoft.Json.Formatting.None);
            problems.Add(new OptionProblem(target.Path, $"{target.Id} {logic} cannot be read, so nothing was sampled: {miss.Message}"));
        }

        return OptionProblem.Refusal(ReadSampleOptions.Spec.Label, problems);
    }
}

/// <summary>A sample run's call waiting in the slice lane: ready once the run has its outcome, which is the call's answer.</summary>
internal sealed class SampleWatch : IPendingResult
{
    private VerbOutcome? _outcome;

    public bool Step(double untilMs) => _outcome != null;

    public object Result() => _outcome ?? throw new InvalidOperationException("The sample run has not ended.");

    /// <summary>The run ended: its reply, or the error a sample's read failed with.</summary>
    internal void End(SampleOutcome<BatchItemView> outcome) => _outcome = outcome switch
    {
        SampleOutcome<BatchItemView>.Completed completed => new VerbOutcome.Answered(SampleReply.Of(completed.Result)),
        SampleOutcome<BatchItemView>.Failed failed => new VerbOutcome.Refused(new VerbError(failed.Code, failed.Message)),
        _ => new VerbOutcome.Refused(new VerbError("internal_error", "read sample: a run ended with no outcome."))
    };
}

/// <summary>
/// read sample's reply as the catalogue declares it: gateway_id, started_at_utc, duration_seconds, interval_seconds,
/// sample_count, change_count, and changes: one row per value that changed, {reference_id, logic_type, value, at_s},
/// the first sample's at 0 s.
/// </summary>
internal static class SampleReply
{
    internal const string World = "world";

    internal static JObject Of(SampleResult<BatchItemView> result)
    {
        JArray changes = new JArray();
        foreach (SampleChange<BatchItemView> change in result.Changes)
        {
            foreach (BatchItemView reading in change.Readings)
            {
                if (reading is LogicReadItemView read)
                {
                    changes.Add(new JObject
                    {
                        ["reference_id"] = read.ReferenceId.ToString(),
                        ["logic_type"] = read.LogicType?.Name,
                        ["value"] = read.Value,
                        ["at_s"] = change.ElapsedSeconds
                    });
                }
            }
        }

        return new JObject
        {
            ["gateway_id"] = result.GatewayId ?? World,
            ["started_at_utc"] = JToken.FromObject(result.StartedAtUtc, ApiJson.Serializer),
            ["duration_seconds"] = result.DurationSeconds,
            ["interval_seconds"] = result.IntervalSeconds,
            ["sample_count"] = result.SampleCount,
            ["change_count"] = changes.Count,
            ["changes"] = changes
        };
    }
}
