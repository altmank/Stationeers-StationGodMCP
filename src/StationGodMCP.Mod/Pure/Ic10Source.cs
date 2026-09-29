#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// What set_ic_source changes in, or notes about, an IC10 source before the chip gets it. The chip stores its source
/// as ASCII (ProgrammableChip.SourceCode is an AsciiString: every other character becomes '?') but compiles the
/// string it was handed, so a non-ASCII source would run one program and save another; the tool hands the chip the
/// ASCII text it stores. A CRLF becomes LF: the chip splits lines on '\n' only and would keep the '\r' in each line.
/// The chip runs any length, but the in-game editor (InputSourceCode: MAX_LINES 128, LINE_LENGTH_LIMIT 90,
/// MAX_FILE_SIZE 4096) cuts a longer source when a player opens and submits it, so those are warnings.
/// </summary>
internal static class Ic10Source
{
    internal const int EditorMaximumLines = 128;
    internal const int EditorLineLength = 90;
    internal const int EditorMaximumCharacters = 4096;

    /// <summary>The source with CRLF as LF.</summary>
    internal static string WithUnixLineEnds(string source) => source.Replace("\r\n", "\n");

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

        int longest = FirstLongLine(lines);
        if (longest >= 0)
        {
            notes.Add(new SourceNote("over_editor_line_length",
                $"Line {Count(longest)} (0-based) is longer than the in-game editor's {EditorLineLength} " +
                "characters; the editor cuts such lines when a player edits them. The chip runs them whole."));
        }

        if (stored.Length > EditorMaximumCharacters)
        {
            notes.Add(new SourceNote("over_editor_size",
                $"{Count(stored.Length)} characters; the in-game editor refuses to submit more than " +
                $"{EditorMaximumCharacters} and cuts the source to that when it opens it. The chip runs it whole."));
        }

        return notes;
    }

    private static int FirstLongLine(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length > EditorLineLength)
            {
                return index;
            }
        }

        return -1;
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
