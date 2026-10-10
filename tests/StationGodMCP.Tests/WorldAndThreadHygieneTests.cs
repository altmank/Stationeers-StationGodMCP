#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// What a world change forgets and how the stores the game's hooks write are guarded. WorldStores, Prints and
/// JobReplication use game types the tests cannot compile, so their parts are read from the source.
/// </summary>
[Collection(ParsedChipProgramsCollection.Name)]
public sealed class WorldAndThreadHygieneTests
{
    [Fact]
    public void ClearingForgetsEveryParsedChipProgram()
    {
        LintRuleSet set = LintTestKit.Rules(LintTestKit.File1(
            "{\"id\": \"batches\", \"level\": \"info\", \"select\": \"networks\", " +
            "\"assert\": \"count(chip_batch_names(x)) == 1\", \"message\": \"m\"}"));
        LintRule rule = Assert.Single(set.Rules);
        LintGraph g = new LintGraph();
        LintRecord housing = g.Make(LintModel.Thing, "thing:1", ("prefab", "StructureCircuitHousing"));
        LintRecord chip = g.Make(LintModel.Chip, "chip:1", ("housing", housing), ("language", "ic10"),
            ("source", "lb r0 HASH(\"StructureBattery\") Ratio Average"), ("pins", new List<ILintObject>()));
        LintRecord network = g.Make(LintModel.Network, "network:1", ("id", 1), ("kind", "cable"),
            ("chips", new List<ILintObject> { chip }), ("devices", new List<ILintObject> { housing }));

        LintOutcome outcome = LintEngine.Judge(rule, new LintSubjectRef(network, null),
            new LintContext(new TestLintWorld()), false, true).Outcome;
        Assert.Equal(LintOutcome.Passed, outcome);
        Assert.True(LintChipPrograms.ParsedCount > 0);

        LintChipPrograms.Clear();

        Assert.Equal(0, LintChipPrograms.ParsedCount);
    }

    [Fact]
    public void ClearingAPrintLogStartsAStoppedOneAgain()
    {
        PrintLog log = new PrintLog();
        log.Record(new PrintRecord(42, "ItemIronSheets", 7, "StructureAutolathe", "Autolathe", 10.0, 50, null));
        log.Stop();
        Assert.True(log.Stopped);

        log.Clear();

        Assert.False(log.Stopped);
        Assert.Null(log.Of(42));
    }

    [Fact]
    public void LeavingAWorldForgetsTheParsedChipPrograms()
    {
        Assert.Contains(RegisteredClears(), clear => clear.EndsWith("LintChipPrograms.Clear", System.StringComparison.Ordinal));
    }

    [Fact]
    public void LeavingAWorldTurnsPrintProvenanceBackOn()
    {
        Assert.Contains("Prints.Log.Clear", RegisteredClears());
        ClassDeclarationSyntax prints = Class("Api/Shared/Game/Prints.cs", "Prints");
        List<string> ownState = prints.Members.OfType<FieldDeclarationSyntax>()
            .Where(field => field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword))
            .SelectMany(field => field.Declaration.Variables.Select(variable => variable.Identifier.Text))
            .ToList();

        Assert.Empty(ownState);
    }

    [Fact]
    public void TheStatePacketCountIsReadAndWrittenAtomically()
    {
        ClassDeclarationSyntax replication = Class("Api/Shared/Game/JobReplication.cs", "JobReplication");
        PropertyDeclarationSyntax count = replication.Members.OfType<PropertyDeclarationSyntax>()
            .Single(property => property.Identifier.Text == "StateWrites");
        Assert.True(count.AccessorList == null || count.AccessorList.Accessors.All(accessor => !accessor.IsKind(SyntaxKind.SetAccessorDeclaration)),
            "StateWrites has a setter: its writes are not all Interlocked.");

        List<string> fields = replication.Members.OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Type.ToString() == "long")
            .SelectMany(field => field.Declaration.Variables.Select(variable => variable.Identifier.Text))
            .ToList();
        string counter = Assert.Single(fields);
        List<IdentifierNameSyntax> uses = replication.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => name.Identifier.Text == counter)
            .ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, use => Assert.True(InInterlockedCall(use), $"{use.Parent} touches {counter} without Interlocked."));
        Assert.Contains(uses, use => CalledMethod(use) == "Interlocked.Increment");
        Assert.Contains(uses, use => CalledMethod(use) == "Interlocked.Read");
    }

    private static List<string> RegisteredClears() =>
        ModSource.Instance.File("Api/Shared/Game/WorldStores.cs").Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Register" } &&
                           call.ArgumentList.Arguments.Count == 2)
            .Select(call => call.ArgumentList.Arguments[1].Expression.ToString())
            .ToList();

    private static ClassDeclarationSyntax Class(string path, string name) =>
        ModSource.Instance.File(path).Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == name);

    private static bool InInterlockedCall(SyntaxNode use) => CalledMethod(use)?.StartsWith("Interlocked.") == true;

    private static string? CalledMethod(SyntaxNode use) =>
        use.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } ? call.Expression.ToString() : null;
}

/// <summary>Tests that count the process-wide parsed chip programs run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParsedChipProgramsCollection
{
    internal const string Name = "parsed chip programs";
}
