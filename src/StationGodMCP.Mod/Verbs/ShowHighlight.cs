#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Show;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// show highlight (catalogue show.json): show the player where things (id), a cable, pipe or chute network (network)
/// or far points (at) are, through walls, frames and terrain, tinted and labelled, for the call's seconds. A new call
/// replaces what the last drew unless keep; clear only removes what is drawn. Read only: nothing in the world changes.
/// </summary>
internal static class ShowHighlight
{
    /// <summary>
    /// The call: the what's options as they were bound, and the reader they were bound from. clear goes with targets,
    /// seconds and keep, and a defaulted option (seconds, keep) reads the same whether the call gave it or left it
    /// out, so only the reader says what the call gave.
    /// </summary>
    internal sealed class Call
    {
        private Call(ShowHighlightOptions options, OptionReader given)
        {
            Options = options;
            Given = given;
        }

        internal ShowHighlightOptions Options { get; }

        internal OptionReader Given { get; }

        internal static Call Bind(OptionReader given) => new Call(ShowHighlightOptions.Bind(given), given);
    }

    internal static VerbOutcome Run(Call call)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        HighlightOrder? order = HighlightOrder.Of(call.Options, call.Given, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ShowHighlightOptions.Spec.Label, problems));
        }

        HighlightView view = order.Clear ? HighlightScreen.Clear() : HighlightScreen.Show(order.Request!);
        return new VerbOutcome.Answered(view);
    }
}
