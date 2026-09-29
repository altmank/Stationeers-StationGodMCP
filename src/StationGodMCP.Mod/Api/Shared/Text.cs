#nullable enable

using System.Text.RegularExpressions;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>Game strings as plain text.</summary>
internal static class Text
{
    private static readonly Regex RichTextTag = new Regex("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// A display string without its Unity rich-text tags (colour, bold...), trimmed; null stays. The game's
    /// "&lt;N:EN:Key&gt;" placeholder for a name missing from the language file is replaced by its key first
    /// (ThingName.Resolved), or stripping would take it for a tag and leave the name out.
    /// </summary>
    internal static string? Plain(string? text) =>
        text == null ? null : RichTextTag.Replace(ThingName.Resolved(text), string.Empty).Trim();

    /// <summary>
    /// A trader condition's DebugName as plain text. The game's GasCondition builds it with an interpolated string
    /// ending in "{Percent}%%", which C# does not collapse, so a literal "%%" is shown as "%".
    /// </summary>
    internal static string? Condition(string? text) => Plain(text)?.Replace("%%", "%");
}
