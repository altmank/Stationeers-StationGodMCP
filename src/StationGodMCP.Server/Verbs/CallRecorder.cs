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
    private bool _failed;
    private bool _closed;

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
            if (!Begin(timeMs))
            {
                return;
            }

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
            End();
        }
    }

    /// <summary>A cut: {t_ms, cut: {ref, verb, what, bytes, sent}}.</summary>
    internal void RecordCut(long timeMs, string reference, string verb, string? what, int bytes, int sent)
    {
        lock (_gate)
        {
            if (!Begin(timeMs))
            {
                return;
            }

            _writer.WriteStartObject("cut");
            _writer.WriteString("ref", reference);
            _writer.WriteString("verb", verb);
            _writer.WriteString("what", what);
            _writer.WriteNumber("bytes", bytes);
            _writer.WriteNumber("sent", sent);
            _writer.WriteEndObject();
            End();
        }
    }

    /// <summary>A re-ask: {t_ms, re_ask: {ref, calls_after, how}}, how being whole, repeat or same_call.</summary>
    internal void RecordReAsk(long timeMs, string reference, int callsAfter, string how)
    {
        lock (_gate)
        {
            if (!Begin(timeMs))
            {
                return;
            }

            _writer.WriteStartObject("re_ask");
            _writer.WriteString("ref", reference);
            _writer.WriteNumber("calls_after", callsAfter);
            _writer.WriteString("how", how);
            _writer.WriteEndObject();
            End();
        }
    }

    // False once the recording is closed: a late call is not recorded, and not failed.
    private bool Begin(long timeMs)
    {
        if (_closed)
        {
            return false;
        }

        _line.SetLength(0);
        _writer.Reset(_line);
        _writer.WriteStartObject();
        _writer.WriteNumber("t_ms", timeMs);
        return true;
    }

    // A recording that cannot be written never fails the call: it is said once on stderr.
    private void End()
    {
        _writer.WriteEndObject();
        _writer.Flush();
        try
        {
            _file.Write(_line.GetBuffer(), 0, (int)_line.Length);
            _file.Write(NewLine);
            _file.Flush();
        }
        catch (IOException exception)
        {
            if (!_failed)
            {
                _failed = true;
                Console.Error.WriteLine($"[StationGodMCP] The recording could not be written: {exception.Message}");
            }
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
            _closed = true;
            _writer.Dispose();
            _file.Dispose();
        }
    }
}
