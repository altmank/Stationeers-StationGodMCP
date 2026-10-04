#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A view that holds back entries of a list it carries (a report listing up to limit pieces, a stack window, a log
/// tail): it notes each such list under path, the JSON path of the view in the reply ("" at the top, "dry_run." under
/// a plan's dry run), through Pure.Shaping.Truncations.
/// </summary>
internal interface ITruncatingView
{
    void NoteTruncations(string path);
}

/// <summary>
/// Finds the truncating views of a reply, at its top and in the objects it holds (not inside lists, where none
/// lives), and has each note its lists under its JSON path. Properties are read once per type and cached; the reply's
/// lists are never walked, so a world-sized reply costs no more than a small one.
/// </summary>
internal static class TruncatingViews
{
    private const int MaximumDepth = 4;

    private static readonly SnakeCaseNamingStrategy Snake = new SnakeCaseNamingStrategy();

    private static readonly Dictionary<Type, PropertyInfo[]> Objects = new Dictionary<Type, PropertyInfo[]>();

    internal static void Note(object? reply) => Walk(reply, string.Empty, 0);

    private static void Walk(object? value, string path, int depth)
    {
        if (value == null || depth > MaximumDepth)
        {
            return;
        }

        if (value is ITruncatingView view)
        {
            view.NoteTruncations(path);
        }

        foreach (PropertyInfo property in ObjectsOf(value.GetType()))
        {
            Walk(property.GetValue(value), path + Snake.GetPropertyName(property.Name, false) + ".", depth + 1);
        }
    }

    // The public instance properties a view's JSON holds objects in: not strings, numbers, ids, lists or dictionaries.
    private static PropertyInfo[] ObjectsOf(Type type)
    {
        lock (Objects)
        {
            if (Objects.TryGetValue(type, out PropertyInfo[] known))
            {
                return known;
            }

            List<PropertyInfo> found = new List<PropertyInfo>();
            if (type.Namespace != null && type.Namespace.StartsWith("StationGodMCP", StringComparison.Ordinal))
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    Type held = property.PropertyType;
                    if (property.GetIndexParameters().Length == 0 && !held.IsValueType && held != typeof(string) &&
                        !typeof(IEnumerable).IsAssignableFrom(held))
                    {
                        found.Add(property);
                    }
                }
            }

            PropertyInfo[] properties = found.ToArray();
            Objects[type] = properties;
            return properties;
        }
    }
}
