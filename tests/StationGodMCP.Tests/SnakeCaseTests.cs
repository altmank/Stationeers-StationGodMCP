#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Every reply is a typed view named by ApiJson's snake_case strategy. An anonymous object would bypass the views
/// (and their wire tests), so the mod must have none.
/// </summary>
public sealed class SnakeCaseTests
{
    [Fact]
    public void NoAnonymousObjectsInTheMod()
    {
        List<string> found = new List<string>();
        foreach (string file in Directory.GetFiles(ModSource(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (AnonymousObjectCreationExpressionSyntax creation in
                     root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>())
            {
                int line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                found.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.Empty(found);
    }

    private static string ModSource([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "StationGodMCP.Mod"));
}
