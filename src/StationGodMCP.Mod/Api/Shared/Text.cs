#nullable enable

using System.Text.RegularExpressions;

namespace StationGodMCP.Api.Shared;

/// <summary>Game strings as plain text.</summary>
internal static class Text
{
    private static readonly Regex RichTextTag = new Regex("<[^>]+>", RegexOptions.Compiled);

    /// <summary>A display string without its Unity rich-text tags (colour, bold...), trimmed; null stays.</summary>
    internal static string? Plain(string? text) => text == null ? null : RichTextTag.Replace(text, string.Empty).Trim();
}
