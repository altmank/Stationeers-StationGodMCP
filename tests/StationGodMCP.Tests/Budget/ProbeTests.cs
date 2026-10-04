#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// Tools for keeping ReplyShapes current, idle unless their variable is set: SG_SIZES=file writes every shaped
/// method's default reply size and undeclared lists; SG_DETAIL=method with SG_DETAIL_OUT=file breaks one reply down
/// by key; SG_TREE=file writes every view's fields, the starting point for a new tool's shape.
/// </summary>
public sealed class ProbeTests
{
    [Fact]
    public void Sizes()
    {
        string? output = Environment.GetEnvironmentVariable("SG_SIZES");
        if (output == null)
        {
            return;
        }

        StringBuilder report = new StringBuilder();
        foreach (KeyValuePair<string, ReplyShape[]> method in ReplyShapes.ByMethod.OrderBy(pair => pair.Key))
        {
            foreach (ReplyShape shape in method.Value)
            {
                ReplyBudgetTests.Measured measured =
                    ReplyBudgetTests.Measure(shape.WithTopLimits(ReplyBudgetTests.DefaultLimitsOf(method.Key)));
                report.AppendLine($"{method.Key}	{shape.View.Name}	{measured.Bytes}	{string.Join(",", measured.Undeclared)}");
            }
        }

        File.WriteAllText(output, report.ToString());
    }

    [Fact]
    public void Detail()
    {
        string? method = Environment.GetEnvironmentVariable("SG_DETAIL");
        string? output = Environment.GetEnvironmentVariable("SG_DETAIL_OUT");
        if (method == null || output == null)
        {
            return;
        }

        StringBuilder report = new StringBuilder();
        foreach (ReplyShape shape in ReplyShapes.ByMethod[method])
        {
            ReplyBudgetTests.Measured measured =
                ReplyBudgetTests.Measure(shape.WithTopLimits(ReplyBudgetTests.DefaultLimitsOf(method)));
            report.AppendLine($"## {shape.View.Name} {measured.Bytes}");
            Newtonsoft.Json.Linq.JObject root = Newtonsoft.Json.Linq.JObject.Parse(measured.Json);
            Walk(root, "", report, 0);
        }

        File.WriteAllText(output, report.ToString());
    }

    private static void Walk(Newtonsoft.Json.Linq.JToken token, string path, StringBuilder report, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        if (token is Newtonsoft.Json.Linq.JObject obj)
        {
            foreach (Newtonsoft.Json.Linq.JProperty property in obj.Properties())
            {
                int bytes = property.Value.ToString(Newtonsoft.Json.Formatting.None).Length;
                if (bytes > 300)
                {
                    report.AppendLine($"{new string(' ', depth * 2)}{path}{property.Name}: {bytes}");
                    Walk(property.Value, path + property.Name + ".", report, depth + 1);
                }
            }
        }
        else if (token is Newtonsoft.Json.Linq.JArray array && array.Count > 0)
        {
            report.AppendLine($"{new string(' ', depth * 2)}{path}[{array.Count}] each ~{array[0].ToString(Newtonsoft.Json.Formatting.None).Length}");
            Walk(array[0], path + "[0].", report, depth + 1);
        }
    }

    [Fact]
    public void Tree()
    {
        string? output = Environment.GetEnvironmentVariable("SG_TREE");
        if (output == null)
        {
            return;
        }

        StringBuilder report = new StringBuilder();
        HashSet<Type> seen = new HashSet<Type>();
        foreach (JsonElement method in StationGodMCP.Client.GameCatalogue.BuiltIn.Document.GetProperty("methods").EnumerateArray())
        {
            if (!method.TryGetProperty("x-views", out JsonElement views))
            {
                continue;
            }

            foreach (JsonElement viewName in views.EnumerateArray())
            {
                Type? type = ViewShapes.Find(viewName.GetString()!);
                if (type != null)
                {
                    report.AppendLine($"## {method.GetProperty("name").GetString()} {type.Name}");
                    Dump(type, report, seen, 1);
                }
            }
        }

        File.WriteAllText(output, report.ToString());
    }

    private static void Dump(Type type, StringBuilder report, HashSet<Type> seen, int depth)
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
            string shown = element != null ? $"[{element.Name}]" : dict ? $"{{{string.Join(",", fieldType.GetGenericArguments().Select(t => t.Name))}}}" : fieldType.Name;
            report.AppendLine($"{new string(' ', depth * 2)}{type.Name}.{name}: {shown}");
            Type inner = element ?? (dict ? fieldType.GetGenericArguments()[1] : fieldType);
            if (inner.Namespace != null && inner.Namespace.StartsWith("StationGodMCP") && !inner.IsEnum)
            {
                Dump(inner, report, seen, depth + 1);
            }
        }
    }
}
