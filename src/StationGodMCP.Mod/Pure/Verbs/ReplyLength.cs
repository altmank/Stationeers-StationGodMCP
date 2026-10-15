#nullable enable

using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace StationGodMCP.Pure.Verbs;

/// <summary>How long a value's JSON text is, counted as it is written: no text is kept.</summary>
internal static class ReplyLength
{
    internal static long Utf8Bytes(object? value, JsonSerializer serializer)
    {
        Utf8Count count = new Utf8Count();
        Write(value, serializer, count);
        return count.Bytes;
    }

    internal static long Characters(object? value, JsonSerializer serializer)
    {
        Utf8Count count = new Utf8Count();
        Write(value, serializer, count);
        return count.Characters;
    }

    private static void Write(object? value, JsonSerializer serializer, TextWriter text)
    {
        using JsonTextWriter writer = new JsonTextWriter(text) { Formatting = serializer.Formatting, CloseOutput = false };
        serializer.Serialize(writer, value);
    }
}

/// <summary>
/// A text writer that keeps only counts: characters, and the bytes they take in UTF-8 (a surrogate pair is four bytes,
/// two per half).
/// </summary>
internal class Utf8Count : TextWriter
{
    internal long Bytes { get; private set; }

    internal long Characters { get; private set; }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        Characters++;
        Bytes += BytesOf(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        Characters += count;
        for (int at = index; at < index + count; at++)
        {
            Bytes += BytesOf(buffer[at]);
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
        {
            return;
        }

        Characters += value.Length;
        foreach (char character in value)
        {
            Bytes += BytesOf(character);
        }
    }

    // A high or low surrogate half is two of its pair's four bytes; a lone half, written as U+FFFD, would be three
    // (counted as two here: the replies' text is the game's names and the mod's own, never lone halves).
    internal static int BytesOf(char character) => character switch
    {
        < (char)0x80 => 1,
        < (char)0x800 => 2,
        >= (char)0xD800 and <= (char)0xDFFF => 2,
        _ => 3
    };
}
