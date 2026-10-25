#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find, write and show lattice (catalogue topic lattice): Lattice's whole contract as JSON, through LatticePresence.
/// A problem with the call's JSON is invalid_argument naming its path; without Lattice, lattice_not_running.
/// </summary>
internal static class LatticeCalls
{
    internal const string NotRunning = "lattice_not_running";

    internal static VerbOutcome Answer(string label, LatticeAnswer answer)
    {
        if (!answer.Refused)
        {
            return new VerbOutcome.Answered(answer.Reply);
        }

        List<OptionProblem> problems = new List<OptionProblem>();
        foreach (string problem in answer.Problems)
        {
            int colon = problem.IndexOf(": ", System.StringComparison.Ordinal);
            problems.Add(colon > 0
                ? new OptionProblem(problem.Substring(0, colon), problem.Substring(colon + 2))
                : new OptionProblem("", problem));
        }

        return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
    }

    internal static VerbOutcome? Missing() => LatticePresence.Running
        ? null
        : new VerbOutcome.Refused(new VerbError(NotRunning,
            "Lattice is not installed or runs no world: the Lattice verbs need it."));
}

/// <summary>find lattice: any of Lattice's answers (ask and its args). Read only.</summary>
[ReplyFact(FactRule.Live, "Lattice's answer now")]
internal static class FindLattice
{
    internal static VerbOutcome Run(FindLatticeOptions options)
    {
        string label = FindLatticeOptions.Spec.Label;
        if (LatticeCalls.Missing() is { } missing)
        {
            return missing;
        }

        if (options.Args != null && !(options.Args is JObject))
        {
            return LatticeCalls.Answer(label, new LatticeAnswer(new List<string> { "args: is an object" }));
        }

        return LatticeCalls.Answer(label, LatticePresence.Ask(options.Ask ?? "", options.Args as JObject ?? new JObject()));
    }
}

/// <summary>write lattice: any Lattice command; reports Lattice's preview until confirm, then submits it.</summary>
[ReplyFact(FactRule.Live, "what Lattice changed, or would change")]
internal static class WriteLattice
{
    internal static VerbOutcome Run(WriteLatticeOptions options)
    {
        string label = WriteLatticeOptions.Spec.Label;
        if (LatticeCalls.Missing() is { } missing)
        {
            return missing;
        }

        bool acts = options.Report.Acts;
        LatticeAnswer answer = LatticePresence.Command(options.Command ?? new JObject(), acts);
        if (answer.Refused || acts)
        {
            return LatticeCalls.Answer(label, answer);
        }

        // A report changes nothing: report_only, as every write's report says.
        answer.Reply["report_only"] = true;
        return new VerbOutcome.Answered(answer.Reply);
    }
}

/// <summary>show lattice: one part of this seat's Lattice view (layer, set, drafts, clip or highlight).</summary>
[ReplyFact(FactRule.Live, "this seat's Lattice view")]
internal static class ShowLattice
{
    internal static VerbOutcome Run(ShowLatticeOptions options)
    {
        string label = ShowLatticeOptions.Spec.Label;
        if (LatticeCalls.Missing() is { } missing)
        {
            return missing;
        }

        List<(string Name, JToken? Given)> parts = new List<(string Name, JToken? Given)>
        {
            ("layer", options.Layer), ("set", options.Set), ("drafts", options.Drafts), ("clip", options.Clip),
            ("highlight", options.Highlight),
        };
        (string Name, JToken? Given)? chosen = null;
        foreach ((string name, JToken? given) in parts)
        {
            if (given == null)
            {
                continue;
            }

            if (chosen != null)
            {
                return LatticeCalls.Answer(label, new LatticeAnswer(new List<string>
                {
                    $"{name}: one of layer, set, drafts, clip or highlight per call ({chosen.Value.Name} is given too)"
                }));
            }

            chosen = (name, given);
        }

        if (chosen is not { } part)
        {
            return LatticeCalls.Answer(label, new LatticeAnswer(new List<string> { "layer: give one of layer, set, drafts, clip or highlight" }));
        }

        if (!(part.Given is JObject given1))
        {
            return LatticeCalls.Answer(label, new LatticeAnswer(new List<string> { $"{part.Name}: is an object" }));
        }

        return LatticeCalls.Answer(label, LatticePresence.View(part.Name, given1));
    }
}
