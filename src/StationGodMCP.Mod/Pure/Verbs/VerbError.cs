#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>A verb call's refusal: its code, its message in verb words, and the data a program can act on.</summary>
internal sealed record VerbError(string Code, string Message, JToken? Data = null)
{
    internal const string InvalidArgument = "invalid_argument";
    internal const string MethodNotFound = "method_not_found";

    /// <summary>
    /// The method is no verb with a handler here: a per-tool name, a verb whose handler this game's StationGod does not
    /// have, or a made-up name. Lists the verbs; nothing ran.
    /// </summary>
    internal static VerbError UnknownMethod(string method, string? what = null)
    {
        string called = what == null ? $"'{method}'" : $"'{method}' with what '{what}'";
        return new VerbError(MethodNotFound,
            $"Unknown method {called}: nothing ran. A call is a verb, {{\"method\": \"<verb>\", \"params\": " +
            $"{{\"what\": ..., options}}}}, with verb one of {string.Join(", ", VerbSurface.VerbNames)}.");
    }

    /// <summary>A game check's refusal as a verb error: its code, its message, and its data as the replies write it.</summary>
    internal static VerbError Of(ApiException refused) =>
        new VerbError(refused.Code, refused.Message,
            refused.Details == null ? null : JToken.FromObject(refused.Details, ApiJson.Fresh()));

    /// <summary>{code, message, data?}: a row's error.</summary>
    internal JObject ToJson()
    {
        JObject error = new JObject { ["code"] = Code, ["message"] = Message };
        if (Data != null)
        {
            error["data"] = Data.DeepClone();
        }

        return error;
    }
}

/// <summary>One thing wrong with a verb call: where (an option name or path), what, and the nearest valid names.</summary>
internal sealed record OptionProblem(string Path, string Problem, IReadOnlyList<string>? Nearest = null)
{
    internal JObject ToJson()
    {
        JObject problem = new JObject { ["path"] = Path, ["problem"] = Problem };
        if (Nearest is { Count: > 0 })
        {
            problem["nearest"] = new JArray(Nearest);
        }

        return problem;
    }

    /// <summary>
    /// Every problem at once as one invalid_argument: "label: path: problem (nearest: a, b); ...", data {problems,
    /// valid?}, valid being the what's own options with the nearest three to an unknown name first.
    /// </summary>
    internal static VerbError Refusal(string label, IReadOnlyList<OptionProblem> problems,
        IReadOnlyList<string>? valid = null)
    {
        StringBuilder message = new StringBuilder();
        message.Append(label).Append(": ");
        JArray listed = new JArray();
        for (int index = 0; index < problems.Count; index++)
        {
            OptionProblem problem = problems[index];
            if (index > 0)
            {
                message.Append("; ");
            }

            message.Append(problem.Path).Append(": ").Append(problem.Problem);
            if (problem.Nearest is { Count: > 0 })
            {
                message.Append(" (nearest: ").Append(string.Join(", ", problem.Nearest)).Append(')');
            }

            listed.Add(problem.ToJson());
        }

        message.Append('.');
        JObject data = new JObject { ["problems"] = listed };
        if (valid is { Count: > 0 })
        {
            data["valid"] = new JArray(valid);
        }

        return new VerbError(VerbError.InvalidArgument, message.ToString(), data);
    }
}
