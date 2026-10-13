#nullable enable

using System;
using System.Collections.Concurrent;
using System.Text;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// The words the per-tool handlers' messages print for argument and tool names. A message names an argument through
/// Arg, a tool through Tool, or, as one constant text, through Text with {arg:name} and {tool:name} in it (Format for
/// a composite format string). Every one gives the name as it is; Text works a template out once and keeps it.
/// Verb handlers do not use it: their messages are written in verb words.
/// </summary>
internal static class Vocabulary
{
    /// <summary>The most templates whose text is kept; past it Text works each one out again.</summary>
    internal const int MaximumPlainTexts = 4096;

    private const string ArgumentMark = "{arg:";
    private const string ToolMark = "{tool:";

    private static readonly ConcurrentDictionary<string, string> Plain =
        new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The argument's name.</summary>
    internal static string Arg(string name) => name;

    /// <summary>The tool's name.</summary>
    internal static string Tool(string name) => name;

    /// <summary>An argument path (items[3].reference_id), as it is.</summary>
    internal static string Path(string path) => path;

    /// <summary>A constant message with {arg:name} and {tool:name} in it, the marks taken off; worked out once and kept.</summary>
    internal static string Text(string template)
    {
        if (Plain.TryGetValue(template, out string? plain))
        {
            return plain;
        }

        plain = Render(template);
        if (Plain.Count < MaximumPlainTexts)
        {
            Plain.TryAdd(template, plain);
        }

        return plain;
    }

    /// <summary>As Text, for a composite format string (string.Format).</summary>
    internal static string Format(string template) => Text(template);

    /// <summary>The template with each mark replaced by its name.</summary>
    internal static string Render(string template)
    {
        int mark = NextMark(template, 0, out int nameStart);
        if (mark < 0)
        {
            return template;
        }

        StringBuilder said = new StringBuilder(template.Length + 16);
        int copied = 0;
        while (mark >= 0)
        {
            int close = template.IndexOf('}', nameStart);
            if (close < 0)
            {
                break;
            }

            said.Append(template, copied, mark - copied);
            said.Append(template, nameStart, close - nameStart);
            copied = close + 1;
            mark = NextMark(template, copied, out nameStart);
        }

        said.Append(template, copied, template.Length - copied);
        return said.ToString();
    }

    // The next {arg: or {tool: from start, and where its name begins; -1 when there is none.
    private static int NextMark(string template, int start, out int nameStart)
    {
        int argument = template.IndexOf(ArgumentMark, start, StringComparison.Ordinal);
        int tool = template.IndexOf(ToolMark, start, StringComparison.Ordinal);
        if (argument < 0 && tool < 0)
        {
            nameStart = -1;
            return -1;
        }

        if (tool < 0 || argument >= 0 && argument < tool)
        {
            nameStart = argument + ArgumentMark.Length;
            return argument;
        }

        nameStart = tool + ToolMark.Length;
        return tool;
    }
}
