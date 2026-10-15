#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Verbs;

/// <summary>
/// check rules (catalogue check.json): the rule set check layout and the build and plan verbs use. list (the rules in
/// effect and where each came from), validate (the files in effect, or rules_text before it is saved), fields (the
/// model's object types and the sets a select names), functions (the language's forms and the library), test (each
/// rule's examples, run without the world), explain (one rule on one subject in the world, with every value it read).
/// Read only.
/// </summary>
internal static class CheckRules
{
    private const int MaximumExplained = 10;

    [Profiled]
    internal static VerbOutcome Run(CheckRulesOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        RuleCheck? check = RuleCheck.Of(options, problems);
        if (check == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(CheckRulesOptions.Spec.Label, problems));
        }

        LintRuleSet set = check.RulesText != null ? LintRuleFiles.With(check.RulesText, "rules_text") : LintRuleFiles.Current();
        LintRulesView view = new LintRulesView(check.Action, new LintRuleSourceView(set));
        string? id = check.Rule;
        switch (check.Action)
        {
            case RuleCheck.List:
                view.Rules = new List<LintRuleView>();
                foreach (LintRule rule in set.Rules)
                {
                    if (id == null || rule.Id == id)
                    {
                        view.Rules.Add(new LintRuleView(rule, id != null || check.Full));
                    }
                }

                RequireFound(id, view.Rules.Count, set);
                break;
            case RuleCheck.Validate:
                view.Valid = set.Errors.Count == 0;
                break;
            case RuleCheck.Fields:
                view.Types = new List<LintTypeView>();
                foreach (ObjectType type in LintModel.Types)
                {
                    view.Types.Add(new LintTypeView(type));
                }

                view.Sets = new Dictionary<string, string>(LintSets.Docs);
                break;
            case RuleCheck.Functions:
                view.Functions = Functions();
                break;
            case RuleCheck.Test:
                view.Tests = new List<LintRuleTestView>();
                foreach (LintRule rule in set.Rules)
                {
                    if (id == null || rule.Id == id)
                    {
                        view.Tests.Add(new LintRuleTestView(rule.Id, LintExamples.Run(rule, LintGameLibrary.Library)));
                    }
                }

                RequireFound(id, view.Tests.Count, set);
                view.AllPassed = view.Tests.TrueForAll(test => test.Passed);
                break;
            case RuleCheck.Explain:
                view.Explained = Explain(set, id!, check.Subject!);
                break;
            default:
                return new VerbOutcome.Refused(OptionProblem.Refusal(CheckRulesOptions.Spec.Label, new[]
                {
                    new OptionProblem("action", "is list, validate, fields, functions, test or explain")
                }));
        }

