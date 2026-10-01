using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// Where output_file replies are written: --output-dir, else STATIONGODMCP_OUTPUT_DIR, else
/// %LOCALAPPDATA%\StationGodMCP\output (beside the installed sidecar's server folder). The sidecar runs on the agent's
/// machine, also with a remote game, so the agent can always read the file. Each write keeps the folder bounded: files
/// older than MaximumAge go, then the oldest beyond MaximumFiles.
/// </summary>
internal sealed class OutputFolder(string path)
{
    internal const string EnvironmentVariable = "STATIONGODMCP_OUTPUT_DIR";
    internal const string CommandLineOption = "--output-dir";
    internal const int MaximumFiles = 200;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    // Short scalars and small objects of the reply are repeated in the pointer's summary; anything larger stays in the file.
    private const int SummaryValueBytes = 300;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal string Path { get; } = System.IO.Path.GetFullPath(path);

    internal static OutputFolder Default =>
        new(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StationGodMCP", "output"));

    internal static OutputFolder From(string? option, string? environment) =>
        !string.IsNullOrWhiteSpace(option) ? new OutputFolder(option.Trim())
        : !string.IsNullOrWhiteSpace(environment) ? new OutputFolder(environment.Trim())
        : Default;

    /// <summary>
    /// Writes the reply and answers the pointer {output_file, bytes, tool, counts, summary, in_file_only}. A write that
    /// fails answers the reply itself with output_file_error added: the call has run, so its reply must not be lost.
    /// </summary>
    internal JsonElement Write(string tool, OutputTarget target, JsonElement reply)
    {
        string file = System.IO.Path.Combine(Path, FileNameFor(tool, target));
        try
        {
            Directory.CreateDirectory(Path);
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(reply, FileJson);
            string temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, file, overwrite: true);
            Prune(file);
            return Pointer(tool, file, content.LongLength, reply);
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

    private static string FileNameFor(string tool, OutputTarget target) => target switch
    {
        OutputTarget.Named named => named.Name.Value,
        _ => string.Create(CultureInfo.InvariantCulture,
            $"{tool}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}.json")
    };

    private static JsonElement Pointer(string tool, string file, long bytes, JsonElement reply)
    {
        JsonObject counts = new();
        JsonObject summary = new();
        JsonArray fileOnly = new();
        if (reply.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in reply.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    counts[property.Name] = property.Value.GetArrayLength();
                }
                else if (property.Value.GetRawText().Length <= SummaryValueBytes)
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
            ["tool"] = tool,
            ["counts"] = counts,
            ["summary"] = summary
        };
        if (fileOnly.Count > 0)
        {
            pointer["in_file_only"] = fileOnly;
        }

        return JsonSerializer.SerializeToElement(pointer);
    }

    // Another sidecar may write or prune the same folder at the same time: a file that is gone or held is left alone.
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
                // Held by a reader or already pruned by another sidecar.
            }
        }
    }
}
