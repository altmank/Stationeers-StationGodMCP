#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// A logic_type given as a JSON number: a LogicType id is a ushort, so only a whole number from 0 to 65535 is one.
/// A number past a long (1e19) or with a fraction is still a number, never a name to look up.
/// </summary>
internal static class LogicTypeNumber
{
    internal const string RangeMessage = "Numeric logic_type must be a whole number from 0 to 65535.";

    internal static bool TryId(double value, out ushort id)
    {
        bool fits = value >= ushort.MinValue && value <= ushort.MaxValue && value == System.Math.Floor(value);
        id = fits ? (ushort)value : (ushort)0;
        return fits;
    }

    /// <summary>
    /// Text written as a number ("65536", "-1", "1e19", "12.5") that is no ushort id: it gets RangeMessage, not the
    /// unknown-name message. A name never starts with a digit, a sign or a point; checked by characters, so a
    /// runtime's double overflow rules do not matter.
    /// </summary>
    internal static bool IsNumberText(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || !(char.IsDigit(trimmed[0]) || NumberSigns.IndexOf(trimmed[0]) >= 0))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (!char.IsDigit(c) && NumberSigns.IndexOf(c) < 0 && c != 'e' && c != 'E')
            {
                return false;
            }
        }

        return true;
    }

    private const string NumberSigns = "+-.";
}