        return Answer.Of(view);
    }

    private static void RequireFound(string? id, int found, LintRuleSet set)
    {
        if (id != null && found == 0)
        {
            throw ApiErrors.Refused("rule_not_found", new List<string>(set.Disabled).Contains(id)
                ? $"check rules: rule {id} is turned off (\"enabled\": false)."
                : $"check rules: no rule {id} is in effect; action list names them.");
        }
    }

    private static List<LintFunctionView> Functions()
    {
        List<LintFunctionView> functions = new List<LintFunctionView>();
        foreach (KeyValuePair<string, string> form in LintForms.Docs)
        {
            functions.Add(new LintFunctionView(form.Key, form.Value.Split(':')[0], form.Value, true, false));
        }

        List<LintFunction> library = new List<LintFunction>(LintGameLibrary.Library.Functions);
        library.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (LintFunction function in library)
        {
            functions.Add(new LintFunctionView(function.Name, function.Signature, function.Doc, false, function.Cached));
        }

        return functions;
    }

    // One rule on the subjects id (a thing, a network, a room; port: one of its ports), at (a cell) and other (the
    // other of a pair) name, in a world of the 2 m cells around it.
    private static List<LintExplainView> Explain(LintRuleSet set, string id, ExplainSubject subject)
    {
        LintRule rule = set.Find(id) ??
                        throw ApiErrors.Refused("rule_not_found", $"check rules: no rule {id} is in effect; action list names them.");
        GameLintWorld world = GameLintWorld.Audit(Around(subject.At ?? Where(subject.Id!.Value)));
        LintContext context = new LintContext(world);
        List<LintExplainView> explained = new List<LintExplainView>();
        int matched = 0;
        IReadOnlyList<ILintObject> subjects = world.Subjects(rule.Select.Set);
        if (rule.Select.IsPairs)
        {
            foreach (ILintObject a in subjects)
            {
                foreach (ILintObject b in subjects)
                {
                    if (a.Key.CompareTo(b.Key) < 0 && Names(a, b, subject.Id, subject.Other) &&
                        ++matched <= MaximumExplained)
                    {
                        LintSubjectRef pair = new LintSubjectRef(a, b);
                        explained.Add(new LintExplainView(rule.Id, pair.Describe, LintEngine.Judge(rule, pair, context, true, true)));
                    }
                }
            }
        }
        else
        {
            foreach (ILintObject each in subjects)
            {
                if (Matches(each, subject) && ++matched <= MaximumExplained)
                {
                    explained.Add(new LintExplainView(rule.Id, each.Describe,
                        LintEngine.Judge(rule, new LintSubjectRef(each), context, true, true)));
                }
            }
        }

        Truncations.Note("explained", explained.Count, matched,
            "name one subject: other for a pair, port for one port of a device");

        if (explained.Count == 0)
        {
            throw ApiErrors.Refused("subject_not_found",
                $"check rules: rule {id} selects {rule.Select.Set}; none of those around the place given is the subject named.");
        }

        return explained;
    }

    private static bool Names(ILintObject a, ILintObject b, long? reference, long? other) =>
        (a.ReferenceId == reference && (other == null || b.ReferenceId == other)) ||
        (b.ReferenceId == reference && (other == null || a.ReferenceId == other));

    private static bool Matches(ILintObject each, ExplainSubject subject)
    {
        if (subject.At is Vec3 point)
        {
            return each.Position is Vec3 position && (position - point).Length < 0.3;
        }

        if (each.ReferenceId != subject.Id)
        {
            return false;
        }

        return subject.Port == null || !(each is PortSubject) ||
               each.Key.EndsWith("/" + subject.Port.Value, StringComparison.Ordinal);
    }

    // Where a thing, a network (its first member) or a room (its first cell) is.
    private static Vec3 Where(long id)
    {
        if (GameLookup.TryFindThing(new ThingId(id), out Thing thing))
        {
            return Bodies.V(thing.Position);
        }

        Room? room = StructureAirRecord.FindRoom(id);
        if (room != null)
        {
            foreach (WorldGrid grid in room.Grids)
            {
                return Bodies.V(grid.Value.ToVector3());
            }
        }

        foreach (UpgradeFamily family in new UpgradeFamily[] { new CableFamily(), new PipeFamily(), new ChuteFamily() })
        {
            try
            {
                List<SmallGrid> members = family.NetworkMembers(new ThingId(id));
                if (members.Count > 0)
                {
                    return Bodies.V(members[0].Position);
                }
            }
            catch (ApiException)
            {
            }
        }

        throw ApiErrors.Refused("subject_not_found", $"check rules: no thing, room or network has id {id}.");
    }

    // The 2 m cell holding the point and the 26 around it.
    private static List<GridCell> Around(Vec3 point)
    {
        GridCell centre = SmallCellCode.LargeOf(new GridCell((int)Math.Round(point.X * 10.0),
            (int)Math.Round(point.Y * 10.0), (int)Math.Round(point.Z * 10.0)));
        List<GridCell> cells = new List<GridCell>(27);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    cells.Add(new GridCell(centre.X + dx * SmallCellCode.Large, centre.Y + dy * SmallCellCode.Large,
                        centre.Z + dz * SmallCellCode.Large));
                }
            }
        }

        return cells;
    }
}
