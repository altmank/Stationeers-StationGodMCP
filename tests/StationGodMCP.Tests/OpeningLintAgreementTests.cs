#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// check layout's audit and the write rules judge the same device the same way: the write world (OpeningWorld) is
/// built from the subjects the audit sees, and every rule on "write" finds on an opening what the audit finds there.
/// The rule set decides which rules run on a write, so the mod's rules are the copy built into its DLL, never a
/// lint-rules.json beside it that another version left.
/// </summary>
public sealed class OpeningLintAgreementTests
{
    private const string OxidiserRule = "no_oxidiser_vented_outdoors";

    // An outward active vent with power, as the game describes one: a cable network and a pipe network.
    private sealed class VentSite
    {
        internal VentSite(bool outdoors, Dictionary<string, double> gases)
        {
            Dictionary<string, LintValue> pipe = new Dictionary<string, LintValue>(StringComparer.Ordinal)
            {
                ["id"] = LintValue.Of(325097), ["kind"] = LintValue.Of("pipe"), ["gases"] = Gases(gases),
                ["temperature"] = LintValue.Of(293.15)
            };
            Dictionary<string, LintValue> cable = new Dictionary<string, LintValue>(StringComparer.Ordinal)
            {
                ["id"] = LintValue.Of(325090), ["kind"] = LintValue.Of("cable")
            };
            Pipe = new LintRecord(LintModel.Network, "network:325097", pipe);
            Cable = new LintRecord(LintModel.Network, "network:325090", cable);
            Vent = new LintRecord(LintModel.Thing, "thing:213177", new Dictionary<string, LintValue>
            {
                ["reference_id"] = LintValue.Of(213177), ["prefab"] = LintValue.Of("StructureActiveVent"),
                ["display_name"] = LintValue.Of("Furnace Waste Vent"), ["kind"] = LintValue.Of("device"),
                ["runtime_types"] = LintValue.Strings(new List<string>
                    { "ActiveVent", "DeviceAtmospherics", "Device", "SmallGrid", "Structure", "Thing" }),
                ["outdoors"] = LintValue.Of(outdoors),
                ["networks"] = LintValue.Objects(new List<ILintObject> { Cable, Pipe })
            });
            pipe["devices"] = LintValue.Objects(new List<ILintObject> { Vent });
            pipe["members"] = LintValue.EmptyList;
            cable["devices"] = LintValue.Objects(new List<ILintObject> { Vent });
        }

        internal LintRecord Vent { get; }

        internal LintRecord Pipe { get; }

        internal LintRecord Cable { get; }

        // The audit's world over a box around the vent: the vent and the networks it reaches.
        internal TestLintWorld Audit() =>
            new TestLintWorld().Add("devices", Vent).Add("networks", Cable).Add("networks", Pipe);

        private static LintValue Gases(Dictionary<string, double> gases)
        {
            Dictionary<string, LintValue> map = new Dictionary<string, LintValue>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> gas in gases)
            {
                map[gas.Key] = LintValue.Of(gas.Value);
            }

