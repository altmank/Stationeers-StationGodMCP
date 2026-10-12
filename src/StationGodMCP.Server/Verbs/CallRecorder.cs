using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The recording (docs/architecture/hub.md, Recording): with --record, one JSON line per call at the pipeline input,
/// before it is checked: {t_ms, face, stream, in_flight, verb, what, options: [names]}. Appended and flushed line by
/// line under one lock, so lines from concurrent calls never interleave.
/// </summary>
internal sealed class CallRecorder : IDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly object _gate = new();
    private readonly FileStream _file;
    private readonly MemoryStream _line = new();
    private readonly Utf8JsonWriter _writer;

    internal CallRecorder(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new Utf8JsonWriter(_line, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    internal void Record(long timeMs, Face face, long stream, int inFlight, string verb, string? what, JsonObject options)
    {
        lock (_gate)
        {
            _line.SetLength(0);
            _writer.Reset(_line);
            _writer.WriteStartObject();
            _writer.WriteNumber("t_ms", timeMs);
            _writer.WriteString("face", FaceName(face));
            _writer.WriteNumber("stream", stream);
            _writer.WriteNumber("in_flight", inFlight);
            _writer.WriteString("verb", verb);
            if (what != null)
            {
                _writer.WriteString("what", what);
            }
            else
            {
                _writer.WriteNull("what");
            }

            _writer.WriteStartArray("options");
            foreach ((string name, JsonNode? _) in options)
            {
                _writer.WriteStringValue(name);
            }

            _writer.WriteEndArray();
            _writer.WriteEndObject();
            _writer.Flush();
            _file.Write(_line.GetBuffer(), 0, (int)_line.Length);
            _file.Write(NewLine);
            _file.Flush();
        }
    }

    internal static string FaceName(Face face) => face switch
    {
        Face.Script => "script",
        Face.Program => "program",
        _ => "direct"
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
            _file.Dispose();
        }
    }
}
