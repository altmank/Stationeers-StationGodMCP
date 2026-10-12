#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.VocabularyChecks;

/// <summary>
/// Every message in the mod that names an argument or a tool through the vocabulary prints, without a rename map,
/// exactly the text it printed before it was routed. The snapshot holds those old texts, per file, taken from the
/// source before the routing (WriteTheSnapshotFromTheOldSource); a routed message whose map-less text is not in it, or
/// a snapshot text no routed message prints, fails.
/// </summary>
public sealed class RoutedMessageTests
{
    private const string SnapshotFile = "tests/StationGodMCP.Tests/Fixtures/Vocabulary/routed-messages.json";

    /// <summary>A git revision before the routing: set it to write the snapshot from that revision's source.</summary>
    private const string OldSourceVariable = "STATIONGOD_ROUTED_SNAPSHOT_FROM";

    private static string SnapshotPath => Path.Combine(CatalogueFiles.RepositoryRoot(), SnapshotFile);

    internal static Dictionary<string, List<MessageExpression>> RoutedNow()
    {
        Dictionary<string, List<MessageExpression>> routed = new Dictionary<string, List<MessageExpression>>(StringComparer.Ordinal);
        foreach (SourceFile file in ModSource.Instance.Files)
        {
            List<MessageExpression> found = MessageExpression.In(file.Root).Where(message => message.Routed).ToList();
            if (found.Count > 0)
            {
                routed[file.Path] = found;
            }
        }

        return routed;
    }