            return LintValue.Of(map);
        }
    }

    [Theory]
    [InlineData(true, "Oxygen", 5.0)]
    [InlineData(true, "NitrousOxide", 0.2)]
    [InlineData(true, "Nitrogen", 5.0)]
    [InlineData(false, "Oxygen", 5.0)]
    public void TheAuditAndTheWriteJudgeFindTheSameOnTheSameWorld(bool outdoors, string gas, double mol)
    {
        LintRuleSet rules = LintTestKit.World();
        VentSite site = new VentSite(outdoors, new Dictionary<string, double> { [gas] = mol });
        TestLintWorld audit = site.Audit();

        List<string> audited = new List<string>();
        foreach (LintFinding finding in LintEngine.Run(rules, audit, "audit").Findings)
        {
            LintRule? rule = rules.Find(finding.Code);
            if (rule != null && rule.RunsOn(OpeningLint.Phase))
            {
                audited.Add($"{finding.Code} {finding.Level}: {finding.Message}");
            }
        }

        OpeningVerdict verdict = OpeningLint.Judge(OpeningDevice.Vent, OpeningLogic.On, 0.0, 1.0, 2, () => rules,
            () => new OpeningWorld(audit, site.Vent, OpeningDevice.Vent));
        List<string> judged = new List<string>();
        foreach (LintFinding finding in verdict.Problems)
        {
            judged.Add($"{finding.Code} {finding.Level}: {finding.Message}");
        }

        foreach (LintFinding finding in verdict.Warnings)
        {
            judged.Add($"{finding.Code} {finding.Level}: {finding.Message}");
        }

        Assert.True(verdict.Opens);
        Assert.Equal(audited, judged);
        Assert.Equal(outdoors && gas != "Nitrogen", judged.Exists(found => found.StartsWith(OxidiserRule + " Problem")));
    }

    [Fact]
    public void TheWriteWorldHoldsTheVentAndOnlyItsPipeNetwork()
    {
        VentSite site = new VentSite(true, new Dictionary<string, double> { ["Oxygen"] = 5.0 });

        OpeningWorld world = new OpeningWorld(site.Audit(), site.Vent, OpeningDevice.Vent);

        Assert.Equal(new[] { site.Vent.Key }, Keys(world.Subjects("things")));
        Assert.Equal(new[] { site.Pipe.Key }, Keys(world.Subjects("networks")));
        Assert.Empty(world.Subjects("ports"));
    }

    [Fact]
    public void ARuleFileWhoseOxidiserRuleRunsOnAuditOnlyLeavesTheOpeningUnchecked()
    {
        string stale = LintTestKit.WorldText()
            .Replace("\"on\": [\"audit\", \"write\"]", "\"on\": [\"audit\"]");
        LintRuleSet rules = LintTestKit.Rules(stale);
        VentSite site = new VentSite(true, new Dictionary<string, double> { ["Oxygen"] = 5.0 });

        bool audited = LintEngine.Run(rules, site.Audit(), "audit").Findings
            .Exists(finding => finding.Code == OxidiserRule);
        OpeningVerdict verdict = OpeningLint.Judge(OpeningDevice.Vent, OpeningLogic.On, 0.0, 1.0, 2, () => rules,
            () => new OpeningWorld(site.Audit(), site.Vent, OpeningDevice.Vent));

        Assert.True(audited);
        Assert.True(verdict.Opens);
        Assert.Empty(verdict.Problems);
        Assert.Contains(OpeningLint.Phase, LintTestKit.World().Find(OxidiserRule)!.On);
    }

    [Fact]
    public void TheModLoadsItsRulesFromTheCopyBuiltIntoItsDll()
    {
        XDocument project = XDocument.Load(Path.Combine(Root(), "StationGodMCP.csproj"));
        string? embedded = null;
        foreach (XElement resource in project.Descendants("EmbeddedResource"))
        {
            if ((string?)resource.Attribute("Include") == "lint-rules.json")
            {
                embedded = (string?)resource.Attribute("LogicalName");
            }
        }

        ClassDeclarationSyntax files = RuleFilesClass();
        string? resourceName = null;
        List<string> members = new List<string>();
        foreach (MemberDeclarationSyntax member in files.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    members.Add(variable.Identifier.ValueText);
                    if (variable.Identifier.ValueText == "Resource")
                    {
                        resourceName = ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText;
                    }
                }
            }
            else if (member is PropertyDeclarationSyntax property)
            {
                members.Add(property.Identifier.ValueText);
            }
        }

        Assert.NotNull(embedded);
        Assert.Equal(embedded, resourceName);
        Assert.DoesNotContain("ModFile", members);
        Assert.DoesNotContain("Assembly.Location", files.ToFullString());
    }

    private static ClassDeclarationSyntax RuleFilesClass()
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Root(), "src", "StationGodMCP.Mod",
            "Api", "Shared", "Game", "Lint", "LintRuleFiles.cs"))).GetRoot();
        foreach (ClassDeclarationSyntax type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (type.Identifier.ValueText == "LintRuleFiles")
            {
                return type;
            }
        }

        throw new InvalidOperationException("LintRuleFiles not found");
    }

    private static List<string> Keys(IReadOnlyList<ILintObject> subjects)
    {
        List<string> keys = new List<string>(subjects.Count);
        foreach (ILintObject subject in subjects)
        {
            keys.Add(subject.Key);
        }

        return keys;
    }

    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
