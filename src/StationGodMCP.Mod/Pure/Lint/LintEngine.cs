#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

internal enum LintOutcome
{
    /// <summary>The select's where left the subject out.</summary>
    Skipped,
    Passed,
    Failed,

    /// <summary>The rule could not be evaluated on the subject: a rule_error.</summary>
    Error
}

/// <summary>What one rule said about one subject, and for explain the values it read.</summary>
internal sealed class LintVerdict
{
    internal LintVerdict(LintOutcome outcome, ConflictLevel level, string? message, long? otherId, string? error,
        LintTrace? trace)
    {
        Outcome = outcome;
        Level = level;
        Message = message;
        OtherId = otherId;
        Error = error;
        Trace = trace;
    }

    internal LintOutcome Outcome { get; }

    internal ConflictLevel Level { get; }

    internal string? Message { get; }

    internal long? OtherId { get; }

    internal string? Error { get; }

    internal LintTrace? Trace { get; }
}

/// <summary>A lint run's findings and what it looked at.</summary>
internal sealed class LintRun
{
    internal LintRun(List<LintFinding> findings, Dictionary<string, int> subjects, int ruleErrors, double milliseconds)
    {
        Findings = findings;
        Subjects = subjects;
        RuleErrors = ruleErrors;
        Milliseconds = milliseconds;
    }

    internal List<LintFinding> Findings { get; }

    /// <summary>Subjects checked per rule id.</summary>
    internal Dictionary<string, int> Subjects { get; }

    internal int RuleErrors { get; }

    internal double Milliseconds { get; }
}

/// <summary>
/// Runs rules over a world: each rule's select gives its subjects (dry_run: only planned ones), its where filters
/// them, and every subject whose assert is false becomes a finding with the rule's id, level and message. A subject
/// the rule cannot evaluate becomes a rule_error finding naming the rule, the subject and the reason; nothing thrown
/// leaves the engine.
/// </summary>
internal static class LintEngine
{
    internal const string RuleErrorCode = "rule_error";
    internal const int RuleErrorsPerRule = 25;

    internal static LintRun Run(LintRuleSet set, ILintWorld world, string phase)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        LintContext context = new LintContext(world);
        List<LintFinding> findings = new List<LintFinding>();
        Dictionary<string, int> checkedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int ruleErrors = 0;
        for (int order = 0; order < set.Rules.Count; order++)
        {
            LintRule rule = set.Rules[order];
            if (!rule.RunsOn(phase))
            {
                continue;
            }

            int errors = 0, count = 0;
            foreach (LintSubjectRef subject in SubjectsOf(rule, world, context, phase == "dry_run", ref errors, findings, order))
            {
                count++;
                LintVerdict verdict = Judge(rule, subject, context, false, true);
                if (verdict.Outcome == LintOutcome.Failed)
                {
                    findings.Add(FindingOf(rule, order, subject, verdict));
                }
                else if (verdict.Outcome == LintOutcome.Error && errors++ < RuleErrorsPerRule)
                {
                    findings.Add(ErrorFinding(rule, order, subject, verdict.Error!));
                }
            }

            ruleErrors += errors;
            checkedCounts[rule.Id] = count;
        }

