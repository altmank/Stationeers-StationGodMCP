#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// A write given in parts that act in order (write chip: code, pins, run). Every part is checked before any acts
/// (check gives the part's act): when any part is refused, nothing acts and the call refuses with every refused part
/// at once, each under its name in the one problems list an option check answers. Then the parts act in order. One
/// part answers that part's reply as it is; several answer {part: reply, ...}. A part refused while acting stops the
/// rest, named in not_run: when it is the first part, nothing acted and its refusal is the call's ("Not run: ..."
/// added); after a part acted, the reply keeps what acted, the refused part as its error, and not_run.
/// </summary>
internal static class WriteParts
{
    internal static VerbOutcome Of(string label, IReadOnlyList<string> parts, Func<string, Func<object>> check)
    {
        List<Func<object>> acts = new List<Func<object>>(parts.Count);
        List<OptionProblem> problems = new List<OptionProblem>();
        foreach (string part in parts)
        {
            try
            {
                acts.Add(check(part));
            }
            catch (ApiException refused)
            {
                problems.Add(new OptionProblem(part, $"cannot be written ({refused.Code}): {refused.Message}"));
            }
        }

        return problems.Count > 0
            ? new VerbOutcome.Refused(OptionProblem.Refusal(label, problems))
            : Act(parts, acts);
    }

    private static VerbOutcome Act(IReadOnlyList<string> parts, IReadOnlyList<Func<object>> acts)
    {
        JsonSerializer serializer = ApiJson.Fresh();
        JObject reply = new JObject();
        for (int index = 0; index < parts.Count; index++)
        {
            string part = parts[index];
            try
            {
                reply[part] = ReplyFields.Value(acts[index](), serializer);
            }
            catch (ApiException refused)
            {
                VerbError error = VerbError.Of(refused);
                JArray notRun = new JArray();
                for (int rest = index + 1; rest < parts.Count; rest++)
                {
                    notRun.Add(parts[rest]);
                }

                if (index == 0)
                {
                    return new VerbOutcome.Refused(notRun.Count == 0
                        ? error
                        : error.WithMessage($"{error.Message} Not run: {string.Join(", ", notRun)}."));
                }

                reply[part] = error.ToJson();
                reply["not_run"] = notRun;
                return new VerbOutcome.Answered(reply);
            }
        }

        return new VerbOutcome.Answered(parts.Count == 1 && reply[parts[0]] is JObject one ? one : reply);
    }
}
