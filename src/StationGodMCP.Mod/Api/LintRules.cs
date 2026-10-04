#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// lint_rules: the rule set lint_layout and the place and plan tools use. list (the rules in effect and where each
/// came from), validate (the files in effect, or a file's text before it is saved), fields (the model's object types
/// and the sets a select names), functions (the language's forms and the library), test (each rule's examples, run
/// without the world), explain (one rule on one subject in the world, with every value it read). Read only.
/// </summary>
internal static class LintRulesApi
{
    private const int MaximumExplained = 10;

    internal static LintRulesView Handle(Args args)
    {
        string action = args.OptionalString("action") ?? "list";
        string? text = args.OptionalString("text");
        LintRuleSet set = text != null ? LintRuleFiles.With(text, "text") : LintRuleFiles.Current();
        LintRulesView view = new LintRulesView(action, new LintRuleSourceView(set));
        string? id = args.OptionalString("rule_id");
        switch (action)
        {
            case "list":
                view.Rules = new List<LintRuleView>();
                foreach (LintRule rule in set.Rules)
                {
                    if (id == null || rule.Id == id)
                    {
                        view.Rules.Add(new LintRuleView(rule, id != null || (args.OptionalBool("full") ?? false)));
                    }
                }

                RequireFound(id, view.Rules.Count, set);
                break;
            case "validate":
                view.Valid = set.Errors.Count == 0;
                break;
            case "fields":
                view.Types = new List<LintTypeView>();
                foreach (ObjectType type in LintModel.Types)
                {
                    view.Types.Add(new LintTypeView(type));
                }

                view.Sets = new Dictionary<string, string>(LintSets.Docs);
                break;
            case "functions":
                view.Functions = Functions();
                break;
            case "test":
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
            case "explain":
                view.Explained = Explain(args, set, id ?? throw ApiErrors.InvalidArgument("explain needs rule_id."));
                break;
            default:
                throw ApiErrors.InvalidArgument("action is list, validate, fields, functions, test or explain.");
        }

        return view;
    }

    private static void RequireFound(string? id, int found, LintRuleSet set)
    {
        if (id != null && found == 0)
        {
            throw ApiErrors.Refused("rule_not_found",
                Disabled(set, id) ? $"Rule {id} is turned off (\"enabled\": false)." : $"No rule {id} is in effect.");
        }
    }

    private static bool Disabled(LintRuleSet set, string id)
    {
        foreach (string each in set.Disabled)
        {
            if (each == id)
            {
                return true;
            }
        }

        return false;
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

    // One rule on the subjects reference_id (a thing, a network, a room; port: one of its ports), at (a cell) and
    // other_id (the other of a pair) name, in a world of the 2 m cells around it.
    private static List<LintExplainView> Explain(Args args, LintRuleSet set, string id)
    {
        LintRule rule = set.Find(id) ?? throw ApiErrors.Refused("rule_not_found", $"No rule {id} is in effect.");
        long? reference = ThingId.TryRead(args.Optional("reference_id"), out ThingId thingId) ? thingId.Value : (long?)null;
        long? other = ThingId.TryRead(args.Optional("other_id"), out ThingId otherId) ? otherId.Value : (long?)null;
        int? port = args.OptionalInt("port", 0, 64);
        Vec3? at = args.Has("at") ? Point(args) : (Vec3?)null;
        if (reference == null && at == null)
        {
            throw ApiErrors.InvalidArgument("explain needs reference_id (a thing, network or room id) or at (a cell, [x, y, z]).");
        }

        GameLintWorld world = GameLintWorld.Audit(Around(at ?? Where(reference!.Value)));
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
                    if (a.Key.CompareTo(b.Key) < 0 && Names(a, b, reference, other) && ++matched <= MaximumExplained)
                    {
                        LintSubjectRef pair = new LintSubjectRef(a, b);
                        explained.Add(new LintExplainView(rule.Id, pair.Describe, LintEngine.Judge(rule, pair, context, true, true)));
                    }
                }
            }
        }
        else
        {
            foreach (ILintObject subject in subjects)
            {
                if (Matches(subject, reference, port, at) && ++matched <= MaximumExplained)
                {
                    explained.Add(new LintExplainView(rule.Id, subject.Describe,
                        LintEngine.Judge(rule, new LintSubjectRef(subject), context, true, true)));
                }
            }
        }

        Pure.Shaping.Truncations.Note("explained", explained.Count, matched,
            "name one subject: other_id for a pair, port for one port of a device");

        if (explained.Count == 0)
        {
            throw ApiErrors.Refused("subject_not_found",
                $"Rule {id} selects {rule.Select.Set}; none of those around the place given is the subject named.");
        }

        return explained;
    }

    private static bool Names(ILintObject a, ILintObject b, long? reference, long? other) =>
        (a.ReferenceId == reference && (other == null || b.ReferenceId == other)) ||
        (b.ReferenceId == reference && (other == null || a.ReferenceId == other));

    private static bool Matches(ILintObject subject, long? reference, int? port, Vec3? at)
    {
        if (at is Vec3 point)
        {
            return subject.Position is Vec3 position && (position - point).Length < 0.3;
        }

        if (subject.ReferenceId != reference)
        {
            return false;
        }

        return port == null || !(subject is PortSubject) || subject.Key.EndsWith("/" + port.Value, StringComparison.Ordinal);
    }

    private static Vec3 Point(Args args)
    {
        Metres point = BuildArgs.PositionOf(args.Optional("at")!, "at");
        return new Vec3(point.X, point.Y, point.Z);
    }

    // Where a thing, a network (its first member) or a room (its first cell) is.
    private static Vec3 Where(long id)
    {
        if (GameLookup.TryFindThing(new ThingId(id), out Thing thing))
        {
            return Bodies.V(thing.Position);
        }

        Room? room = Shared.Game.Structures.StructureAirRecord.FindRoom(id);
        if (room != null)
        {
            foreach (WorldGrid grid in room.Grids)
            {
                return Bodies.V(grid.Value.ToVector3());
            }
        }

        foreach (Shared.Game.Upgrades.UpgradeFamily family in new Shared.Game.Upgrades.UpgradeFamily[]
                     { new CableFamily(), new PipeFamily(), new ChuteFamily() })
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

        throw ApiErrors.Refused("subject_not_found", $"No thing, room or network has id {id}.");
    }

    // The 2 m cell holding the point and the 26 around it.
    private static List<GridCell> Around(Vec3 point)
    {
        GridCell centre = SmallCellCode.LargeOf(new GridCell((int)Math.Round(point.X * 10.0), (int)Math.Round(point.Y * 10.0),
            (int)Math.Round(point.Z * 10.0)));
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
