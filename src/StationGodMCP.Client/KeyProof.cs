using System.Security.Cryptography;
using System.Text;

namespace StationGodMCP.Client;

/// <summary>
/// The proof that a client holds its key (protocol.md, Proving a key): lowercase hex HMAC-SHA256, keyed by the
/// base64-decoded key, over the UTF-8 of "stationgod-v2\n" + nonce + "\n" + client + "\n" + transport, with the nonce
/// as the base64 text received. The key itself never crosses the wire.
/// </summary>
public static class KeyProof
{
    /// <summary>The proof, or null when the key is not base64.</summary>
    public static string? Of(string keyBase64, string nonce, string client, string transport)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        byte[] message = Encoding.UTF8.GetBytes($"stationgod-v2\n{nonce}\n{client}\n{transport}");
        return Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant();
    }
}
