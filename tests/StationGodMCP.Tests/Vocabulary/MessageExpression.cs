#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.VocabularyChecks;

/// <summary>
/// One message expression in the mod's source, read without a rename map: a string literal, an interpolated string or a
/// + chain of them, as the outermost such expression. Its text is what it prints with no map: literal text with the
/// {arg:}/{tool:} marks taken off, Vocabulary.Arg("x") and Vocabulary.Tool("x") as x, Vocabulary.Text(t) as t, and any
/// other operand or interpolation hole as {its source, whitespace collapsed}. Routed: it names a name through the
/// vocabulary (a mark, or Arg or Tool of a literal).
/// </summary>
internal sealed class MessageExpression
{
    private static readonly Regex Marks = new Regex(@"\{(?:arg|tool):([^}]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

    private MessageExpression(ExpressionSyntax node, string text, bool routed, bool marked)
    {
        Node = node;
        Text = text;
        Routed = routed;
        Marked = marked;
    }

    internal ExpressionSyntax Node { get; }

    /// <summary>What it prints without a map.</summary>
    internal string Text { get; }

    internal bool Routed { get; }

    /// <summary>Whether a literal in it carries {arg:} or {tool:} marks outside any Vocabulary.Text call in it.</summary>
    internal bool Marked { get; }

    /// <summary>Every outermost message expression in a file.</summary>
    internal static List<MessageExpression> In(SyntaxNode root)
    {
        List<MessageExpression> found = new List<MessageExpression>();
        foreach (ExpressionSyntax node in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (!IsMessage(node) || IsInsideMessage(node))
            {
                continue;
            }

            StringBuilder text = new StringBuilder();
            bool routed = false;
            bool marked = false;
            Read(node, text, ref routed, ref marked, false);
            found.Add(new MessageExpression(node, text.ToString(), routed, marked));
        }

        return found;
    }

    /// <summary>
    /// Whether something renders the expression's marks itself: it is the form (first argument) of Args.Reject or
    /// Args.RejectAllBut, or a template of a TextList.
    /// </summary>
    internal bool ReachesText() => IsTextListTemplate || IsRejectForm;

    /// <summary>Whether the expression is one of the templates a new TextList(...) takes.</summary>
    internal bool IsTextListTemplate =>
        Argument() is ArgumentSyntax argument && argument.Parent?.Parent is ObjectCreationExpressionSyntax creation &&
        creation.Type.ToString() == "TextList";

    private bool IsRejectForm =>
        Argument() is ArgumentSyntax argument && argument.Parent is ArgumentListSyntax list &&
        list.Parent is InvocationExpressionSyntax invocation && list.Arguments.IndexOf(argument) == 0 &&
        (Called(invocation).EndsWith(".Reject") || Called(invocation).EndsWith(".RejectAllBut"));

    private ArgumentSyntax? Argument()
    {
        SyntaxNode? up = Node.Parent;
        while (up is ParenthesizedExpressionSyntax)
        {
            up = up.Parent;
        }

        return up as ArgumentSyntax;
    }

    private static bool IsMessage(ExpressionSyntax node) => node switch
    {
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.StringLiteralExpression),
        InterpolatedStringExpressionSyntax => true,
        ParenthesizedExpressionSyntax parenthesized => IsMessage(parenthesized.Expression),
        InvocationExpressionSyntax invocation => IsVocabulary(invocation),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add => IsMessage(add.Left) || IsMessage(add.Right),
        _ => false
    };

    private static bool IsInsideMessage(ExpressionSyntax node)
    {
        SyntaxNode? up = node.Parent;
        if (up is ArgumentSyntax && up.Parent?.Parent is InvocationExpressionSyntax invocation && IsVocabulary(invocation))
        {
            return true;
        }

        if (up is InterpolationSyntax && NamedThrough(node) != null)
        {
            return true;
        }

        return up is ExpressionSyntax parent && !(up is InterpolationSyntax) && IsMessage(parent) &&
               (parent is BinaryExpressionSyntax || parent is ParenthesizedExpressionSyntax);
    }

    private static bool IsVocabulary(InvocationExpressionSyntax invocation) =>
        Called(invocation) is "Vocabulary.Text" or "Vocabulary.Format" or "Vocabulary.Arg" or "Vocabulary.Tool";

    private static string Called(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Expression is IdentifierNameSyntax owner
            ? owner.Identifier.Text + "." + member.Name.Identifier.Text
            : "." + member.Name.Identifier.Text,
        IdentifierNameSyntax name => name.Identifier.Text,
        _ => string.Empty
    };

