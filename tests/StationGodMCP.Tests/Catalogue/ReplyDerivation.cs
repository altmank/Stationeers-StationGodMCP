#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The view classes a handler can return, read from its source: the handler's declared return type, or, where it
/// returns object, the types its return statements create or get from the methods they call (a few levels down).
/// What cannot be followed is reported, so the catalogue's reply is completed by hand there.
/// </summary>
internal sealed class ReplyDerivation
{
    private const int MaximumDepth = 5;

    private readonly HashSet<string> _views = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<string> _unresolved = new List<string>();
    private readonly HashSet<SyntaxNode> _visited = new HashSet<SyntaxNode>();

    private ReplyDerivation()
    {
    }

    internal IReadOnlyCollection<string> Views => _views;

    internal IReadOnlyList<string> Unresolved => _unresolved;

    internal static ReplyDerivation Of(Handler handler)
    {
        ReplyDerivation derivation = new ReplyDerivation();
        List<MethodDeclarationSyntax> methods = MethodsNamed(handler.Member, handler.Type);
        if (methods.Count == 0)
        {
            derivation._unresolved.Add($"no method {handler.Type}.{handler.Member}");
        }

        foreach (MethodDeclarationSyntax method in methods)
        {
            derivation.FromMethod(method, 0);
        }

        return derivation;
    }

    private void FromMethod(MethodDeclarationSyntax method, int depth)
    {
        if (!_visited.Add(method))
        {
            return;
        }

        string declared = TypeName(method.ReturnType);
        if (declared != "object" && ViewShapes.Find(declared) != null)
        {
            _views.Add(declared);
            return;
        }

        if (depth > MaximumDepth)
        {
            _unresolved.Add($"too deep at {method.Identifier.Text}");
            return;
        }

        if (method.ExpressionBody != null)
        {
            FromExpression(method.ExpressionBody.Expression, method, depth);
        }

        if (method.Body == null)
        {
            return;
        }

        foreach (ReturnStatementSyntax statement in method.Body.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (statement.Expression == null || statement.Ancestors().TakeWhile(node => node != method)
                    .Any(node => node is LambdaExpressionSyntax or LocalFunctionStatementSyntax or AnonymousMethodExpressionSyntax))
            {
                continue;
            }

            FromExpression(statement.Expression, method, depth);
        }
    }

    private void FromExpression(ExpressionSyntax expression, MethodDeclarationSyntax within, int depth)
    {
        switch (expression)
        {
            case ObjectCreationExpressionSyntax creation:
                AddType(TypeName(creation.Type), expression);
                break;
            case ConditionalExpressionSyntax conditional:
                FromExpression(conditional.WhenTrue, within, depth);
                FromExpression(conditional.WhenFalse, within, depth);
                break;
            case SwitchExpressionSyntax switchExpression:
                foreach (SwitchExpressionArmSyntax arm in switchExpression.Arms)
                {
                    FromExpression(arm.Expression, within, depth);
                }

                break;
            case CastExpressionSyntax cast:
                FromExpression(cast.Expression, within, depth);
                break;
            case ParenthesizedExpressionSyntax parenthesized:
                FromExpression(parenthesized.Expression, within, depth);
                break;
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce:
                FromExpression(coalesce.Left, within, depth);
                FromExpression(coalesce.Right, within, depth);
                break;
            case ThrowExpressionSyntax:
                break;
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }:
                break;
            case InvocationExpressionSyntax call:
                FromCall(call, within, depth);
                break;
            case IdentifierNameSyntax local:
                FromLocal(local.Identifier.Text, within, depth, expression);
                break;
            default:
                _unresolved.Add($"{expression} in {within.Identifier.Text}");
                break;
        }
    }

    private void FromCall(InvocationExpressionSyntax call, MethodDeclarationSyntax within, int depth)
    {
        string? owner;
        string name;
        switch (call.Expression)
        {
            case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } member
                when char.IsUpper(receiver.Identifier.Text[0]):
                owner = receiver.Identifier.Text;
                name = member.Name.Identifier.Text;
                break;
            case MemberAccessExpressionSyntax member:
                owner = null;
                name = member.Name.Identifier.Text;
                break;
            case IdentifierNameSyntax local:
                owner = (within.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                name = local.Identifier.Text;
                break;
            case GenericNameSyntax generic:
                owner = (within.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                name = generic.Identifier.Text;
                break;
            default:
                _unresolved.Add($"{call} in {within.Identifier.Text}");
                return;
        }

        List<MethodDeclarationSyntax> methods = MethodsNamed(name, owner);
        if (methods.Count == 0 || (owner == null && methods.Count > 1))
        {
            _unresolved.Add($"{call.Expression} in {within.Identifier.Text}");
            return;
        }

        foreach (MethodDeclarationSyntax method in methods)
        {
            FromMethod(method, depth + 1);
        }
    }

    // A local whose declaration names its type (View x = ...) or whose initializer can be followed (var x = new View()).
    private void FromLocal(string name, MethodDeclarationSyntax within, int depth, ExpressionSyntax at)
    {
        foreach (VariableDeclarationSyntax declaration in within.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                if (variable.Identifier.Text != name)
                {
                    continue;
                }

                string type = TypeName(declaration.Type);
                if (type != "var" && type != "object")
                {
                    AddType(type, at);
                }
                else if (variable.Initializer != null)
                {
                    FromExpression(variable.Initializer.Value, within, depth);
                }
                else
                {
                    _unresolved.Add($"local {name} in {within.Identifier.Text}");
                }

                return;
            }
        }

        _unresolved.Add($"{name} in {within.Identifier.Text}");
    }

    private void AddType(string type, ExpressionSyntax at)
    {
        if (ViewShapes.Find(type) != null)
        {
            _views.Add(type);
        }
        else
        {
            _unresolved.Add($"type {type} ({at})");
        }
    }

    private static List<MethodDeclarationSyntax> MethodsNamed(string name, string? owner)
    {
        List<MethodDeclarationSyntax> found = new List<MethodDeclarationSyntax>();
        IEnumerable<SourceFile> files = owner != null ? ModSource.Instance.FilesDeclaring(owner) : ModSource.Instance.Files;
        foreach (SourceFile file in files)
        {
            foreach (MethodDeclarationSyntax method in file.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Identifier.Text == name &&
                    (owner == null || (method.Parent as BaseTypeDeclarationSyntax)?.Identifier.Text == owner))
                {
                    found.Add(method);
                }
            }
        }

        return found;
    }

    private static string TypeName(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => TypeName(nullable.ElementType),
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        PredefinedTypeSyntax predefined => predefined.Keyword.Text,
        _ => type.ToString()
    };
}
