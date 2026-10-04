using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Client;

/// <summary>
/// What output_file asks for (clients.md, Output files): true names the file after the method and the time, a string
/// is the file name, false or absent answers inline, anything else is refused before the call is sent.
/// </summary>
public abstract record OutputChoice
{
    private const int MaximumLength = 80;
    private const string Extension = ".json";

    private OutputChoice()
    {
    }

    /// <summary>The choice an output_file value makes; null (absent) and JSON null answer inline.</summary>
    public static OutputChoice Of(JsonElement? value) => value?.ValueKind switch
    {
        null or JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => new Inline(),
        JsonValueKind.True => new ToFile(new OutputTarget.Auto()),
        JsonValueKind.String => Named(value.Value.GetString()!),
        _ => new Refused("Argument 'output_file' must be true, false or a file name.")
    };

    // Letters, digits, '-', '_' and '.', 1 to 80 characters after trimming, not starting with '.', no "..".
    private static OutputChoice Named(string given)
    {
        string name = given.Trim();
        bool allowed = name.Length is > 0 and <= MaximumLength && name.All(IsAllowed) && name[0] != '.' &&
                       !name.Contains("..", StringComparison.Ordinal);
        return allowed
            ? new ToFile(new OutputTarget.Named(name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension))
            : new Refused(
                $"Argument 'output_file' must be true or a plain file name (letters, digits, '-', '_' and '.', at most " +
                $"{MaximumLength} characters, not starting with '.', no folders); '{given}' is not.");
    }

    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';

    /// <summary>No file: the reply is answered inline.</summary>
    public sealed record Inline : OutputChoice;

    /// <summary>The reply goes to a file.</summary>
    public sealed record ToFile(OutputTarget Target) : OutputChoice;

    /// <summary>The value is not one output_file takes: invalid_argument, and the call is not sent.</summary>
    public sealed record Refused(string Message) : OutputChoice;
}

/// <summary>The file a reply is written to: a name made per call, or the caller's checked name with .json.</summary>
public abstract record OutputTarget
{
    private OutputTarget()
    {
    }

    internal abstract string FileName(string method);

    public sealed record Auto : OutputTarget
    {
        // <method>-<local yyyyMMdd-HHmmss-fff>-<4 hex characters>.json
        internal override string FileName(string method) =>
            string.Create(CultureInfo.InvariantCulture,
                $"{method}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}.json");
    }

    public sealed record Named(string Name) : OutputTarget
    {
        internal override string FileName(string method) => Name;
    }
}

/// <summary>
/// Where output files are written: the caller's option, else STATIONGODMCP_OUTPUT_DIR, else
/// %LOCALAPPDATA%\StationGodMCP\output. The folder is on the caller's machine, also with a remote game, so the caller
/// can always read the file. Each write keeps the folder bounded: files older than MaximumAge go, then the oldest beyond
/// MaximumFiles, never the file just written.
/// </summary>
public sealed class OutputFolder(string path)
{
    public const string EnvironmentVariable = "STATIONGODMCP_OUTPUT_DIR";
    public const int MaximumFiles = 200;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    // A top-level value whose compact JSON text is at most this many characters is repeated in the pointer's summary.
    private const int SummaryValueCharacters = 300;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public static OutputFolder Default =>
        new(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StationGodMCP", "output"));

    public static OutputFolder From(string? option, string? environment) =>
        !string.IsNullOrWhiteSpace(option) ? new OutputFolder(option.Trim())
        : !string.IsNullOrWhiteSpace(environment) ? new OutputFolder(environment.Trim())
        : Default;

    /// <summary>
    /// Writes the reply and answers the pointer {output_file, bytes, tool, counts, summary, in_file_only, truncated}. A write that
    /// fails answers the reply itself with output_file_error added: the call has run, so its reply must not be lost.
    /// </summary>
    public JsonElement Write(string method, OutputTarget target, JsonElement reply)
    {
        string file = System.IO.Path.Combine(Path, target.FileName(method));
        try
        {
            Directory.CreateDirectory(Path);
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(reply, FileJson);
            string temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, file, overwrite: true);
            Prune(file);
            return Pointer(method, file, content.LongLength, reply);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            JsonObject inline = reply.ValueKind == JsonValueKind.Object
                ? JsonObject.Create(reply)!
                : new JsonObject { ["reply"] = JsonNode.Parse(reply.GetRawText()) };
            inline["output_file_error"] = $"Could not write {file}: {exception.Message} The reply is given here instead.";
            return JsonSerializer.SerializeToElement(inline);
        }
    }

    /// <summary>The reply's notice of the lists it holds back: carried whole into the pointer, whatever its size.</summary>
    private const string TruncatedKey = "truncated";

    private static JsonElement Pointer(string method, string file, long bytes, JsonElement reply)
    {
        JsonObject counts = new();
        JsonObject summary = new();
        JsonArray fileOnly = new();
        JsonNode? truncated = null;
        if (reply.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in reply.EnumerateObject())
            {
                if (property.Name == TruncatedKey)
                {
                    truncated = JsonNode.Parse(property.Value.GetRawText());
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    counts[property.Name] = property.Value.GetArrayLength();
                }
                else if (JsonSerializer.Serialize(property.Value, CompactJson).Length <= SummaryValueCharacters)
                {
                    summary[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
                else
                {
                    fileOnly.Add(property.Name);
                }
            }
        }

        JsonObject pointer = new()
        {
            ["output_file"] = file,
            ["bytes"] = bytes,
            ["tool"] = method,
            ["counts"] = counts,
            ["summary"] = summary
        };
        if (fileOnly.Count > 0)
        {
            pointer["in_file_only"] = fileOnly;
        }

        if (truncated != null)
        {
            pointer[TruncatedKey] = truncated;
        }

        return JsonSerializer.SerializeToElement(pointer);
    }

    // Another client may write or prune the same folder at the same time: a file that is gone or held is left alone.
    private void Prune(string kept)
    {
        DateTime cutoff = DateTime.UtcNow - MaximumAge;
        FileInfo[] newestFirst = new DirectoryInfo(Path).GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        IEnumerable<FileInfo> expired = newestFirst
            .Where((file, index) => index >= MaximumFiles || file.LastWriteTimeUtc < cutoff)
            .Where(file => !string.Equals(file.FullName, kept, StringComparison.OrdinalIgnoreCase));
        foreach (FileInfo file in expired)
        {
            try
            {
                file.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held by a reader or already pruned by another client.
            }
        }
    }
}