        return new LintRun(findings, checkedCounts, ruleErrors, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>One rule on one subject. where: whether the select's where is asked first (explain asks it too).</summary>
    internal static LintVerdict Judge(LintRule rule, LintSubjectRef subject, LintContext context, bool trace,
        bool where)
    {
        LintTrace? record = trace ? new LintTrace() : null;
        try
        {
            LintFrame frame = rule.Unit.Frame(context, subject, record);
            if (where && rule.Where != null && !Passes(rule, subject, frame, context, record))
            {
                return new LintVerdict(LintOutcome.Skipped, rule.Level, null, null, null, record);
            }

            if (rule.Assert(frame).AsBool)
            {
                return new LintVerdict(LintOutcome.Passed, rule.Level, null, null, null, record);
            }

            ConflictLevel level = rule.Level;
            if (rule.LevelWhen != null && !LintRule.TryLevel(rule.LevelWhen(frame).AsString, out level))
            {
                throw new LintEvaluationException("level_when gave no level (problem, warning or info)");
            }

            long? other = OtherOf(rule, subject, frame);
            return new LintVerdict(LintOutcome.Failed, level, rule.Message.Render(frame), other, null, record);
        }
        catch (LintEvaluationException error)
        {
            return new LintVerdict(LintOutcome.Error, rule.Level, null, null, error.Message, record);
        }
        catch (Exception error)
        {
            return new LintVerdict(LintOutcome.Error, rule.Level, null, null, $"{error.GetType().Name}: {error.Message}",
                record);
        }
    }

    private static bool Passes(LintRule rule, LintSubjectRef subject, LintFrame frame, LintContext context,
        LintTrace? trace)
    {
        if (rule.WhereUnit == null)
        {
            return rule.Where!(frame).AsBool;
        }

        return rule.Where!(rule.WhereUnit.Frame(context, new LintSubjectRef(subject.First), trace)).AsBool &&
               (subject.Second == null ||
                rule.Where(rule.WhereUnit.Frame(context, new LintSubjectRef(subject.Second), trace)).AsBool);
    }

    private static long? OtherOf(LintRule rule, LintSubjectRef subject, LintFrame frame)
    {
        if (rule.Other != null)
        {
            LintValue other = rule.Other(frame);
            return other.Kind == LintKind.Object ? other.AsObject.ReferenceId
                : other.Kind == LintKind.Number ? (long)other.AsNumber
                : (long?)null;
        }

        return subject.Second?.ReferenceId;
    }

    private static LintFinding FindingOf(LintRule rule, int order, LintSubjectRef subject, LintVerdict verdict) =>
        new LintFinding(rule.Id, verdict.Level, order, verdict.Message!, subject.First.ReferenceId,
            subject.First.Position ?? Vec3.Zero, verdict.OtherId, subject.Key, subject.Describe);

    private static LintFinding ErrorFinding(LintRule rule, int order, LintSubjectRef subject, string reason) =>
        new LintFinding(RuleErrorCode, ConflictLevel.Warning, order,
            $"Rule {rule.Id} could not be evaluated on {subject.Describe}: {reason}.", subject.First.ReferenceId,
            subject.First.Position ?? Vec3.Zero, subject.Second?.ReferenceId, subject.Key, subject.Describe, rule.Id);

    /// <summary>The subjects a rule asks about: its set (planned ones only for a dry run), or its pairs.</summary>
    internal static IEnumerable<LintSubjectRef> SubjectsOf(LintRule rule, ILintWorld world, LintContext context,
        bool plannedOnly, ref int errors, List<LintFinding> findings, int order)
    {
        IReadOnlyList<ILintObject> set = world.Subjects(rule.Select.Set);
        List<LintSubjectRef> subjects = new List<LintSubjectRef>(set.Count);
        if (!rule.Select.IsPairs)
        {
            foreach (ILintObject subject in set)
            {
                if (!plannedOnly || world.IsPlanned(subject))
                {
                    subjects.Add(new LintSubjectRef(subject));
                }
            }

            return subjects;
        }

        List<ILintObject> kept = new List<ILintObject>(set.Count);
        foreach (ILintObject subject in set)
        {
            if (rule.Where == null)
            {
                kept.Add(subject);
                continue;
            }

            try
            {
                if (rule.Where(rule.WhereUnit!.Frame(context, new LintSubjectRef(subject))).AsBool)
                {
                    kept.Add(subject);
                }
            }
            catch (Exception error)
            {
                if (errors++ < RuleErrorsPerRule)
                {
                    findings.Add(ErrorFinding(rule, order, new LintSubjectRef(subject),
                        error is LintEvaluationException ? error.Message : $"{error.GetType().Name}: {error.Message}"));
                }
            }
        }

        foreach ((int a, int b) in LintPairs.Within(kept, rule.Select.Within!.Value))
        {
            if (!plannedOnly || world.IsPlanned(kept[a]) || world.IsPlanned(kept[b]))
            {
                subjects.Add(new LintSubjectRef(kept[a], kept[b]));
            }
        }

        return subjects;
    }
}

/// <summary>
/// Unordered pairs of objects whose boxes (a thing's mesh box, else its position) are at most a distance apart, each
/// pair once, found through a spatial hash rather than every pair.
/// </summary>
internal static class LintPairs
{
    internal static List<(int A, int B)> Within(IReadOnlyList<ILintObject> objects, double within)
    {
        List<Box3?> boxes = new List<Box3?>(objects.Count);
        foreach (ILintObject item in objects)
        {
            boxes.Add(BoxOf(item));
        }

        double size = Math.Max(2.0, within * 2);
        Dictionary<(long, long, long), List<int>> buckets = new Dictionary<(long, long, long), List<int>>();
        for (int index = 0; index < boxes.Count; index++)
        {
            if (!(boxes[index] is Box3 box))
            {
                continue;
            }

            long x0 = Cell(box.Min.X - within, size), x1 = Cell(box.Max.X + within, size);
            long y0 = Cell(box.Min.Y - within, size), y1 = Cell(box.Max.Y + within, size);
            long z0 = Cell(box.Min.Z - within, size), z1 = Cell(box.Max.Z + within, size);
            for (long x = x0; x <= x1; x++)
            {
                for (long y = y0; y <= y1; y++)
                {
                    for (long z = z0; z <= z1; z++)
                    {
                        if (!buckets.TryGetValue((x, y, z), out List<int> bucket))
                        {
                            bucket = new List<int>(4);
                            buckets[(x, y, z)] = bucket;
                        }

                        bucket.Add(index);
                    }
                }
            }
        }

        HashSet<long> seen = new HashSet<long>();
        List<(int, int)> pairs = new List<(int, int)>();
        foreach (List<int> bucket in buckets.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                for (int j = i + 1; j < bucket.Count; j++)
                {
                    int a = Math.Min(bucket[i], bucket[j]), b = Math.Max(bucket[i], bucket[j]);
                    if (a != b && seen.Add((long)a * objects.Count + b) && Gap(boxes[a]!.Value, boxes[b]!.Value) <= within + 1e-9)
                    {
                        pairs.Add((a, b));
                    }
                }
            }
        }

        pairs.Sort(static (p, q) => p.Item1 != q.Item1 ? p.Item1.CompareTo(q.Item1) : p.Item2.CompareTo(q.Item2));
        return pairs;
    }

    private static long Cell(double value, double size) => (long)Math.Floor(value / size);

    // The greatest per-axis separation of two boxes: 0 or less when they touch or overlap on every axis.
    private static double Gap(Box3 a, Box3 b)
    {
        double gap = double.NegativeInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            gap = Math.Max(gap, Math.Max(a.Min[axis] - b.Max[axis], b.Min[axis] - a.Max[axis]));
        }

        return gap;
    }

    private static Box3? BoxOf(ILintObject item)
    {
        if (ReferenceEquals(item.Type, LintModel.Thing))
        {
            try
            {
                if (LintStandardFunctions.BoxOf(item) is Box3 box)
                {
                    return box;
                }
            }
            catch (LintEvaluationException)
            {
            }
        }

        return item.Position is Vec3 at ? new Box3(at, at) : (Box3?)null;
    }
}
