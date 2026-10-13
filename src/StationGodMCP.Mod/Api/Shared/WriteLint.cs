#nullable enable

using System.Collections.Generic;
using System.Text;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// What the write rules' verdict (OpeningLint) does to a logic write. A problem refuses it with lint_refused, whose
/// data lists every rule that found one; allow: ["lint"] lets it through, each problem then a warning and the write
/// marked as passing the lint guard. A warning is a warning, its code lint_&lt;rule id&gt;. The refusal names the
/// rules and the save's rule file; it does not offer allow, which lowers the guard.
/// </summary>
internal static class WriteLint
{
    internal const string RefusedCode = "lint_refused";

    /// <summary>The allow word that lets a lint problem through, and the guard a write that used it passed.</summary>
    internal const string Guard = "lint";

    internal const string CodePrefix = "lint_";

    /// <summary>
    /// A write checked against the write rules: its value there before, whether it opens the device, its warnings, and
    /// the controllers that write the same logic type on the device; throws lint_refused for a problem not allowed.
    /// </summary>
    internal static LogicWriteChecked Checked(LogicRead requested, double? previous, OpeningVerdict verdict, bool allow,
        int? index, List<ControllerView>? drivenBy) =>
        new LogicWriteChecked(requested, previous, verdict.Opens,
            Warnings(verdict, allow, requested.ReferenceId, index), drivenBy, verdict.Problems.Count > 0);

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
                $"{problem.Message} Lint rule {problem.Code} (level problem) let through by allow: [\"lint\"].",
                index, Thing(problem)));
        }

        foreach (LintFinding warning in verdict.Warnings)
        {
            warnings.Add(new BuildIssueView(CodePrefix + warning.Code, warning.Message, index, Thing(warning)));
        }

        return warnings;
    }

    /// <summary>Each finding as lint_refused's data lists it: the rule, its message and the thing it is about.</summary>
    internal static List<LintRefusedRuleView> Rules(List<LintFinding> findings)
    {
        List<LintRefusedRuleView> rules = new List<LintRefusedRuleView>(findings.Count);
        foreach (LintFinding finding in findings)
        {
            rules.Add(new LintRefusedRuleView(finding.Code, finding.Message, Thing(finding)));
        }

        return rules;
    }

    /// <summary>Each finding's warning code, lint_&lt;rule id&gt;, as a checked write's warnings carry it.</summary>
    internal static List<string> Codes(List<LintFinding> findings)
    {
        List<string> codes = new List<string>(findings.Count);
        foreach (LintFinding finding in findings)
        {
            codes.Add(CodePrefix + finding.Code);
        }

        return codes;
    }

    private static ApiException Refusal(OpeningVerdict verdict, ThingId device)
    {
        List<LintRefusedRuleView> rules = Rules(verdict.Problems);
        StringBuilder said = new StringBuilder();
        foreach (LintFinding problem in verdict.Problems)
        {
            said.Append(' ').Append(problem.Message);
        }

        return new ApiException(RefusedCode,
            $"The write opens device {device}, and the write lint rules find {Count(rules.Count)} on the networks " +
            $"it opens to:{said} Nothing is written. A rule's level is set in the save's lint-rules.json " +
            "(check {what: \"rules\"} lists the files).",
            new LintRefusedView(rules));
    }

    private static string Count(int problems) => problems == 1 ? "a problem" : $"{problems} problems";

    private static ThingId? Thing(LintFinding finding) =>
        finding.ThingId is long id ? new ThingId(id) : (ThingId?)null;
}
