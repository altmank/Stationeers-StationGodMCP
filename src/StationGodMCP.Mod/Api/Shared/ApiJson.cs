#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StationGodMCP.Pure.Shaping;

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
    /// A reply as JSON text, byte for byte what JsonConvert.SerializeObject(reply, Settings) writes, through one shared
    /// serializer instead of a new one per reply. Main thread only.
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
    /// (the kept jobs' size estimate, KeptJobSizes.Final). Main thread only.
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
    /// A reply envelope as JSON text with its result shaped as it is written (ShapingJsonWriter). Without anything to
    /// leave out the text is byte for byte WriteShared's. Main thread only.
    /// </summary>
    internal static ShapedText WriteShaped(object reply, ShapeRequest shape) => WriteShaped(Serializer, reply, shape, ShapingRoot.Envelope);

    /// <summary>A call's reply: shaped, with the handler's truncation notes, and truncated always written.</summary>
    internal static ShapedText WriteReply(object reply, ShapeRequest shape, IReadOnlyList<Truncation> truncations) =>
        WriteShaped(Serializer, reply, shape, ShapingRoot.Envelope, truncations, announce: true);

    /// <summary>
    /// As WriteShaped, through the given serializer and with the result where root says. When fields selectors matched
    /// nothing and some have exactly one close key in the reply (FieldMapping), the reply is written once more with
    /// those read as that key, and says so in fields_mapped. nearMisses false writes it once, as given, without
    /// fields_closest.
    /// </summary>
    internal static ShapedText WriteShaped(JsonSerializer serializer, object? value, ShapeRequest shape, ShapingRoot root,
        IReadOnlyList<Truncation>? truncations = null, bool announce = false, bool nearMisses = true)
    {
        ShapedText written = WriteOnce(serializer, value, shape, root, truncations, announce, nearMisses);
        ShapeRequest? mapped = nearMisses ? FieldMapping.Remap(shape, written.Outcome) : null;
        return mapped == null ? written : WriteOnce(serializer, value, mapped, root, truncations, announce, nearMisses);
    }

    private static ShapedText WriteOnce(JsonSerializer serializer, object? value, ShapeRequest shape, ShapingRoot root,
        IReadOnlyList<Truncation>? truncations, bool announce, bool nearMisses)
    {
        StringWriter text = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture);
        ShapeOutcome outcome;
        using (JsonTextWriter inner = WriterLike(serializer, text))
        {
            ShapingJsonWriter shaping = new ShapingJsonWriter(inner, shape, root, truncations, announce, nearMisses);
            shaping.Formatting = serializer.Formatting;
            serializer.Serialize(shaping, value, null);
            shaping.Flush();
            outcome = shaping.Outcome;
        }

        return new ShapedText(text.ToString(), outcome);
    }

    // The settings the serializer sets on a writer it is given, set here on the writer that formats behind the
    // shaping one, so both write the same text.
    private static JsonTextWriter WriterLike(JsonSerializer serializer, TextWriter text) => new JsonTextWriter(text)
    {
        Formatting = serializer.Formatting,
        Culture = serializer.Culture,
        DateFormatHandling = serializer.DateFormatHandling,
        DateTimeZoneHandling = serializer.DateTimeZoneHandling,
        DateFormatString = serializer.DateFormatString,
        FloatFormatHandling = serializer.FloatFormatHandling,
        StringEscapeHandling = serializer.StringEscapeHandling
    };

    /// <summary>A reply as JSON text with a serializer of its own: safe on any thread.</summary>
    internal static string WriteFresh(object? reply) => JsonConvert.SerializeObject(reply, Settings);

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
