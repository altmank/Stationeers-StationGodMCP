#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Profiling;

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
/// Runs rules over a world: each rule's select gives its subjects (report: only planned ones), its where filters
/// them, and every subject whose assert is false becomes a finding with the rule's id, level and message. A subject
/// the rule cannot evaluate becomes a rule_error finding naming the rule, the subject and the reason; nothing thrown
/// leaves the engine.
/// </summary>
internal static class LintEngine
{
    internal const string RuleErrorCode = "rule_error";
    internal const int RuleErrorsPerRule = 25;

    [Profiled]
    internal static LintRun Run(LintRuleSet set, ILintWorld world, string phase)
    {
        Answer<LintRun> answer = new Answer<LintRun>();
        foreach (object? _ in Steps(set, world, phase, answer))
        {
        }

        return answer.Value;
    }

    /// <summary>
    /// Run spread over frames (a Work body): each rule's subjects selected a batch a step (SelectSteps: its where and
    /// its pairs too, never one step for a whole set; review D, R-D-13), then one subject judged per step. The run's
    /// milliseconds count only its steps, not the frames between them.
    /// </summary>
    internal static IEnumerable<object?> Steps(LintRuleSet set, ILintWorld world, string phase, Answer<LintRun> answer)
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

            int count = 0;
            Selection selection = new Selection();
            foreach (object? step in SelectSteps(rule, world, context, phase == "report", findings, order, selection))
            {
                clock.Stop();
                yield return step;
                clock.Start();
            }

            int errors = selection.Errors;
            foreach (LintSubjectRef subject in selection.Subjects)
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

                clock.Stop();
                yield return null;
                clock.Start();
            }

            ruleErrors += errors;
            checkedCounts[rule.Id] = count;
        }

        answer.Value = new LintRun(findings, checkedCounts, ruleErrors, clock.Elapsed.TotalMilliseconds);
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
    // Subjects listed per step of SelectSteps, and pair candidates whose where is asked per step.
    private const int SubjectsAStep = 512;
    private const int WheresAStep = 16;

    /// <summary>A rule's selected subjects, and the rule errors met selecting them.</summary>
    internal sealed class Selection
    {
        internal List<LintSubjectRef> Subjects { get; } = new List<LintSubjectRef>();

        internal int Errors { get; set; }
    }

    /// <summary>
    /// A rule's subjects as a Work body, a batch a step: its set's members (report: only planned ones); for pairs, the
    /// members its where keeps (WheresAStep a step), then the pairs within its distance (LintPairs.WithinSteps).
    /// </summary>
    internal static IEnumerable<object?> SelectSteps(LintRule rule, ILintWorld world, LintContext context,
        bool plannedOnly, List<LintFinding> findings, int order, Selection selection)
    {
        IReadOnlyList<ILintObject> set = world.Subjects(rule.Select.Set);
        List<LintSubjectRef> subjects = selection.Subjects;
        if (!rule.Select.IsPairs)
        {
            for (int index = 0; index < set.Count; index++)
            {
                if (!plannedOnly || world.IsPlanned(set[index]))
                {
                    subjects.Add(new LintSubjectRef(set[index]));
                }

                if ((index + 1) % SubjectsAStep == 0)
                {
                    yield return null;
                }
            }

            yield break;
        }

        List<ILintObject> kept = new List<ILintObject>(set.Count);
        for (int index = 0; index < set.Count; index++)
        {
            ILintObject subject = set[index];
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
                if (selection.Errors++ < RuleErrorsPerRule)
                {
                    findings.Add(ErrorFinding(rule, order, new LintSubjectRef(subject),
                        error is LintEvaluationException ? error.Message : $"{error.GetType().Name}: {error.Message}"));
                }
            }

            if ((index + 1) % WheresAStep == 0)
            {
                yield return null;
            }
        }

        Answer<List<(int A, int B)>> pairs = new Answer<List<(int A, int B)>>();
        foreach (object? step in LintPairs.WithinSteps(kept, rule.Select.Within!.Value, pairs))
        {
            yield return step;
        }

        foreach ((int a, int b) in pairs.Value)
        {
            if (!plannedOnly || world.IsPlanned(kept[a]) || world.IsPlanned(kept[b]))
            {
                subjects.Add(new LintSubjectRef(kept[a], kept[b]));
            }
        }
    }
}

/// <summary>
/// Unordered pairs of objects whose boxes (a thing's mesh box, else its position) are at most a distance apart, each
/// pair once, found through a spatial hash rather than every pair.
/// </summary>
internal static class LintPairs
{
    // A box spanning more 2 m buckets than this is compared with every other box instead.
    private const long MaximumBuckets = 512;

    // Boxes read, boxes bucketed, and buckets paired per step of WithinSteps.
    private const int BoxesAStep = 64;
    private const int BucketedAStep = 256;
    private const int BucketsAStep = 64;

    internal static List<(int A, int B)> Within(IReadOnlyList<ILintObject> objects, double within)
    {
        Answer<List<(int A, int B)>> pairs = new Answer<List<(int A, int B)>>();
        foreach (object? _ in WithinSteps(objects, within, pairs))
        {
        }

        return pairs.Value;
    }

    /// <summary>Within spread over frames (a Work body): the boxes, the buckets and the pairs each a batch a step.</summary>
    internal static IEnumerable<object?> WithinSteps(IReadOnlyList<ILintObject> objects, double within,
        Answer<List<(int A, int B)>> answer)
    {
        List<Box3?> boxes = new List<Box3?>(objects.Count);
        for (int index = 0; index < objects.Count; index++)
        {
            boxes.Add(BoxOf(objects[index]));
            if ((index + 1) % BoxesAStep == 0)
            {
                yield return null;
            }
        }

        double size = Math.Max(2.0, within * 2);
        Dictionary<(long, long, long), List<int>> buckets = new Dictionary<(long, long, long), List<int>>();
        List<int> large = new List<int>();
        for (int index = 0; index < boxes.Count; index++)
        {
            if (!(boxes[index] is Box3 box))
            {
                continue;
            }

            long x0 = Cell(box.Min.X - within, size), x1 = Cell(box.Max.X + within, size);
            long y0 = Cell(box.Min.Y - within, size), y1 = Cell(box.Max.Y + within, size);
            long z0 = Cell(box.Min.Z - within, size), z1 = Cell(box.Max.Z + within, size);
            if ((index + 1) % BucketedAStep == 0)
            {
                yield return null;
            }

            if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > MaximumBuckets)
            {
                large.Add(index);
                continue;
            }

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
        List<(int A, int B)> pairs = new List<(int A, int B)>();
        int paired = 0;
        foreach (List<int> bucket in buckets.Values)
        {
            if (++paired % BucketsAStep == 0)
            {
                yield return null;
            }

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

        // A box too big to bucket is measured against every other, one a step.
        foreach (int big in large)
        {
            yield return null;
            for (int other = 0; other < boxes.Count; other++)
            {
                int a = Math.Min(big, other), b = Math.Max(big, other);
                if (a != b && boxes[other] is Box3 near && seen.Add((long)a * objects.Count + b) &&
                    Gap(boxes[big]!.Value, near) <= within + 1e-9)
                {
                    pairs.Add((a, b));
                }
            }
        }

        pairs.Sort(static (p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B.CompareTo(q.B));
        answer.Value = pairs;
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
            catch (Exception)
            {
                // A box the model cannot give falls back to the position below.
            }
        }

        return item.Position is Vec3 at ? new Box3(at, at) : (Box3?)null;
    }
}
