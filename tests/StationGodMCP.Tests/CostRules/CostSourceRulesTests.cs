#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests.CostRules;

/// <summary>
/// The cost gate's source rules (cost design A7), read from the mod's syntax (ModSource; the game types do not
/// compile here):
///   1. the game's global collections (GameCollections.txt) are read only inside src/StationGodMCP.Mod/World/;
///   2. none of them is copied (ToList, ToArray, CopyTo, AddRange of one, a collection built from one), anywhere;
///   3. a loop over one of them, or over a World member, yields (a resumable step, A3), anywhere;
///   4. a reply view (a class or struct named *View, or any type under Api/Views/) holds no game type in a field or
///      property (GameTypes.txt: the decompile's types and the engine's), so the connection's writer thread can write
///      it off the game's thread (A5). Names the mod declares itself are its own types, not the game's.
/// Rule 3 sees only loops written over the collection itself; a loop over a copy, or in a helper over a list passed
/// in, is not seen. The measured gate (scoreboard drivers/gate.py) covers the rest: any call whose worst StationGod
/// frame passes the request budget on the large world fails it, whoever loops.
/// Today's code breaks these rules; each rule's baseline (CostRules/*.baseline.txt, path and count) holds today's
/// violations, the to-do list of the cost build's steps 2 to 6. A rule fails on a file over its baseline (a new
/// violation) and on a file under it (the baseline must shrink with the code).
/// STATIONGOD_WRITE_COST_BASELINE=1 rewrites the baselines.
/// </summary>
public sealed class CostSourceRulesTests
{
    private const string WorldFolder = "World/";

    private static readonly string[] CopyMethods = { "ToList", "ToArray", "CopyTo" };

    private static readonly string[] CollectionTypes =
        { "List", "HashSet", "Dictionary", "SortedDictionary", "Queue", "Stack", "LinkedList", "SortedSet" };

    private static readonly Lazy<List<string[]>> Collections = new Lazy<List<string[]>>(LoadCollections);

    private static readonly Lazy<HashSet<string>> GameTypes = new Lazy<HashSet<string>>(LoadGameTypes);

    private readonly ITestOutputHelper _output;

    public CostSourceRulesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GameCollectionsAreReadOnlyInsideTheWorldModule() =>
        Check("game-collections-outside-world", file => file.Path.StartsWith(WorldFolder, StringComparison.Ordinal)
            ? 0
            : Accesses(file.Root).Count(IsGameCollection));

    [Fact]
    public void NoGameCollectionIsCopied() =>
        Check("game-collection-copies", file => file.Root.DescendantNodes().Count(IsCopy));

    [Fact]
    public void ALoopOverAGameCollectionOrTheWorldYields() =>
        Check("unyielded-world-loops", file => file.Root.DescendantNodes().Count(IsUnyieldedLoop));

    [Fact]
    public void ReplyViewsHoldNoGameType() =>
        Check("view-game-types", file => ViewMembers(file).Count(HoldsGameType));

    [Fact]
    public void TheViewRuleSeesAGameTypeInAViewOnly()
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText("""
            class ThingNameView
            {
                public string Name { get; }
                public Thing Held { get; }
                private readonly List<Device> _devices;
                public Dictionary<string, Vector3> Places { get; }
                public static Thing Shared;
            }

            class Reader
            {
                public Thing Held { get; }
            }
            """).GetRoot();

