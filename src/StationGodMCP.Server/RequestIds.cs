using System.Text;
using System.Text.Json;

namespace StationGodMCP.Server;

/// <summary>
/// The id of a request line that is not valid JSON, read as far as the line parses, so the parse error reaches the
/// client that waits for that id (JSON-RPC's null id would leave it waiting for its timeout). Null when the id comes
/// after the fault or is not a string or number.
/// </summary>
internal static class RequestIds
{
    internal static JsonElement? Recover(string line)
    {
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(line));
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                    reader.ValueTextEquals("id") && reader.Read())
                {
                    return reader.TokenType switch
                    {
                        JsonTokenType.String => JsonSerializer.SerializeToElement(reader.GetString()),
                        JsonTokenType.Number => JsonDocument.Parse(reader.ValueSpan.ToArray()).RootElement.Clone(),
                        _ => null
                    };
                }
            }
        }
        catch (JsonException)
        {
            // The fault came before the id.
        }

        return null;
    }
}
