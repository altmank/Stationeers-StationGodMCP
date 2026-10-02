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
/// The mod's sources as Roslyn syntax trees, parsed once: which file declares each type, every const string, and the
/// handler class and method each ApiHost.Methods entry calls. Syntax only: the handlers use game types the tests cannot
/// compile, so nothing here needs a semantic model.
/// </summary>
internal sealed class ModSource
{
    private static readonly Lazy<ModSource> Loaded = new Lazy<ModSource>(() => new ModSource(Root()));

    private readonly Dictionary<string, List<SourceFile>> _typeFiles = new Dictionary<string, List<SourceFile>>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constants = new Dictionary<string, string>(StringComparer.Ordinal);

    private ModSource(string root)
    {
        RootFolder = root;
        foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            SourceFile file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'),
                CSharpSyntaxTree.ParseText(System.IO.File.ReadAllText(path)).GetRoot());
            Files.Add(file);
            foreach (BaseTypeDeclarationSyntax type in file.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!_typeFiles.TryGetValue(type.Identifier.Text, out List<SourceFile>? files))
                {
                    _typeFiles[type.Identifier.Text] = files = new List<SourceFile>();
                }

                if (!files.Contains(file))
                {
                    files.Add(file);
                }
            }

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

    internal string RootFolder { get; }

    internal List<SourceFile> Files { get; } = new List<SourceFile>();

    /// <summary>The files that declare a type of this name (partial types and same-named nested types alike).</summary>
    internal IReadOnlyList<SourceFile> FilesDeclaring(string type) =>
        _typeFiles.TryGetValue(type, out List<SourceFile>? files) ? files : Array.Empty<SourceFile>();

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

    /// <summary>An integer literal (possibly negated), an int const resolved from the same file, or null.</summary>
    internal static long? IntegerOf(ExpressionSyntax expression, SourceFile file) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
                                             literal.Token.Value is int or long or uint =>
            Convert.ToInt64(literal.Token.Value),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } negated =>
            IntegerOf(negated.Operand, file) is long value ? -value : null,
        IdentifierNameSyntax name => EnclosingConstant(name, file) ?? file.IntegerConstant(name.Identifier.Text),
        MemberAccessExpressionSyntax member => Instance.IntegerConstant(member),
        _ => null
    };

    // A const named from inside a type: the innermost enclosing type that declares it wins.
    private static long? EnclosingConstant(IdentifierNameSyntax name, SourceFile file)
    {
        foreach (BaseTypeDeclarationSyntax type in name.Ancestors().OfType<BaseTypeDeclarationSyntax>())
        {
            if (file.IntegerConstant(name.Identifier.Text, type.Identifier.Text) is long value)
            {
                return value;
            }
        }

        return null;
    }

    // Type.Name for an int const declared in Type, or int.MaxValue / int.MinValue.
    private long? IntegerConstant(MemberAccessExpressionSyntax member)
    {
        if (member.Expression is PredefinedTypeSyntax { Keyword.Text: "int" })
        {
            return member.Name.Identifier.Text switch
            {
                "MaxValue" => int.MaxValue,
                "MinValue" => int.MinValue,
                _ => null
            };
        }

        string owner = member.Expression is IdentifierNameSyntax identifier ? identifier.Identifier.Text : string.Empty;
        foreach (SourceFile file in FilesDeclaring(owner))
        {
            if (file.IntegerConstant(member.Name.Identifier.Text, owner) is long value)
            {
                return value;
            }
        }

        return null;
    }

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

    /// <summary>An int const of this file, in any type or in the named one.</summary>
    internal long? IntegerConstant(string name, string? inType = null)
    {
        foreach (FieldDeclarationSyntax field in Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                (inType != null && (field.Parent as BaseTypeDeclarationSyntax)?.Identifier.Text != inType))
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
            {
                if (variable.Identifier.Text == name && variable.Initializer != null)
                {
                    return ModSource.IntegerOf(variable.Initializer.Value, this);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every name the file can use as a type (identifiers and generic names), for following it one level: all simple
    /// names except members reached through an expression (x.Name), member bindings (x?.Name) and local method calls.
    /// </summary>
    internal HashSet<string> NamesUsed()
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SimpleNameSyntax name in Root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            bool member = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ||
                          name.Parent is MemberBindingExpressionSyntax ||
                          name.Parent is InvocationExpressionSyntax call && call.Expression == name;
            if (!member)
            {
                names.Add(name.Identifier.Text);
            }
        }

        return names;
    }

    /// <summary>Whether the file has this string as a literal (an argument name read some other way than Args).</summary>
    internal bool NamesLiteral(string text) =>
        Root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Any(literal => literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText == text);

    public override string ToString() => Path;
}