    private static void Read(ExpressionSyntax node, StringBuilder text, ref bool routed, ref bool marked, bool inText)
    {
        switch (node)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                Plain(literal.Token.ValueText, text, ref routed, ref marked, inText);
                return;
            case InterpolatedStringExpressionSyntax interpolated:
                foreach (InterpolatedStringContentSyntax content in interpolated.Contents)
                {
                    if (content is InterpolatedStringTextSyntax part)
                    {
                        Plain(part.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}"), text, ref routed,
                            ref marked, inText);
                    }
                    else if (content is InterpolationSyntax hole)
                    {
                        if (hole.AlignmentClause == null && hole.FormatClause == null && NamedThrough(hole.Expression) is string name)
                        {
                            text.Append(name);
                            routed = true;
                        }
                        else
                        {
                            routed |= NamesThroughVocabulary(hole.Expression);
                            text.Append('{').Append(Source(hole.Expression)).Append(hole.AlignmentClause?.ToString() ?? string.Empty)
                                .Append(hole.FormatClause?.ToString() ?? string.Empty).Append('}');
                        }
                    }
                }

                return;
            case ParenthesizedExpressionSyntax parenthesized when IsMessage(parenthesized.Expression):
                Read(parenthesized.Expression, text, ref routed, ref marked, inText);
                return;
            case InvocationExpressionSyntax invocation when Called(invocation) is "Vocabulary.Text" or "Vocabulary.Format" &&
                                                            invocation.ArgumentList.Arguments.Count == 1:
                Read(invocation.ArgumentList.Arguments[0].Expression, text, ref routed, ref marked, true);
                return;
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add when IsMessage(add):
                Read(add.Left, text, ref routed, ref marked, inText);
                Read(add.Right, text, ref routed, ref marked, inText);
                return;
        }

        if (NamedThrough(node) is string named)
        {
            text.Append(named);
            routed = true;
            return;
        }

        text.Append('{').Append(Source(node)).Append('}');
    }

    // Whether the expression is Vocabulary.Arg, Tool or Path of anything (a name held in a variable).
    private static bool NamesThroughVocabulary(ExpressionSyntax node) =>
        node is InvocationExpressionSyntax invocation &&
        Called(invocation) is "Vocabulary.Arg" or "Vocabulary.Tool" or "Vocabulary.Path";

    // x for Vocabulary.Arg("x") or Vocabulary.Tool("x"); null for anything else.
    private static string? NamedThrough(ExpressionSyntax node) =>
        node is InvocationExpressionSyntax invocation && Called(invocation) is "Vocabulary.Arg" or "Vocabulary.Tool" &&
        invocation.ArgumentList.Arguments.Count == 1 &&
        invocation.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    private static void Plain(string value, StringBuilder text, ref bool routed, ref bool marked, bool inText)
    {
        if (Marks.IsMatch(value))
        {
            routed = true;
            marked |= !inText;
            value = Marks.Replace(value, "$1");
        }

        text.Append(value);
    }

    // A hole's source as it reads without a map: routed names inside it (a nested Text, Arg or Tool, a mark) as the
    // names, so a hole that routes a literal of its own compares equal to its old source.
    private static readonly Regex NestedText = new Regex(
        @"Vocabulary\.(?:Text|Format)\(((?:\$?""(?:[^""\\]|\\.)*""\s*\+?\s*)+)\)", RegexOptions.CultureInvariant);

    private static readonly Regex NestedHole = new Regex(@"\{Vocabulary\.(?:Arg|Tool)\(""([^""]*)""\)\}",
        RegexOptions.CultureInvariant);

    private static readonly Regex NestedName = new Regex(@"Vocabulary\.(?:Arg|Tool|Path)\(([^()]*)\)",
        RegexOptions.CultureInvariant);

    // A literal cut over lines inside a hole ("a " + "b", $"a {x} " + "b") reads as the one literal it was.
    private static readonly Regex Joined = new Regex(@"""\s*\+\s*\$?""", RegexOptions.CultureInvariant);

    private static string Source(SyntaxNode node)
    {
        string source = NestedText.Replace(node.ToString(), "$1");
        source = NestedHole.Replace(source, "$1");
        source = NestedName.Replace(source, "$1");
        source = Marks.Replace(source, "$1");
        source = Joined.Replace(source, string.Empty);
        return Spaces.Replace(source, " ").Trim();
    }
}
