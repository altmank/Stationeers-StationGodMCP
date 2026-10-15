#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// One enum member by its declared name, ignoring case and surrounding spaces. Enum.TryParse is not a name lookup:
/// it takes "Error,PressureInternal" and ORs the two values into a third member, and takes "4" as a value. A tool
/// argument that names a member must name exactly one. The names are one dictionary per enum, built once (cost design
/// A1: no per-name scan, no Enum.Parse per call).
/// </summary>
internal static class EnumName
{
    internal static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        string? name = text?.Trim();
        if (!string.IsNullOrEmpty(name) && Names<TEnum>.ByName.TryGetValue(name!, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static class Names<TEnum> where TEnum : struct, Enum
    {
        internal static readonly Dictionary<string, TEnum> ByName = Build();

        // Declared order: of two names differing only in case, the first declared answers, as the scan this replaces did.
        private static Dictionary<string, TEnum> Build()
        {
            string[] names = Enum.GetNames(typeof(TEnum));
            Dictionary<string, TEnum> byName =
                new Dictionary<string, TEnum>(names.Length, StringComparer.OrdinalIgnoreCase);
            foreach (string declared in names)
            {
                if (!byName.ContainsKey(declared))
                {
                    byName.Add(declared, (TEnum)Enum.Parse(typeof(TEnum), declared));
                }
            }

            return byName;
        }
    }
}
