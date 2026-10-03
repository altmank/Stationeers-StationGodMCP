#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The reply parts a handler asks its call's shape about before building them: every Shape.Wants("list", "key") in
/// the handler's own file with both names literal or const strings (protocol.md, Shaping before the reply is built).
/// Only its own file: the files one level down are shared by many handlers (grid_survey reads ThingHealth.cs's
/// HealthScanner), so an ask there cannot be told apart by method.
/// </summary>
internal static class CostlyAsks
{
    internal static HashSet<(string List, string Key)> In(Handler handler)
    {
        HashSet<(string List, string Key)> asked = new HashSet<(string List, string Key)>();
        foreach (InvocationExpressionSyntax call in handler.File.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "Wants",
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Shape" }
                } &&
                call.ArgumentList.Arguments.Count == 2 &&
                ModSource.Instance.StringOf(call.ArgumentList.Arguments[0].Expression) is string list &&
                ModSource.Instance.StringOf(call.ArgumentList.Arguments[1].Expression) is string key)
            {
                asked.Add((list, key));
            }
        }

        return asked;
    }
}
