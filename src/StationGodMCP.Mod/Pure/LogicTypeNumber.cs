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
}
