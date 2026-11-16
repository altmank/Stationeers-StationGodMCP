#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write interact (catalogue write.json): a press of one thing's switch or button as the player makes it. The game is
/// asked first whether the press works and refuses with its own text (not_accepted); out of reach is out_of_range
/// unless forced; then the press is made. Answers {thing, interactable, before, after, player, message}. Host only;
/// cannot be undone.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteInteract
{
    internal static VerbOutcome Run(WriteInteractOptions options)
    {
        string label = WriteInteractOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        string? name = NameOf(options.Interactable);
        if (name == null)
        {
            problems.Add(new OptionProblem("interactable", "is a name or an index (inspect device include interactables)"));
        }

        if (options.State is long state && (state < 0 || state > 100))
        {
            problems.Add(new OptionProblem("state", "is a small non-negative integer"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        Human player = options.AsPlayer == null ? PlayerOrigin.RequireHuman() : MovablePlayers.Find(options.AsPlayer);
        Assets.Scripts.Objects.Thing thing =
            GameLookup.RequireThing(TargetIds.Of(options.Selector.Ids!.Ids[0], label));
        return new VerbOutcome.Answered(Interactables.Press(thing, name!, (int?)options.State,
            options.Hand == "held", options.Force == true, player));
    }

    private static string? NameOf(JToken? token) =>
        token?.Type switch
        {
            JTokenType.String => ((string)token!).Trim().Length > 0 ? ((string)token!).Trim() : null,
            JTokenType.Integer => ((long)token!).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };
}
