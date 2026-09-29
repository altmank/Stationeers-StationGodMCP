#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// What set_ic_source changes in, or notes about, an IC10 source before the chip gets it. The chip stores its source
/// as ASCII (ProgrammableChip.SourceCode is an AsciiString: every other character becomes '?') but compiles the
/// string it was handed, so a non-ASCII source would run one program and save another; the tool hands the chip the
/// ASCII text it stores. A CRLF, and a lone CR (old Mac line ends), becomes LF: the chip splits lines on '\n' only,
/// so it would keep the '\r' in each CRLF line and read a CR-only source as one line.
/// The chip runs any length, but the in-game editor (InputSourceCode: MAX_LINES 128, LINE_LENGTH_LIMIT 90,
/// MAX_FILE_SIZE 4096) cuts a longer source when a player opens and submits it, so those are warnings.
/// </summary>
internal static class Ic10Source
{
    internal const int EditorMaximumLines = 128;
    internal const int EditorLineLength = 90;
    internal const int EditorMaximumCharacters = 4096;

    /// <summary>How many long lines over_editor_line_length names; past that it counts them.</summary>
    internal const int NamedLongLines = 10;

    /// <summary>The source with CRLF and lone CR as LF.</summary>
    internal static string WithUnixLineEnds(string source) => source.Replace("\r\n", "\n").Replace('\r', '\n');

    // A CR that does not start a CRLF.
    private static bool HasLoneCr(string source) => source.Replace("\r\n", "\n").IndexOf('\r') >= 0;

    internal static bool HasNonAscii(string source)
    {
        foreach (char character in source)
        {
            if (character > '\u007F')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The notes on a source as given (original) and as the chip gets it (stored).</summary>
    internal static List<SourceNote> Notes(string original, string stored)
    {
        List<SourceNote> notes = new List<SourceNote>();
        if (original.Contains("\r\n"))
        {
            notes.Add(new SourceNote("crlf_normalised",
                "CRLF line ends were stored as LF; the chip splits lines on LF only."));
        }

        if (HasLoneCr(original))
        {
            notes.Add(new SourceNote("cr_normalised",
                "CR line ends (without LF) were stored as LF; the chip splits lines on LF only and would read the " +
                "text as one line."));
        }

        if (HasNonAscii(original))
        {
            notes.Add(new SourceNote("non_ascii_replaced",
                "The chip stores its source as ASCII: each non-ASCII character was replaced by '?', and the chip " +
                "runs that text (as it would after a save and load)."));
        }

        string[] lines = stored.Split('\n');
        if (lines.Length > EditorMaximumLines)
        {
            notes.Add(new SourceNote("over_editor_lines",
                $"{Count(lines.Length)} lines; the in-game editor shows {EditorMaximumLines} and drops the rest " +
                "when a player submits it. The chip runs them all."));
        }

        List<int> longLines = LongLines(lines);
        if (longLines.Count > 0)
        {
            notes.Add(new SourceNote("over_editor_line_length",
                $"{LineList(longLines)} longer than the in-game editor's {EditorLineLength} characters; the editor " +
                "cuts such lines when a player edits them. The chip runs them whole."));
        }

        if (stored.Length > EditorMaximumCharacters)
        {
            notes.Add(new SourceNote("over_editor_size",
                $"{Count(stored.Length)} characters; the in-game editor refuses to submit more than " +
                $"{EditorMaximumCharacters} and cuts the source to that when it opens it. The chip runs it whole."));
        }

        return notes;
    }

    private static List<int> LongLines(string[] lines)
    {
        List<int> longLines = new List<int>();
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length > EditorLineLength)
            {
                longLines.Add(index);
            }
        }

        return longLines;
    }

    // "Line 4 (0-based) is", "Lines 0, 1 (0-based) are", or past NamedLongLines the count and the first of them.
    private static string LineList(List<int> longLines)
    {
        if (longLines.Count == 1)
        {
            return $"Line {Count(longLines[0])} (0-based) is";
        }

        int named = Math.Min(longLines.Count, NamedLongLines);
        string list = string.Join(", ", longLines.GetRange(0, named).ConvertAll(Count));
        return longLines.Count > named
            ? $"{Count(longLines.Count)} lines (0-based, the first {named}: {list}) are"
            : $"Lines {list} (0-based) are";
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Something set_ic_source changed in a source, or would be lost to the in-game editor.</summary>
internal sealed class SourceNote
{
    internal SourceNote(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }
}
