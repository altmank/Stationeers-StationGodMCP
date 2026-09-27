#nullable enable

using System.Globalization;
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
