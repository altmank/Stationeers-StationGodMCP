#nullable enable

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>
/// The embedded catalogue.json: its exact bytes' identity (welcome's catalogue.hash, SHA-256 of the file as built, so
/// every language computes the same), its text (the protocol method catalogue returns it), and the loaded catalogue.
/// </summary>
internal sealed class CatalogueFile
{
    internal CatalogueFile(byte[] bytes, Catalogue catalogue)
    {
        Text = new UTF8Encoding(false).GetString(bytes);
        Hash = HashOf(bytes);
        Catalogue = catalogue;
    }

    internal string Text { get; }

    /// <summary>The same catalogue as one line of compact JSON, to send inside a message.</summary>
    internal string CompactText => _compact ??= Compact(Text);

    private string? _compact;

    private static string Compact(string json)
    {
        using JsonTextReader reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal
        };
        return JToken.ReadFrom(reader).ToString(Formatting.None);
    }

    /// <summary>"sha256:" and the lowercase hex of the file's SHA-256.</summary>
    internal string Hash { get; }

    internal Catalogue Catalogue { get; }

    internal static string HashOf(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(bytes);
        StringBuilder hex = new StringBuilder("sha256:", 7 + digest.Length * 2);
        foreach (byte value in digest)
        {
            hex.Append(value.ToString("x2"));
        }

        return hex.ToString();
    }
}
