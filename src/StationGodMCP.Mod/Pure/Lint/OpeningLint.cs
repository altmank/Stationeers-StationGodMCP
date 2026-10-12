#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>What the write rules said about one write: whether it opens, and their problems and warnings.</summary>
internal sealed class OpeningVerdict
{
    /// <summary>The rule id of the warning a verdict whose rules could not run carries (lint_unavailable).</summary>
    internal const string UnavailableRule = "unavailable";

    /// <summary>A write that opens nothing: no rule ran.</summary>
    internal static readonly OpeningVerdict Closed = new OpeningVerdict(false, new List<LintFinding>(),
        new List<LintFinding>(), 0.0);

    internal OpeningVerdict(bool opens, List<LintFinding> problems, List<LintFinding> warnings, double milliseconds)
        : this(opens, problems, warnings, milliseconds, false)
    {
    }

    private OpeningVerdict(bool opens, List<LintFinding> problems, List<LintFinding> warnings, double milliseconds,
        bool unavailable)
    {
        Opens = opens;
        Problems = problems;
        Warnings = warnings;
        Milliseconds = milliseconds;
        IsUnavailable = unavailable;
    }

    internal bool Opens { get; }

    /// <summary>
    /// The write opens the device, but the rules could not run (a rule file or a game read threw): no problem is
    /// known, and the one warning, unavailable, says why. A checked write goes ahead with it; a held restore is skipped.
    /// </summary>
    internal bool IsUnavailable { get; }

    /// <summary>Findings at level problem, rules in rule-set order: each refuses the write unless allowed.</summary>
    internal List<LintFinding> Problems { get; }

    /// <summary>Findings at level warning (rule_error included): the reply's warnings.</summary>
    internal List<LintFinding> Warnings { get; }

    internal double Milliseconds { get; }

    /// <summary>An opening write to the device whose rules threw: the warning unavailable, with the error.</summary>
    internal static OpeningVerdict Unavailable(long referenceId, Exception error) =>
        new OpeningVerdict(true, new List<LintFinding>(), new List<LintFinding>
        {
            new LintFinding(UnavailableRule, ConflictLevel.Warning, 0,
                $"The write lint rules could not run on device {referenceId}: {error.GetType().Name}: {error.Message}",
                referenceId, Vec3.Zero, null, null, null)
        }, 0.0, true);
}

/// <summary>
/// The write rules: the rules with "write" in their on, run on every opening transition of a vent, valve, pump or
/// regulator (OpeningTransition) over the pipe networks it opens to as they hold now (OpeningWorld), through the same
/// engine and rule files as lint_layout. Nothing is kept between writes, so a network that gains oxidiser between
/// two openings is caught at the second. A write that opens nothing reads no rule file and builds no world.
/// Findings at level info are not reported.
/// </summary>
internal static class OpeningLint
{
    internal const string Phase = "write";

    internal static OpeningVerdict Judge(OpeningDevice device, OpeningLogic logic, double? previous, double requested,
        int modes, Func<LintRuleSet> rules, Func<ILintWorld> world)
    {
        if (!OpeningTransition.Opens(device, logic, previous, requested, modes))
        {
            return OpeningVerdict.Closed;
        }

        LintRun run = LintEngine.Run(rules(), world(), Phase);
        List<LintFinding> problems = new List<LintFinding>();
        List<LintFinding> warnings = new List<LintFinding>();
        foreach (LintFinding finding in LintReport.Ordered(run.Findings))
        {
            if (finding.Level == ConflictLevel.Problem)
            {
                problems.Add(finding);
            }
            else if (finding.Level == ConflictLevel.Warning)
            {
                warnings.Add(finding);
            }
        }

        return new OpeningVerdict(true, problems, warnings, run.Milliseconds);
    }
}