        Assert.Equal(3, ViewMembers(new SourceFile("Api/Shared/X.cs", root)).Count(HoldsGameType));
    }

    [Fact]
    public void TheLinqBanIsAnError()
    {
        string project = File.ReadAllText(Path.Combine(CatalogueFiles.RepositoryRoot(), "StationGodMCP.csproj"));
        int start = project.IndexOf("<WarningsAsErrors>", StringComparison.Ordinal);
        int end = project.IndexOf("</WarningsAsErrors>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "StationGodMCP.csproj has no WarningsAsErrors");
        Assert.Contains("RS0030", project.Substring(start, end - start));
        Assert.Contains("T:System.Linq.Enumerable",
            File.ReadAllText(Path.Combine(CatalogueFiles.RepositoryRoot(), "BannedSymbols.txt")));
    }

    [Fact]
    public void TheRulesSeeWhatTheyAreMeantTo()
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    int n = Device.AllDevices.Count;
                    var copy = GridController.AllStructuresPool.ToList();
                    var also = new List<Thing>(OcclusionManager.AllThings);
                    foreach (Device d in Device.AllDevices) { }
                    for (int i = 0; i < Assets.Scripts.OcclusionManager.AllThings.Count; i++) { }
                    var rooms = RoomController.World?.Rooms;
                    var mine = Something.AllDevicesOfMine;
                }

                IEnumerable<int> Step()
                {
                    foreach (Device d in Device.AllDevices) { yield return 1; }
                    foreach (var t in World.Frames) { }
                }
            }
            """).GetRoot();

        Assert.Equal(7, Accesses(root).Count(IsGameCollection));
        Assert.Equal(2, root.DescendantNodes().Count(IsCopy));
        Assert.Equal(3, root.DescendantNodes().Count(IsUnyieldedLoop));
    }

    private void Check(string rule, Func<SourceFile, int> count)
    {
        SortedDictionary<string, int> found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (SourceFile file in ModSource.Instance.Files)
        {
            int violations = count(file);
            if (violations > 0)
            {
                found[file.Path] = violations;
            }
        }

        string path = BaselinePath(rule);
        _output.WriteLine($"{rule}: {found.Values.Sum()} in {found.Count} files");
        if (Environment.GetEnvironmentVariable("STATIONGOD_WRITE_COST_BASELINE") == "1")
        {
            StringBuilder text = new StringBuilder();
            text.Append("# ").Append(rule).Append(": today's violations per file (CostSourceRulesTests); ")
                .Append(found.Values.Sum()).Append(" in all\n");
            foreach (KeyValuePair<string, int> entry in found)
            {
                text.Append(entry.Key).Append('\t').Append(entry.Value).Append('\n');
            }

            File.WriteAllText(path, text.ToString());
            return;
        }

        Dictionary<string, int> baseline = File.Exists(path)
            ? File.ReadAllLines(path)
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => line.Split('\t'))
                .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]), StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> problems = new List<string>();
        foreach (string file in found.Keys.Union(baseline.Keys).OrderBy(file => file, StringComparer.Ordinal))
        {
            int now = found.GetValueOrDefault(file);
            int was = baseline.GetValueOrDefault(file);
            if (now > was)
            {
                problems.Add($"{file}: {now} (baseline {was}): new violation");
            }
            else if (now < was)
            {
                problems.Add($"{file}: {now} (baseline {was}): fixed, shrink the baseline " +
                             "(STATIONGOD_WRITE_COST_BASELINE=1)");
            }
        }

        Assert.True(problems.Count == 0, rule + ":\n" + string.Join("\n", problems));
    }

    // Member accesses, and conditional ones (RoomController.World?.Rooms), whose last name is a member binding.
    private static IEnumerable<ExpressionSyntax> Accesses(SyntaxNode node) =>
        node.DescendantNodesAndSelf().Where(child => child is MemberAccessExpressionSyntax ||
            child is ConditionalAccessExpressionSyntax { WhenNotNull: MemberBindingExpressionSyntax })
            .Cast<ExpressionSyntax>();

    private static bool IsGameCollection(ExpressionSyntax access)
    {
        string text = Normalized(access);
        foreach (string[] entry in Collections.Value)
        {
            if (EndsWith(text, entry))
            {
                return true;
            }
        }

        return false;
    }

    // Owner.Member at the end of a dotted path, after a dot or at its start: Assets.Scripts.OcclusionManager.AllThings
    // matches OcclusionManager.AllThings, Something.AllThingsOfMine does not.
    private static bool EndsWith(string text, string[] entry)
    {
        string tail = string.Join(".", entry);
        return text == tail || text.EndsWith("." + tail, StringComparison.Ordinal);
    }

    private static string Normalized(ExpressionSyntax expression) =>
        new string(expression.ToString().Where(c => !char.IsWhiteSpace(c) && c != '?' && c != '!').ToArray());

    private static bool ContainsGameCollection(SyntaxNode node) => Accesses(node).Any(IsGameCollection);

    private static bool IsCopy(SyntaxNode node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation:
                if (CopyMethods.Contains(member.Name.Identifier.Text) &&
                    member.Expression is MemberAccessExpressionSyntax target && IsGameCollection(target))
                {
                    return true;
                }

                return member.Name.Identifier.Text == "AddRange" &&
                       invocation.ArgumentList.Arguments.Any(argument =>
                           argument.Expression is MemberAccessExpressionSyntax copied && IsGameCollection(copied));
            case ObjectCreationExpressionSyntax creation:
                string type = creation.Type is GenericNameSyntax generic ? generic.Identifier.Text : creation.Type.ToString();
                return CollectionTypes.Contains(type) && creation.ArgumentList != null &&
                       creation.ArgumentList.Arguments.Any(argument =>
                           argument.Expression is MemberAccessExpressionSyntax copied && IsGameCollection(copied));
            default:
                return false;
        }
    }

    private static bool IsUnyieldedLoop(SyntaxNode node)
    {
        SyntaxNode? over = node switch
        {
            ForEachStatementSyntax loop => loop.Expression,
            ForStatementSyntax loop => loop.Condition,
            _ => null
        };
        if (over == null || !(ContainsGameCollection(over) || OverWorld(over)))
        {
            return false;
        }

        StatementSyntax body = node is ForEachStatementSyntax each ? each.Statement : ((ForStatementSyntax)node).Statement;
        return !body.DescendantNodesAndSelf().OfType<YieldStatementSyntax>().Any();
    }

    private static bool OverWorld(SyntaxNode node) =>
        node.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(access =>
            access.Expression is IdentifierNameSyntax { Identifier.Text: "World" } ||
            Normalized(access.Expression).EndsWith("StationGodMCP.WorldModel.World", StringComparison.Ordinal));

    // The instance fields and properties of the file's reply views: types named *View, and every type under Api/Views/.
    private static IEnumerable<TypeSyntax> ViewMembers(SourceFile file)
    {
        bool viewsFolder = file.Path.StartsWith("Api/Views/", StringComparison.Ordinal);
        foreach (TypeDeclarationSyntax type in file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!(type is ClassDeclarationSyntax || type is StructDeclarationSyntax) ||
                !(viewsFolder || type.Identifier.Text.EndsWith("View", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (MemberDeclarationSyntax member in type.Members)
            {
                if (member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword) ||
                                                     modifier.IsKind(SyntaxKind.ConstKeyword)))
                {
                    continue;
                }

                switch (member)
                {
                    case PropertyDeclarationSyntax property:
                        yield return property.Type;
                        break;
                    case FieldDeclarationSyntax field:
                        yield return field.Declaration.Type;
                        break;
                }
            }
        }
    }

    // A member type naming a game type anywhere in it (List<Device>, Thing?, Dictionary<string, Vector3>).
    private static bool HoldsGameType(TypeSyntax type) =>
        type.DescendantNodesAndSelf().Any(node => node switch
        {
            IdentifierNameSyntax name => GameTypes.Value.Contains(name.Identifier.Text),
            GenericNameSyntax generic => GameTypes.Value.Contains(generic.Identifier.Text),
            _ => false
        });

    // The game's type names, but those the mod declares itself at the top of a namespace (a nested type of the same
    // name, Selector.Thing, does not hide the game's elsewhere).
    private static HashSet<string> LoadGameTypes()
    {
        HashSet<string> names = new HashSet<string>(File.ReadAllLines(Path.Combine(Here(), "GameTypes.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#')), StringComparer.Ordinal);
        foreach (SourceFile file in ModSource.Instance.Files)
        {
            foreach (BaseTypeDeclarationSyntax type in file.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (type.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                {
                    names.Remove(type.Identifier.Text);
                }
            }
        }

        foreach (string generic in CollectionTypes)
        {
            names.Remove(generic);
        }

        return names;
    }

    private static List<string[]> LoadCollections() =>
        File.ReadAllLines(Path.Combine(Here(), "GameCollections.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t')[0].Split('.'))
            .ToList();

    private static string BaselinePath(string rule) => Path.Combine(Here(), rule + ".baseline.txt");

    private static string Here([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
}
