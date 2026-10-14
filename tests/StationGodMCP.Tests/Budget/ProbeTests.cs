#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// A tool for keeping ReplyShapes current, idle unless its variable is set: SG_TREE=file writes every view's fields,
/// the starting point for a new what's shape.
/// </summary>
public sealed class ProbeTests
{
    [Fact]
    public void Tree()
    {
        string? output = Environment.GetEnvironmentVariable("SG_TREE");
        if (output == null)
        {
            return;
        }

        System.Text.StringBuilder report = new System.Text.StringBuilder();
        HashSet<Type> seen = new HashSet<Type>();
        foreach (System.Text.Json.Nodes.JsonNode? verb in
                 System.Text.Json.Nodes.JsonNode.Parse(StationGodMCP.Client.GameCatalogue.BuiltIn.Document.GetRawText())!
                     ["verbs"]!.AsObject().Select(pair => pair.Value))
        {
            foreach ((string what, System.Text.Json.Nodes.JsonNode? node) in verb!["whats"]!.AsObject())
            {
                if (node?["reply"]?["variants"] is not System.Text.Json.Nodes.JsonArray variants)
                {
                    continue;
                }

                foreach (System.Text.Json.Nodes.JsonNode? variant in variants)
                {
                    if ((string?)variant?["view"] is not { } viewName)
                    {
                        continue;
                    }

                    Type? type = ViewShapes.Find(viewName);
                    if (type != null)
                    {
                        report.AppendLine($"## {what} {type.Name}");
                        Dump(type, report, seen, 1);
                    }
                }
            }
        }

        File.WriteAllText(output, report.ToString());
    }

    private static void Dump(Type type, System.Text.StringBuilder report, HashSet<Type> seen, int depth)
    {
        if (!seen.Add(type) || depth > 8)
        {
            return;
        }

        foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance |
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            string name = field.Name.StartsWith("<") ? field.Name.Substring(1, field.Name.IndexOf('>') - 1) : field.Name;
            Type fieldType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            Type? element = fieldType.IsArray ? fieldType.GetElementType() :
                fieldType.IsGenericType && fieldType.GetGenericArguments().Length == 1 && fieldType != typeof(string) &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(fieldType) ? fieldType.GetGenericArguments()[0] : null;
            bool dict = fieldType.IsGenericType && fieldType.GetGenericArguments().Length == 2;
            string shown = element != null ? $"[{element.Name}]" :
                dict ? $"{{{string.Join(",", fieldType.GetGenericArguments().Select(t => t.Name))}}}" : fieldType.Name;
            report.AppendLine($"{new string(' ', depth * 2)}{type.Name}.{name}: {shown}");
            Type inner = element ?? (dict ? fieldType.GetGenericArguments()[1] : fieldType);
            if (inner.Namespace != null && inner.Namespace.StartsWith("StationGodMCP") && !inner.IsEnum)
            {
                Dump(inner, report, seen, depth + 1);
            }
        }
    }
}
