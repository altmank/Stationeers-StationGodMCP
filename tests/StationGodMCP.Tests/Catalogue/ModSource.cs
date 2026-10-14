#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The mod's sources as Roslyn syntax trees, parsed once: every file by its path, and every const string by name.
/// Syntax only: the mod uses game types the tests cannot compile, so nothing here needs a semantic model.
/// </summary>
internal sealed class ModSource
{
    private static readonly Lazy<ModSource> Loaded = new Lazy<ModSource>(() => new ModSource(Root()));

    private readonly Dictionary<string, string> _constants = new Dictionary<string, string>(StringComparer.Ordinal);

    private ModSource(string root)
    {
        foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            SourceFile file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'),
                CSharpSyntaxTree.ParseText(System.IO.File.ReadAllText(path)).GetRoot());
            Files.Add(file);

            foreach (FieldDeclarationSyntax field in file.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (!field.Modifiers.Any(SyntaxKind.ConstKeyword))
                {
                    continue;
                }

                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                        literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        _constants[variable.Identifier.Text] = literal.Token.ValueText;
                    }
                }
            }
        }
    }

    internal static ModSource Instance => Loaded.Value;

    internal List<SourceFile> Files { get; } = new List<SourceFile>();

    internal SourceFile File(string relativePath) =>
        Files.Single(file => file.Path == relativePath);

    /// <summary>A string literal, a const string named by it (Foo or Type.Foo), or null.</summary>
    internal string? StringOf(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        IdentifierNameSyntax name => _constants.TryGetValue(name.Identifier.Text, out string? value) ? value : null,
        MemberAccessExpressionSyntax member =>
            _constants.TryGetValue(member.Name.Identifier.Text, out string? value) ? value : null,
        ParenthesizedExpressionSyntax parenthesized => StringOf(parenthesized.Expression),
        _ => null
    };

    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "StationGodMCP.Mod"));
}

/// <summary>One parsed source file of the mod.</summary>
internal sealed class SourceFile
{
    internal SourceFile(string path, SyntaxNode root)
    {
        Path = path;
        Root = root;
    }

    /// <summary>Relative to src/StationGodMCP.Mod, with forward slashes (Api/ThingHealth.cs).</summary>
    internal string Path { get; }

    internal SyntaxNode Root { get; }

    public override string ToString() => Path;
}
