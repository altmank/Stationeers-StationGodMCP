#nullable enable

using System.Collections.Generic;
using System.Text;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// What the write rules' verdict (OpeningLint) does to a logic write. A problem refuses it with lint_refused, whose
/// data lists every rule that found one; allow_lint lets it through, each problem then a warning. A warning is a
/// warning, its code lint_&lt;rule id&gt;. The refusal names the rules and the save's rule file; it does not offer
/// allow_lint, which lowers the guard.
/// </summary>
internal static class WriteLint
{
    internal const string RefusedCode = "lint_refused";

    internal const string AllowArgument = "allow_lint";

    internal const string CodePrefix = "lint_";

    /// <summary>
    /// A write checked against the write rules: its value there before, whether it opens the device, and its warnings;
    /// throws lint_refused for a problem not allowed.
    /// </summary>
    internal static LogicWriteChecked Checked(LogicRead requested, double? previous, OpeningVerdict verdict, bool allow,
        int? index) =>
        new LogicWriteChecked(requested, previous, verdict.Opens,
            Warnings(verdict, allow, requested.ReferenceId, index));

    /// <summary>The write's warnings from the verdict; throws lint_refused for a problem not allowed.</summary>
    internal static List<BuildIssueView> Warnings(OpeningVerdict verdict, bool allow, ThingId device, int? index)
    {
        List<BuildIssueView> warnings = new List<BuildIssueView>(verdict.Problems.Count + verdict.Warnings.Count);
        if (verdict.Problems.Count > 0 && !allow)
        {
            throw Refusal(verdict, device);
        }

        foreach (LintFinding problem in verdict.Problems)
        {
            warnings.Add(new BuildIssueView(CodePrefix + problem.Code,
                $"{problem.Message} Lint rule {problem.Code} (level problem) let through by " +
                $"{Vocabulary.Arg("allow_lint")}.", index, Thing(problem)));
        }

        foreach (LintFinding warning in verdict.Warnings)
        {
            warnings.Add(new BuildIssueView(CodePrefix + warning.Code, warning.Message, index, Thing(warning)));
        }

        return warnings;
    }

    private static ApiException Refusal(OpeningVerdict verdict, ThingId device)
    {
        List<LintRefusedRuleView> rules = new List<LintRefusedRuleView>(verdict.Problems.Count);
        StringBuilder said = new StringBuilder();
        foreach (LintFinding problem in verdict.Problems)
        {
            rules.Add(new LintRefusedRuleView(problem.Code, problem.Message, Thing(problem)));
            said.Append(' ').Append(problem.Message);
        }

        return new ApiException(RefusedCode,
            $"The write opens device {device}, and the write lint rules find {Count(rules.Count)} on the networks " +
            $"it opens to:{said} Nothing is written. A rule's level is set in the save's lint-rules.json " +
            $"({Vocabulary.Tool("lint_rules")} lists the files).",
            new LintRefusedView(rules));
    }

    private static string Count(int problems) => problems == 1 ? "a problem" : $"{problems} problems";

    private static ThingId? Thing(LintFinding finding) =>
        finding.ThingId is long id ? new ThingId(id) : (ThingId?)null;
}
