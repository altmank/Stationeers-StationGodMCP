#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// One enum member by its declared name, ignoring case and surrounding spaces. Enum.TryParse is not a name lookup:
/// it takes "Error,PressureInternal" and ORs the two values into a third member, and takes "4" as a value. A tool
/// argument that names a member must name exactly one.
/// </summary>
internal static class EnumName
{
    internal static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        string? name = text?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            foreach (string declared in Names<TEnum>.All)
            {
                if (string.Equals(declared, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = (TEnum)Enum.Parse(typeof(TEnum), declared);
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static class Names<TEnum> where TEnum : struct, Enum
    {
        internal static readonly string[] All = Enum.GetNames(typeof(TEnum));
    }
}
