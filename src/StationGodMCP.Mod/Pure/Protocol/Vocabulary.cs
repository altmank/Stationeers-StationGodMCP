#nullable enable

using System;
using System.Collections.Concurrent;
using System.Text;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// The words the mod's messages print for argument and tool names: the running call's rename map (RenameMap) when it
/// sent one, else the names themselves. A message names an argument through Arg, a tool through Tool, or, as one
/// constant text, through Text with {arg:name} and {tool:name} in it. Without a map every one gives today's text: Arg
/// and Tool return the name they were given, and Text a text worked out once per template; so a call without a map
/// allocates nothing more than before.
/// <para>
/// Current is thread-static, as CallContext is: ApiHost enters the call's map around the handler and the reply's
/// serialisation on the main thread, and the connection's reader thread enters it around the full argument check.
/// A thread never sees another call's map, and work outside a call (a later frame's job step) prints names as they are.
/// </para>
/// </summary>
internal static class Vocabulary
{
    /// <summary>The most templates whose map-less text is kept; past it Text works each one out again.</summary>
    internal const int MaximumPlainTexts = 4096;

    private const string ArgumentMark = "{arg:";
    private const string ToolMark = "{tool:";

    [ThreadStatic]
    private static RenameMap? _current;

    private static readonly ConcurrentDictionary<string, string> Plain =
        new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The map of the call running on this thread; null when it sent none, or outside a call.</summary>
    internal static RenameMap? Current => _current;

    /// <summary>Makes map current until the returned scope is disposed, which restores the one before.</summary>
    internal static Scope Enter(RenameMap? map)
    {
        RenameMap? previous = _current;
        _current = map;
        return new Scope(previous);
    }

    /// <summary>The argument as the caller says it.</summary>
    internal static string Arg(string name)
    {
        RenameMap? map = _current;
        return map == null ? name : map.Argument(name);
    }

    /// <summary>The tool as the caller says it.</summary>
    internal static string Tool(string name)
    {
        RenameMap? map = _current;
        return map == null ? name : map.Tool(name);
    }

    /// <summary>
    /// An argument path (items[3].reference_id) with each name as the caller says it; the indices stay.
    /// </summary>
    internal static string Path(string path)
    {
        RenameMap? map = _current;
        if (map == null || path.Length == 0)
        {
            return path;
        }

        StringBuilder said = new StringBuilder(path.Length + 16);
        int start = 0;
        while (start <= path.Length)
        {
            int dot = path.IndexOf('.', start);
            int end = dot < 0 ? path.Length : dot;
            int bracket = path.IndexOf('[', start, end - start);
            int nameEnd = bracket < 0 ? end : bracket;
            if (start > 0)
            {
                said.Append('.');
            }

            said.Append(map.Argument(path.Substring(start, nameEnd - start)));
            said.Append(path, nameEnd, end - nameEnd);
            if (dot < 0)
            {
                break;
            }

            start = dot + 1;
        }

        return said.ToString();
    }

    /// <summary>
    /// A constant message with {arg:name} and {tool:name} in it, as the caller says the names. Without a map it is the
    /// template with the marks taken off, worked out once and kept.
    /// </summary>
    internal static string Text(string template)
    {
        RenameMap? map = _current;
        if (map != null)
        {
            return Render(template, map);
        }

        if (Plain.TryGetValue(template, out string? plain))
        {
            return plain;
        }

        plain = Render(template, null);
        if (Plain.Count < MaximumPlainTexts)
        {
            Plain.TryAdd(template, plain);
        }

        return plain;
    }

    /// <summary>The template with each mark replaced by its name as map says it (as it is, for no map).</summary>
    internal static string Render(string template, RenameMap? map)
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

            string name = template.Substring(nameStart, close - nameStart);
            bool tool = template[mark + 1] == 't';
            said.Append(template, copied, mark - copied);
            said.Append(map == null ? name : tool ? map.Tool(name) : map.Argument(name));
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

    /// <summary>Restores the map that was current before Enter; a struct, so a using block allocates nothing.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly RenameMap? _previous;

        internal Scope(RenameMap? previous) => _previous = previous;

        public void Dispose() => _current = _previous;
    }
}
