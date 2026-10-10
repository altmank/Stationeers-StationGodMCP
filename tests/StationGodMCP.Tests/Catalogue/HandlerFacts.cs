#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// What a handler's files say about one integer argument where they read it (its bounds, its default), so the
/// catalogue's statement of the same can be compared with it.
/// </summary>
internal abstract class ArgumentFact
{
    protected ArgumentFact(string name, ArgRead read)
    {
        Name = name;
        Read = read;
    }

    internal string Name { get; }

    internal ArgRead Read { get; }

    /// <summary>Two reads of a name agree when their facts are equal.</summary>
    protected abstract object Key { get; }

    /// <summary>
    /// The different facts each argument is read with: the handler file's own reads when it has any, else those of
    /// every other file of the handler's.
    /// </summary>
    internal static Dictionary<string, List<T>> ByName<T>(Handler handler, IEnumerable<T> facts)
        where T : ArgumentFact =>
        facts.GroupBy(fact => fact.Name).ToDictionary(
            group => group.Key,
            group =>
            {
                List<T> own = group.Where(fact => fact.Read.File == handler.File).ToList();
                return (own.Count > 0 ? own : group.ToList()).GroupBy(fact => fact.Key).Select(same => same.First()).ToList();
            },
            StringComparer.Ordinal);

    // PageRequest.From(args, defaultLimit, maximumLimit) reads limit from 1 to its maximum, defaulting to its default (Api/Shared/Paging.cs).
    protected static IEnumerable<InvocationExpressionSyntax> PageReads(SourceFile file) =>
        file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "PageRequest" }, Name.Identifier.Text: "From" } &&
            call.ArgumentList.Arguments.Count == 3);
}

/// <summary>An integer argument read with literal bounds (OptionalInt("limit", 1, 500)).</summary>
internal sealed class IntRange : ArgumentFact
{
    private IntRange(string name, long minimum, long maximum, ArgRead read)
        : base(name, read)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    internal long Minimum { get; }

    internal long Maximum { get; }

    protected override object Key => (Minimum, Maximum);

    public override string ToString() => $"{Minimum}..{Maximum} at {Read.File.Path}:{Read.Line}";

    /// <summary>
    /// The one range each of a method's integer arguments is read with (ArgumentFact.ByName). Several different
    /// ranges for a name give none for it; each such name is reported through conflict, with the reads that disagree.
    /// </summary>
    internal static Dictionary<string, IntRange> Expected(Handler handler, Action<string, string> conflict)
    {
        Dictionary<string, IntRange> agreed = new Dictionary<string, IntRange>(StringComparer.Ordinal);
        foreach ((string name, List<IntRange> distinct) in ByName(handler, In(handler)))
        {
            if (distinct.Count == 1)
            {
                agreed[name] = distinct[0];
            }
            else
            {
                conflict(name, $"{handler.Method}: {name} read with several ranges: {string.Join("; ", distinct)}");
            }
        }

        return agreed;
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

            foreach (InvocationExpressionSyntax call in PageReads(file))
            {
                if (ModSource.IntegerOf(call.ArgumentList.Arguments[2].Expression, file) is long maximumLimit)
                {
                    ranges.Add(new IntRange("limit", 1, maximumLimit, new ArgRead("limit", "PageRequest.From", file, call)));
                }
            }
        }

        return ranges;
    }
}

/// <summary>
/// An integer argument's default where it is read: OptionalInt("limit", 1, 500) ?? DefaultLimit, or PageRequest.From's
/// default limit, the constant resolved from the source.
/// </summary>
internal sealed class IntDefault : ArgumentFact
{
    private IntDefault(string name, long value, ArgRead read)
        : base(name, read)
    {
        Value = value;
    }

    internal long Value { get; }

    protected override object Key => Value;

    public override string ToString() => $"{Value} at {Read.File.Path}:{Read.Line}";

    /// <summary>The defaults each of a method's integer arguments is read with, one per form that reads it (ArgumentFact.ByName).</summary>
    internal static Dictionary<string, List<IntDefault>> Expected(Handler handler) => ByName(handler, In(handler));

    /// <summary>Every OptionalInt read whose ?? fallback resolves to an integer, and every PageRequest.From, in the handler's files.</summary>
    internal static List<IntDefault> In(Handler handler)
    {
        List<IntDefault> defaults = new List<IntDefault>();
        foreach (SourceFile file in handler.Files())
        {
            foreach (ArgRead read in ArgRead.In(file).Where(read => read.Reader == "OptionalInt"))
            {
                if (read.Call.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce &&
                    coalesce.Left == read.Call &&
                    ModSource.IntegerOf(coalesce.Right, file) is long value)
                {
                    defaults.Add(new IntDefault(read.Name, value, read));
                }
            }

            foreach (InvocationExpressionSyntax call in PageReads(file))
            {
                if (ModSource.IntegerOf(call.ArgumentList.Arguments[1].Expression, file) is long defaultLimit)
                {
                    defaults.Add(new IntDefault("limit", defaultLimit, new ArgRead("limit", "PageRequest.From", file, call)));
                }
            }
        }

        return defaults;
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
