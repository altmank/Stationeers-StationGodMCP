#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// A call's rename map (protocol.md, the call key rename): how its caller says the handler's argument names and the
/// tool names the mod's messages point to. Params still arrive in handler names; the map changes only what the mod
/// prints (Vocabulary). Each entry maps a handler name to the caller's phrase, so a merge maps several names to one
/// phrase (min and max to box) and a phrase need not be a name (near_player_m to near: "player" + radius). A name the
/// map does not hold prints as it is. Read on the connection's reader thread; immutable after.
/// </summary>
internal sealed class RenameMap
{
    internal const int MaximumEntries = 256;
    internal const int MaximumNameLength = 64;
    internal const int MaximumPhraseLength = 128;

    private const string ArgumentsKey = "args";
    private const string ToolsKey = "tools";

    private readonly Dictionary<string, string> _arguments;
    private readonly Dictionary<string, string> _tools;

    internal RenameMap(Dictionary<string, string> arguments, Dictionary<string, string> tools)
    {
        _arguments = arguments;
        _tools = tools;
    }

    /// <summary>How the caller says the argument: its phrase, or the name itself when the map has none.</summary>
    internal string Argument(string name) => _arguments.TryGetValue(name, out string? said) ? said : name;

    /// <summary>How the caller says the tool: its phrase, or the name itself when the map has none.</summary>
    internal string Tool(string name) => _tools.TryGetValue(name, out string? said) ? said : name;

    /// <summary>
    /// The map a call's rename key holds: null for none (absent, null or no entries), and null with problem set when
    /// the key cannot be used (the call is then refused invalid_argument).
    /// </summary>
    internal static RenameMap? Read(JToken? token, out string? problem)
    {
        problem = null;
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (!(token is JObject given))
        {
            problem = "rename must be an object: {\"args\": {name: phrase}, \"tools\": {name: phrase}}.";
            return null;
        }

        Dictionary<string, string> arguments = new Dictionary<string, string>(System.StringComparer.Ordinal);
        Dictionary<string, string> tools = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (JProperty part in given.Properties())
        {
            Dictionary<string, string>? into =
                part.Name == ArgumentsKey ? arguments : part.Name == ToolsKey ? tools : null;
            if (into == null)
            {
                problem = $"rename takes {ArgumentsKey} and {ToolsKey} only; '{part.Name}' is neither.";
                return null;
            }

            problem = ReadEntries(part, into, arguments.Count + tools.Count);
            if (problem != null)
            {
                return null;
            }
        }

        return arguments.Count + tools.Count == 0 ? null : new RenameMap(arguments, tools);
    }

    private static string? ReadEntries(JProperty part, Dictionary<string, string> into, int heldBefore)
    {
        if (part.Value.Type == JTokenType.Null)
        {
            return null;
        }

        if (!(part.Value is JObject entries))
        {
            return $"rename.{part.Name} must be an object of name: phrase.";
        }

        int held = heldBefore;
        foreach (JProperty entry in entries.Properties())
        {
            if (++held > MaximumEntries)
            {
                return $"rename holds more than {MaximumEntries} entries.";
            }

            if (!IsName(entry.Name))
            {
                return $"rename.{part.Name} key '{entry.Name}' is not a catalogue name (a lower-case letter, then " +
                       $"lower-case letters, digits or '_', up to {MaximumNameLength} characters).";
            }

            string? phrase = entry.Value.Type == JTokenType.String ? (string)entry.Value! : null;
            if (phrase == null || !IsPhrase(phrase))
            {
                return $"rename.{part.Name}.{entry.Name} must be a string of 1 to {MaximumPhraseLength} characters " +
                       "without control characters.";
            }

            into[entry.Name] = phrase;
        }

        return null;
    }

    private static bool IsName(string name)
    {
        if (name.Length == 0 || name.Length > MaximumNameLength || name[0] < 'a' || name[0] > 'z')
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPhrase(string phrase)
    {
        if (phrase.Length == 0 || phrase.Length > MaximumPhraseLength)
        {
            return false;
        }

        foreach (char character in phrase)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
