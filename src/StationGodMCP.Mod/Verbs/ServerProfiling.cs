#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// server profiling (catalogue server.json): profile what StationGod costs the game's main thread — action on, off,
/// reset or report (ProfilingControl) — and answer the switch's state and the session's report (ProfilingView). Changes
/// nothing in the world; host only, where requests run. The route is acts: a call acts without confirm.
/// </summary>
internal static class ServerProfiling
{
    /// <summary>
    /// The call: the what's options as they were bound, and the reader they were bound from. The catalogue fills
    /// slow_frame_ms's default in whether the call gives one or not, so only the reader says what the call gave: on
    /// again without it keeps the session's threshold.
    /// </summary>
    internal sealed class Call
    {
        private Call(ServerProfilingOptions options, OptionReader given)
        {
            Options = options;
            Given = given;
        }

        internal ServerProfilingOptions Options { get; }

        internal OptionReader Given { get; }

        internal static Call Bind(OptionReader given) => new Call(ServerProfilingOptions.Bind(given), given);
    }

    internal static VerbOutcome Run(Call call)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        ProfilingOrder? order = ProfilingOrder.Of(call.Options, call.Given, problems);
        return order != null
            ? Answer.Of(ProfilingControl.Run(order))
            : new VerbOutcome.Refused(OptionProblem.Refusal(ServerProfilingOptions.Spec.Label, problems));
    }
}
