#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>An integer argument read with literal bounds (OptionalInt("limit", 1, 500)).</summary>
internal sealed class IntRange
{
    private IntRange(string name, long minimum, long maximum, ArgRead read)
    {
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
        Read = read;
    }

    internal string Name { get; }

    internal long Minimum { get; }

    internal long Maximum { get; }

    internal ArgRead Read { get; }

    public override string ToString() => $"{Minimum}..{Maximum} at {Read.File.Path}:{Read.Line}";

    /// <summary>
    /// The bounds a method's integer arguments are read with: the handler file's own read when it has one, else the
    /// one bound every other file of the handler's agrees on. Several different bounds for a name in the handler file,
    /// or none agreed elsewhere, give no bound for it (each is reported through conflict).
    /// </summary>
    internal static Dictionary<string, IntRange> Expected(Handler handler, Action<string> conflict)
    {
        Dictionary<string, IntRange> expected = new Dictionary<string, IntRange>(StringComparer.Ordinal);
        foreach (IGrouping<string, IntRange> group in In(handler).GroupBy(range => range.Name))
        {
            List<IntRange> own = group.Where(range => range.Read.File == handler.File).ToList();
            List<IntRange> candidates = own.Count > 0 ? own : group.ToList();
            List<IntRange> distinct = candidates.GroupBy(range => (range.Minimum, range.Maximum)).Select(g => g.First()).ToList();
            if (distinct.Count == 1)
            {
                expected[group.Key] = distinct[0];
            }
            else
            {
                conflict($"{handler.Method}: {group.Key} read with several ranges: {string.Join("; ", distinct)}");
            }
        }

        return expected;
    }

    /// <summary>Every OptionalInt / Int read with both bounds resolvable, in the handler's files.</summary>
    internal static List<IntRange> In(Handler handler)
    {
        List<IntRange> ranges = new List<IntRange>();
        foreach (SourceFile file in handler.Files())
        {
            foreach (ArgRead read in ArgRead.In(file))
            {
                if ((read.Reader != "OptionalInt" && read.Reader != "Int") || read.Call.ArgumentList.Arguments.Count < 3)
                {
                    continue;
                }

                long? minimum = ModSource.IntegerOf(read.Call.ArgumentList.Arguments[1].Expression, file);
                long? maximum = ModSource.IntegerOf(read.Call.ArgumentList.Arguments[2].Expression, file);
                if (minimum.HasValue && maximum.HasValue)
                {
                    ranges.Add(new IntRange(read.Name, minimum.Value, maximum.Value, read));
                }
            }

            // PageRequest.From(args, defaultLimit, maximumLimit) reads limit from 1 to its maximum (Api/Shared/Paging.cs).
            foreach (InvocationExpressionSyntax call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "PageRequest" }, Name.Identifier.Text: "From" } &&
                    call.ArgumentList.Arguments.Count == 3 &&
                    ModSource.IntegerOf(call.ArgumentList.Arguments[2].Expression, file) is long maximumLimit)
                {
                    ranges.Add(new IntRange("limit", 1, maximumLimit, new ArgRead("limit", "PageRequest.From", file, call)));
                }
            }
        }

        return ranges;
    }
}

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
