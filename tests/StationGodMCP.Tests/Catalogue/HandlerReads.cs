#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>A method's handler: the class and method its ApiHost.Methods entry calls (ThingHealthApi.Handle).</summary>
internal sealed class Handler
{
    internal Handler(string method, string type, string member, SourceFile file)
    {
        Method = method;
        Type = type;
        Member = member;
        File = file;
    }

    internal string Method { get; }

    internal string Type { get; }

    internal string Member { get; }

    internal SourceFile File { get; }

    /// <summary>Every handler by method name, read from ApiHost.Methods.</summary>
    internal static IReadOnlyDictionary<string, Handler> All => Map.Value;

    private static readonly Lazy<Dictionary<string, Handler>> Map = new Lazy<Dictionary<string, Handler>>(Read);

    private static Dictionary<string, Handler> Read()
    {
        ModSource source = ModSource.Instance;
        SourceFile host = source.File("Api/ApiHost.cs");
        VariableDeclaratorSyntax methods = host.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.Text == "Methods");
        Dictionary<string, Handler> handlers = new Dictionary<string, Handler>(StringComparer.Ordinal);
        foreach (AssignmentExpressionSyntax entry in methods.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!(entry.Left is ImplicitElementAccessSyntax key) ||
                !(entry.Right is LambdaExpressionSyntax { ExpressionBody: InvocationExpressionSyntax call }) ||
                !(call.Expression is MemberAccessExpressionSyntax target))
            {
                throw new InvalidOperationException($"ApiHost.Methods entry not understood: {entry}");
            }

            string name = source.StringOf(key.ArgumentList.Arguments[0].Expression)!;
            string type = ((IdentifierNameSyntax)target.Expression).Identifier.Text;
            SourceFile file = source.FilesDeclaring(type).Single(candidate => candidate.Path.StartsWith("Api/", StringComparison.Ordinal));
            handlers.Add(name, new Handler(name, type, target.Name.Identifier.Text, file));
        }

        return handlers;
    }

    /// <summary>The handler's file and every file under Api/ declaring a type the handler file names, one level deep.</summary>
    internal IReadOnlyList<SourceFile> Files()
    {
        List<SourceFile> files = new List<SourceFile> { File };
        foreach (string name in File.NamesUsed())
        {
            foreach (SourceFile declaring in ModSource.Instance.FilesDeclaring(name))
            {
                if (declaring.Path.StartsWith("Api/", StringComparison.Ordinal) && !files.Contains(declaring))
                {
                    files.Add(declaring);
                }
            }
        }

        return files;
    }

    public override string ToString() => $"{Method} -> {Type}.{Member} ({File.Path})";
}

/// <summary>An argument name read through Args: the name, the reader, and where.</summary>
internal sealed class ArgRead
{
    internal ArgRead(string name, string reader, SourceFile file, InvocationExpressionSyntax call)
    {
        Name = name;
        Reader = reader;
        File = file;
        Call = call;
    }

    internal string Name { get; }

    internal string Reader { get; }

    internal SourceFile File { get; }

    internal InvocationExpressionSyntax Call { get; }

    internal int Line => Call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public override string ToString() => $"{Name} ({Reader}, {File.Path}:{Line})";

    /// <summary>Args' getters whose first argument is an argument name (Args.cs).</summary>
    internal static readonly HashSet<string> Readers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Has", "ThingId", "OptionalThingId", "ThingIds", "String", "OptionalString", "OptionalBool", "OptionalInt",
        "OptionalDouble", "Double", "Int", "OptionalPositiveDouble", "Array", "OptionalObject", "IsWord", "Optional",
        "Objects", "With"
    };

    /// <summary>Every name the file reads through Args; Reject's names (not its form) included.</summary>
    internal static List<ArgRead> In(SourceFile file)
    {
        List<ArgRead> reads = new List<ArgRead>();
        if (file.Path == "Api/Shared/Args.cs")
        {
            return reads;
        }

        foreach (InvocationExpressionSyntax call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!(call.Expression is MemberAccessExpressionSyntax member) || call.ArgumentList.Arguments.Count == 0)
            {
                continue;
            }

            string reader = member.Name.Identifier.Text;
            if (reader == "Reject")
            {
                foreach (ArgumentSyntax argument in call.ArgumentList.Arguments.Skip(1))
                {
                    if (ModSource.Instance.StringOf(argument.Expression) is string rejected)
                    {
                        reads.Add(new ArgRead(rejected, reader, file, call));
                    }
                }

                continue;
            }

            if (Readers.Contains(reader) &&
                ModSource.Instance.StringOf(call.ArgumentList.Arguments[0].Expression) is string name)
            {
                reads.Add(new ArgRead(name, reader, file, call));
            }
        }

        return reads;
    }
}
