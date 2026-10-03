using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// One file argument from a method's x-file-arguments: Name is a path on the sidecar's machine whose text is sent to
/// the game as the argument Into, so a large text (a 60 KB Lua source) never passes through the caller.
/// </summary>
internal sealed record FileArgument(string Name, string Into, string Description);

/// <summary>What reading the file arguments of a call gave: the arguments to send, or why the call is refused.</summary>
internal abstract record FileRead
{
    private FileRead()
    {
    }

    internal sealed record Ready(JsonElement Arguments) : FileRead;

    internal sealed record Refused(string Message) : FileRead;
}

/// <summary>The catalogue's file arguments, and reading them into a call.</summary>
internal static class FileArguments
{
    /// <summary>The largest file read: four times the largest Lua source the mod takes, in bytes.</summary>
    internal const int MaximumBytes = 4 * 262144;

    /// <summary>The method's file arguments from its x-file-arguments; empty when it has none.</summary>
    internal static IReadOnlyList<FileArgument> Of(JsonObject method)
    {
        if (method["x-file-arguments"] is not JsonObject declared)
        {
            return [];
        }

        List<FileArgument> files = [];
        foreach ((string name, JsonNode? node) in declared)
        {
            JsonObject entry = node!.AsObject();
            files.Add(new FileArgument(name, (string)entry["into"]!, (string)entry["description"]!));
        }

        return files;
    }

    /// <summary>
    /// The arguments with each file argument given replaced by its file's text under Into. Refused: the file argument and Into both given, a path that is not absolute, a file that cannot be
    /// read, one over MaximumBytes, or text that is not UTF-8.
    /// </summary>
    internal static FileRead Read(JsonElement arguments, IReadOnlyList<FileArgument> files)
    {
        if (files.Count == 0 || arguments.ValueKind != JsonValueKind.Object)
        {
            return new FileRead.Ready(arguments);
        }

        JsonObject? replaced = null;
        foreach (FileArgument file in files)
        {
            if (!arguments.TryGetProperty(file.Name, out JsonElement given) || given.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (arguments.TryGetProperty(file.Into, out JsonElement inline) && inline.ValueKind != JsonValueKind.Null)
            {
                return new FileRead.Refused($"Pass {file.Into} or {file.Name}, not both.");
            }

            if (given.ValueKind != JsonValueKind.String)
            {
                return new FileRead.Refused($"Argument '{file.Name}' must be a file path.");
            }

            string text;
            switch (TextOf(given.GetString()!, file.Name))
            {
                case FileText.Read read:
                    text = read.Text;
                    break;
                case FileText.Unreadable unreadable:
                    return new FileRead.Refused(unreadable.Message);
                default:
                    throw new InvalidOperationException("A file is read or unreadable.");
            }

            replaced ??= JsonObject.Create(arguments)!;
            replaced.Remove(file.Name);
            replaced[file.Into] = text;
        }

        return new FileRead.Ready(replaced == null ? arguments : JsonSerializer.SerializeToElement(replaced));
    }

    private static FileText TextOf(string path, string argument)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return new FileText.Unreadable(
                $"Argument '{argument}' must be an absolute path on the machine the MCP server runs on; '{path}' is not.");
        }

        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                return new FileText.Unreadable($"No file at '{path}' ({argument}).");
            }

            if (info.Length > MaximumBytes)
            {
                return new FileText.Unreadable(
                    $"'{path}' is {info.Length} bytes; {argument} reads at most {MaximumBytes}.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return new FileText.Read(strict.GetString(bytes, skip, bytes.Length - skip));
        }
        catch (DecoderFallbackException)
        {
            return new FileText.Unreadable($"'{path}' is not UTF-8 text ({argument}).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FileText.Unreadable($"Could not read '{path}' ({argument}): {exception.Message}");
        }
    }

    private abstract record FileText
    {
        private FileText()
        {
        }

        internal sealed record Read(string Text) : FileText;

        internal sealed record Unreadable(string Message) : FileText;
    }
}
