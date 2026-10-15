#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// A what that answers in parts (inspect chip's parts): every part asked is read before anything is answered. One part
/// answers that part's reply as it is; several answer {part: reply, ...} in the order asked. When any part cannot be
/// answered, the call answers none and refuses with every such part at once, each under parts[i] in the one problems
/// list an option check answers.
/// </summary>
internal static class PartAnswers
{
    internal static VerbOutcome Of(string label, IReadOnlyList<string> parts, Func<string, object> answer)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        ReplyObject replies = new ReplyObject(parts.Count);
        object? first = null;
        for (int index = 0; index < parts.Count; index++)
        {
            string part = parts[index];
            try
            {
                object reply = answer(part);
                first ??= reply;
                replies.Add(part, reply);
            }
            catch (ApiException refused)
            {
                problems.Add(new OptionProblem($"parts[{index}]",
                    $"{part} cannot be answered ({refused.Code}): {refused.Message}"));
            }
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        return new VerbOutcome.Answered(parts.Count == 1 && IsObject(first) ? first! : replies);
    }

    // A reply written as a JSON object: no list, text or number.
    internal static bool IsObject(object? reply) =>
        reply != null && !(reply is string) && !reply.GetType().IsPrimitive && !ViewKeys.IsList(reply) &&
        !(reply is JValue);
}
