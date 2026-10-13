#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>A place the mod refuses with an error code: the code, and the message text there when it can be read.</summary>
internal sealed class ErrorSite
{
    private ErrorSite(string code, string? message, string where)
    {
        Code = code;
        Message = message;
        Where = where;
    }

    internal string Code { get; }

    internal string? Message { get; }

    internal string Where { get; }

    /// <summary>
    /// Every code passed as a literal or const string to ApiErrors.Refused, new ApiException or new ErrorView (both
    /// branches of a conditional), every const string whose name ends in Code, and every Code property returning a
    /// literal (GasHoldRule's verdicts, RunKind.ShortageCode).
    /// </summary>
    internal static List<ErrorSite> All()
    {
        List<ErrorSite> sites = new List<ErrorSite>();
        ModSource source = ModSource.Instance;
        foreach (SourceFile file in source.Files)
        {
            foreach (SyntaxNode node in file.Root.DescendantNodes())
            {
                ArgumentListSyntax? arguments = node switch
                {
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Refused" } } call =>
                        call.ArgumentList,
                    ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.Text: "ApiException" or "ErrorView" } } creation =>
                        creation.ArgumentList,
                    _ => null
                };
                if (arguments == null || arguments.Arguments.Count < 1)
                {
                    continue;
                }

                string? message = arguments.Arguments.Count > 1 ? MessageOf(arguments.Arguments[1].Expression) : null;
                foreach (ExpressionSyntax code in Branches(arguments.Arguments[0].Expression))
                {
                    if (source.StringOf(code) is string text)
                    {
                        sites.Add(new ErrorSite(text, message, $"{file.Path}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
                    }
                }
            }

            foreach (VariableDeclaratorSyntax variable in file.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (variable.Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword) &&
                    variable.Identifier.Text.EndsWith("Code", StringComparison.Ordinal) &&
                    variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    sites.Add(new ErrorSite(literal.Token.ValueText, null, file.Path));
                }
            }

            foreach (PropertyDeclarationSyntax property in file.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (property.Identifier.Text.EndsWith("Code", StringComparison.Ordinal) &&
                    property.ExpressionBody?.Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    sites.Add(new ErrorSite(literal.Token.ValueText, null, file.Path));
                }
            }
        }

        return sites;
    }

    private static IEnumerable<ExpressionSyntax> Branches(ExpressionSyntax expression) => expression switch
    {
        ConditionalExpressionSyntax conditional => Branches(conditional.WhenTrue).Concat(Branches(conditional.WhenFalse)),
        ParenthesizedExpressionSyntax parenthesized => Branches(parenthesized.Expression),
        _ => new[] { expression }
    };

    // The message as written, interpolation holes shown as <name>; null when it is not literal text.
    private static string? MessageOf(ExpressionSyntax expression) => RawMessageOf(expression)?.Trim();

    private static string? RawMessageOf(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(content =>
            content is InterpolatedStringTextSyntax plain ? plain.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}") : $"<{Hole(((InterpolationSyntax)content).Expression)}>")),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum =>
            RawMessageOf(sum.Left) is string left && RawMessageOf(sum.Right) is string right ? left + right : null,
        ParenthesizedExpressionSyntax parenthesized => RawMessageOf(parenthesized.Expression),
        _ => null
    };

    // A hole by the name it reads (id, ReferenceId as reference_id), or an ellipsis for anything computed.
    private static string Hole(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => Snake(name.Identifier.Text),
        MemberAccessExpressionSyntax member when member.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax
                                                 or ThisExpressionSyntax => Snake(member.Name.Identifier.Text),
        _ => "…"
    };

    private static string Snake(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
