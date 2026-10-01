#nullable enable

using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The one set of serialiser settings for every reply. Views are PascalCase classes; the snake_case naming strategy
/// turns their properties into the wire names. It leaves names that are already snake_case (every anonymous object
/// the older tools still build), names given by [JsonProperty], and dictionary keys unchanged.
/// </summary>
internal static class ApiJson
{
    internal static readonly JsonSerializerSettings Settings = Create();

    // Built once from Settings and never reconfigured. Used on the main thread only (ApiHost.Serialize); the listener
    // threads' few replies (game_timeout, the TCP handshake) use WriteFresh.
    private static readonly JsonSerializer Shared = JsonSerializer.CreateDefault(Settings);

    /// <summary>
    /// A reply as JSON text, byte for byte what JsonConvert.SerializeObject(reply, Settings) writes, through one shared
    /// serializer instead of a new one per reply. Main thread only.
    /// </summary>
    internal static string WriteShared(object? reply)
    {
        StringWriter text = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture);
        using (JsonTextWriter writer = new JsonTextWriter(text))
        {
            writer.Formatting = Shared.Formatting;
            Shared.Serialize(writer, reply, null);
        }

        return text.ToString();
    }

    /// <summary>A reply as JSON text with a serializer of its own: safe on any thread.</summary>
    internal static string WriteFresh(object? reply) => JsonConvert.SerializeObject(reply, Settings);

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