    private static Dictionary<string, List<string>> Snapshot() =>
        JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(SnapshotPath))!;

    [Fact]
    public void EveryRoutedMessagePrintsItsOldTextWithoutAMap()
    {
        Dictionary<string, List<string>> snapshot = Snapshot();
        Dictionary<string, List<MessageExpression>> routed = RoutedNow();
        List<string> problems = new List<string>();
        foreach (string file in snapshot.Keys.Union(routed.Keys).OrderBy(name => name, StringComparer.Ordinal))
        {
            List<string> old = snapshot.TryGetValue(file, out List<string>? texts) ? texts : new List<string>();
            List<string> now = routed.TryGetValue(file, out List<MessageExpression>? messages)
                ? messages.ConvertAll(message => message.Text)
                : new List<string>();
            foreach (string text in Without(now, old))
            {
                problems.Add($"{file}: prints \"{text}\" without a map, which is not an old text");
            }

            foreach (string text in Without(old, now))
            {
                problems.Add($"{file}: no routed message prints the old \"{text}\"");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(snapshot.Values.Sum(texts => texts.Count) > 0);
    }

    [Fact]
    public void EveryMarkedTextIsRenderedByVocabularyText()
    {
        List<string> loose = new List<string>();
        foreach (SourceFile file in ModSource.Instance.Files)
        {
            foreach (MessageExpression message in MessageExpression.In(file.Root))
            {
                if (message.Marked && !message.ReachesText())
                {
                    int line = message.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    loose.Add($"{file.Path}:{line}: {message.Text}");
                }
            }
        }

        Assert.True(loose.Count == 0,
            "A text with {arg:} or {tool:} marks prints them unless Vocabulary.Text (or Args.Reject's form) renders it:" +
            Environment.NewLine + string.Join(Environment.NewLine, loose));
    }

    /// <summary>
    /// A message in Api that quotes a catalogue argument ('reference_id' or `reference_id`) names it through the
    /// vocabulary ('{arg:reference_id}' in a Text template, a Vocabulary.Arg hole), so a new message cannot reopen the
    /// gap the rename map closed.
    /// </summary>
    [Fact]
    public void NoMessageInApiQuotesAnArgumentOutsideTheVocabulary()
    {
        HashSet<string> arguments = new HashSet<string>(StringComparer.Ordinal);
        foreach (Pure.Catalogue.CatalogueMethod method in TestCatalogue.File.Value.Catalogue.Methods)
        {
            arguments.UnionWith(method.ArgumentNames.Sorted);
        }

        System.Text.RegularExpressions.Regex quoted =
            new System.Text.RegularExpressions.Regex(@"['`]([a-z][a-z0-9_]*)['`\[.]");
        List<string> loose = new List<string>();
        foreach (SourceFile file in ModSource.Instance.Files)
        {
            if (!file.Path.StartsWith("Api/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (SyntaxToken token in file.Root.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match match in quoted.Matches(token.ValueText))
                {
                    if (arguments.Contains(match.Groups[1].Value))
                    {
                        int line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        loose.Add($"{file.Path}:{line}: {token.ValueText}");
                    }
                }
            }
        }

        Assert.True(loose.Count == 0,
            "Name the argument through the vocabulary ('{arg:name}' in a Vocabulary.Text template, or " +
            "{Vocabulary.Arg(\"name\")} in an interpolated string):" + Environment.NewLine +
            string.Join(Environment.NewLine, loose));
    }

    [Fact]
    public void NoRoutedMessageIsWorkedOutOnceForEveryCall()
    {
        List<string> frozen = new List<string>();
        foreach (KeyValuePair<string, List<MessageExpression>> file in RoutedNow())
        {
            foreach (MessageExpression message in file.Value)
            {
                if (!message.IsTextListTemplate && WorkedOutOnce(message.Node))
                {
                    int line = message.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    frozen.Add($"{file.Key}:{line}: {message.Text}");
                }
            }
        }

        Assert.True(frozen.Count == 0,
            "A routed message in a static or const initialiser is worked out once, with whatever map is current then:" +
            Environment.NewLine + string.Join(Environment.NewLine, frozen));
    }

    // Whether the expression is evaluated outside any call: a static or const field's initialiser, a property's
    // initialiser or a static constructor, with no method, accessor, lambda or local function in between.
    private static bool WorkedOutOnce(SyntaxNode node)
    {
        foreach (SyntaxNode up in node.Ancestors())
        {
            switch (up)
            {
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case AccessorDeclarationSyntax:
                case ArrowExpressionClauseSyntax:
                case MethodDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                    return false;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                case PropertyDeclarationSyntax:
                    return true;
                case FieldDeclarationSyntax field:
                    return field.Modifiers.Any(SyntaxKind.StaticKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword);
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the snapshot when STATIONGOD_ROUTED_SNAPSHOT_FROM names a revision before the routing: each routed
    /// message's map-less text is taken from that revision's file, where it must appear as a message of its own.
    /// Without the variable it does nothing.
    /// </summary>
    [Fact]
    public void WriteTheSnapshotFromTheOldSource()
    {
        string? revision = Environment.GetEnvironmentVariable(OldSourceVariable);
        if (string.IsNullOrEmpty(revision))
        {
            return;
        }

        SortedDictionary<string, List<string>> snapshot = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> missing = new List<string>();
        foreach (KeyValuePair<string, List<MessageExpression>> file in RoutedNow())
        {
            string? oldSource = OldFile(revision, file.Key);
            List<string> oldTexts = oldSource == null
                ? new List<string>()
                : MessageExpression.In(CSharpSyntaxTree.ParseText(oldSource).GetRoot()).ConvertAll(message => message.Text);
            List<string> taken = new List<string>();
            foreach (MessageExpression message in file.Value)
            {
                int at = oldTexts.IndexOf(message.Text);
                if (at < 0)
                {
                    missing.Add($"{file.Key}: {message.Text}");
                    continue;
                }

                taken.Add(oldTexts[at]);
                oldTexts.RemoveAt(at);
            }

            taken.Sort(StringComparer.Ordinal);
            snapshot[file.Key] = taken;
        }

        Assert.True(missing.Count == 0, "Not in the old source:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, JsonConvert.SerializeObject(snapshot, Formatting.Indented) + "\n",
            new UTF8Encoding(false));
    }

    private static string? OldFile(string revision, string relativePath)
    {
        ProcessStartInfo start = new ProcessStartInfo("git", $"show {revision}:src/StationGodMCP.Mod/{relativePath}")
        {
            WorkingDirectory = CatalogueFiles.RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        using Process git = Process.Start(start)!;
        string text = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return git.ExitCode == 0 ? text : null;
    }

    // The texts of from that are not in what (as multisets).
    private static List<string> Without(List<string> from, List<string> what)
    {
        List<string> left = new List<string>(what);
        List<string> extra = new List<string>();
        foreach (string text in from)
        {
            if (!left.Remove(text))
            {
                extra.Add(text);
            }
        }

        return extra;
    }
}
