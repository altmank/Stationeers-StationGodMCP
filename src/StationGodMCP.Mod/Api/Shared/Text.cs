#nullable enable

using System.Collections.Concurrent;
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

    private const int RememberedNames = 512;

    private static readonly ConcurrentDictionary<string, string> PlainNames =
        new ConcurrentDictionary<string, string>(System.StringComparer.Ordinal);

    /// <summary>
    /// Plain, remembered per raw text, for the few short names read again and again (a gas's display name on every
    /// atmosphere read). The raw text is the key, so a language change is new text and a new entry; at most
    /// RememberedNames are kept, past that the rest are worked out each time.
    /// </summary>
    internal static string? PlainName(string? text)
    {
        if (text == null)
        {
            return null;
        }

        if (PlainNames.TryGetValue(text, out string? known))
        {
            return known;
        }

        string plain = Plain(text)!;
        if (PlainNames.Count < RememberedNames)
        {
            PlainNames.TryAdd(text, plain);
        }

        return plain;
    }

    /// <summary>
    /// A trader condition's DebugName as plain text. The game's GasCondition builds it with an interpolated string
    /// ending in "{Percent}%%", which C# does not collapse, so a literal "%%" is shown as "%".
    /// </summary>
    internal static string? Condition(string? text) => Plain(text)?.Replace("%%", "%");
}
