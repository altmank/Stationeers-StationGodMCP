#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The one set of serialiser settings for every reply. Views are PascalCase classes; the snake_case naming strategy
/// turns their properties into the wire names. It leaves names that are already snake_case (every anonymous object
/// the older tools still build), names given by [JsonProperty], and dictionary keys unchanged.
/// </summary>
internal static class ApiJson
{
    internal static readonly JsonSerializerSettings Settings = Create();

    [ThreadStatic]
    private static JsonSerializer? _serializer;

    /// <summary>
    /// This thread's serializer over Settings, built once per thread and never reconfigured: the game thread's, each
    /// connection writer's. A serializer is not shared between threads; one is never made per call.
    /// </summary>
    internal static JsonSerializer Serializer => _serializer ??= JsonSerializer.CreateDefault(Settings);

    /// <summary>
    /// A reply as JSON text, byte for byte what JsonConvert.SerializeObject(reply, Settings) writes, through the writing
    /// thread's serializer instead of a new one per reply. Any thread.
    /// </summary>
    internal static string WriteShared(object? reply)
    {
        StringWriter text = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture);
        using (JsonTextWriter writer = new JsonTextWriter(text))
        {
            writer.Formatting = Serializer.Formatting;
            Serializer.Serialize(writer, reply, null);
        }

        return text.ToString();
    }

    /// <summary>
    /// How many characters WriteShared would write for value, counted as they are written, so the text is never built
    /// (the kept jobs' size estimate, KeptJobSizes.Final). Any thread.
    /// </summary>
    internal static long Length(object? value)
    {
        CharacterCount count = new CharacterCount();
        using (JsonTextWriter writer = new JsonTextWriter(count))
        {
            writer.Formatting = Serializer.Formatting;
            Serializer.Serialize(writer, value, null);
        }

        return count.Characters;
    }

    /// <summary>
    /// A call's reply envelope written once through serializer (the writing thread's), its result shaped as it is
    /// written (ShapingJsonWriter: the handler's truncation notes, truncated always written), its UTF-8 size counted as
    /// it goes and its text no longer kept once it passes limit bytes (the count goes on, for reply_too_large). When
    /// fields selectors matched nothing and some have exactly one close key in the reply (FieldMapping), it is written
    /// once more with those read as that key, and says so in fields_mapped: a streaming write cannot know a key it
    /// passed was the one meant until the reply ends.
    /// </summary>
    internal static CappedText WriteReply(JsonSerializer serializer, object reply, ShapeRequest shape,
        IReadOnlyList<Truncation> truncations, long limit)
    {
        CappedText written = WriteOnce(serializer, reply, shape, ShapingRoot.Envelope, truncations, true, true, limit);
        ShapeRequest? mapped = FieldMapping.Remap(shape, written.Outcome);
        return mapped == null
            ? written
            : WriteOnce(serializer, reply, mapped, ShapingRoot.Envelope, truncations, true, true, limit);
    }

    /// <summary>
    /// A value as JSON text shaped as it is written, through the given serializer and with the result where root says,
    /// with truncation notes (announce: truncated always written); no size cap; the near-miss mapping as WriteReply.
    /// nearMisses false writes it once, as given, without fields_closest.
    /// </summary>
    internal static ShapedText WriteShaped(JsonSerializer serializer, object? value, ShapeRequest shape, ShapingRoot root,
        IReadOnlyList<Truncation>? truncations = null, bool announce = false, bool nearMisses = true)
    {
        CappedText written = WriteOnce(serializer, value, shape, root, truncations, announce, nearMisses, long.MaxValue);
        ShapeRequest? mapped = nearMisses ? FieldMapping.Remap(shape, written.Outcome) : null;
        if (mapped != null)
        {
            written = WriteOnce(serializer, value, mapped, root, truncations, announce, nearMisses, long.MaxValue);
        }

        return new ShapedText(written.Json!, written.Outcome);
    }

    private static CappedText WriteOnce(JsonSerializer serializer, object? value, ShapeRequest shape, ShapingRoot root,
        IReadOnlyList<Truncation>? truncations, bool announce, bool nearMisses, long limit)
    {
        CappedText text = new CappedText(limit);
        using (JsonTextWriter inner = WriterLike(serializer, text))
        {
            ShapingJsonWriter shaping = new ShapingJsonWriter(inner, shape, root, truncations, announce, nearMisses);
            shaping.Formatting = serializer.Formatting;
            serializer.Serialize(shaping, value, null);
            shaping.Flush();
            text.Outcome = shaping.Outcome;
        }

        return text;
    }

    // The settings the serializer sets on a writer it is given, set here on the writer that formats behind the
    // shaping one, so both write the same text.
    private static JsonTextWriter WriterLike(JsonSerializer serializer, TextWriter text) => new JsonTextWriter(text)
    {
        CloseOutput = false,
        Formatting = serializer.Formatting,
        Culture = serializer.Culture,
        DateFormatHandling = serializer.DateFormatHandling,
        DateTimeZoneHandling = serializer.DateTimeZoneHandling,
        DateFormatString = serializer.DateFormatString,
        FloatFormatHandling = serializer.FloatFormatHandling,
        StringEscapeHandling = serializer.StringEscapeHandling
    };

    // A text writer that keeps only how many characters it was given.
    private sealed class CharacterCount : TextWriter
    {
        internal long Characters { get; private set; }

        public override Encoding Encoding => Encoding.Unicode;

        public override void Write(char value) => Characters++;

        public override void Write(char[] buffer, int index, int count) => Characters += count;

        public override void Write(string? value) => Characters += value?.Length ?? 0;
    }

    private static JsonSerializerSettings Create()
    {
        JsonSerializerSettings settings = new JsonSerializerSettings
        {
            FloatFormatHandling = FloatFormatHandling.String,
            Culture = CultureInfo.InvariantCulture,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = false
                }
            }
        };
        settings.Converters.Add(new ThingIdJsonConverter());
        return settings;
    }
}

/// <summary>A shaped reply's text, and what shaping saw while writing it (list lengths for reply_too_large).</summary>
internal sealed class ShapedText
{
    internal ShapedText(string json, ShapeOutcome outcome)
    {
        Json = json;
        Outcome = outcome;
    }

    internal string Json { get; }

    internal ShapeOutcome Outcome { get; }
}

/// <summary>
/// A reply's text as it is written, kept until it passes limit UTF-8 bytes and then only counted (the size and the
/// list lengths reply_too_large reports); Json is null once it is over.
/// </summary>
internal sealed class CappedText : Utf8Count
{
    private readonly long _limit;
    private StringBuilder? _text = new StringBuilder(256);

    internal CappedText(long limit) => _limit = limit;

    internal bool Over => _text == null;

    internal string? Json => _text?.ToString();

    internal ShapeOutcome Outcome { get; set; } = new ShapeOutcome(0);

    public override void Write(char value)
    {
        base.Write(value);
        _text?.Append(value);
        Check();
    }

    public override void Write(char[] buffer, int index, int count)
    {
        base.Write(buffer, index, count);
        _text?.Append(buffer, index, count);
        Check();
    }

    public override void Write(string? value)
    {
        base.Write(value);
        _text?.Append(value);
        Check();
    }

    private void Check()
    {
        if (_text != null && Bytes > _limit)
        {
            _text = null;
        }
    }
}
