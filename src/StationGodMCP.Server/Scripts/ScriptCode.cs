using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The save-time parse of a saved script's source: sg._script in a short-lived Python process started as a script
/// worker is (same Python, same sg folder, every PYTHON* variable cleared, PYTHONHASHSEED=0 and PYTHONIOENCODING=utf-8),
/// the source on its stdin, one JSON object on its stdout. The hub is C#: this is the AST it cannot read itself, once
/// per write script, never on a run.
/// </summary>
internal sealed class ScriptCode
{
    /// <summary>What the process runs: puts the sg folder on sys.path, then parses stdin.</summary>
    internal const string Boot = "import sys; sys.path.insert(0, sys.argv[1]); import sg._script; sg._script.main()";

    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResolveWait = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<string> _python;
    private readonly string _sgRoot;
    private readonly object _gate = new();
    private IReadOnlyList<string>? _resolved;

    internal ScriptCode(IReadOnlyList<string> python, string sgRoot)
    {
        _python = python;
        _sgRoot = sgRoot;
    }

    /// <summary>The Python for script workers when the command line names none.</summary>
    internal static IReadOnlyList<string> DefaultPython => OperatingSystem.IsWindows() ? ["py", "-3"] : ["python3"];

    /// <summary>
    /// The source's signature and id constants. A Python that cannot start, a parser that dies, or output that is not
    /// the parser's object is a refusal with the reason: write script then refuses the save.
    /// </summary>
    internal ScriptSignature Parse(string source)
    {
        ProcessStartInfo start = new(Resolved()[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetTempPath()
        };
        IReadOnlyList<string> python = Resolved();
        for (int index = 1; index < python.Count; index++)
        {
            start.ArgumentList.Add(python[index]);
        }

        // -s -P as the worker takes them: no user site, and the working folder stays off sys.path.
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add("-P");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Boot);
        start.ArgumentList.Add(_sgRoot);
        List<string> inherited = [.. start.Environment.Keys];
        foreach (string name in inherited)
        {
            if (name.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(name);
            }
        }

        start.Environment["PYTHONHASHSEED"] = "0";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        try
        {
            using Process? process = Process.Start(start);
            if (process == null)
            {
                return ScriptSignature.Refused($"'{python[0]}' did not start");
            }

            process.StandardInput.Write(source);
            process.StandardInput.Close();
            string output = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)AnswerWait.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return ScriptSignature.Refused("the Python parser did not finish");
            }

            string reply = output.Trim();
            if (reply.Length == 0)
            {
                string reason = errors.Trim();
                return ScriptSignature.Refused($"the Python parser said nothing (exit {process.ExitCode}" +
                                               (reason.Length > 0 ? ": " + Last(reason) : string.Empty) + ")");
            }

            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(reply) as JsonObject;
            }
            catch (JsonException exception)
            {
                return ScriptSignature.Refused($"the Python parser's reply could not be read: {exception.Message}");
            }

            if (parsed == null)
            {
                return ScriptSignature.Refused("the Python parser's reply is not an object");
            }

            if (parsed["ok"] is not JsonValue ok || !ok.TryGetValue(out bool parsedOk) || !parsedOk)
            {
                return ScriptSignature.Refused(JsonNodes.Text(parsed["error"]) ?? "the Python parser refused the source");
            }

            return ScriptSignature.Parsed(Parameters(parsed["parameters"] as JsonArray), Ids(parsed["ids"] as JsonArray));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ScriptSignature.Refused($"the Python parser could not run ({exception.Message})");
        }
    }

    private static List<ScriptParameter> Parameters(JsonArray? given)
    {
        List<ScriptParameter> parameters = [];
        foreach (JsonNode? node in given ?? [])
        {
            if (node is not JsonObject parameter || JsonNodes.Text(parameter["name"]) is not { } name)
            {
                continue;
            }

            parameters.Add(new ScriptParameter(name, JsonNodes.Text(parameter["type"]),
                JsonNodes.IsTrue(parameter["default"]), JsonNodes.Text(parameter["kind"]) ?? ScriptParameter.PosOrKw));
        }

        return parameters;
    }

    private static List<JsonNode?> Ids(JsonArray? given)
    {
        List<JsonNode?> ids = [];
        foreach (JsonNode? id in given ?? [])
        {
            ids.Add(id?.DeepClone());
        }

        return ids;
    }

    // The last line of a traceback: the parser's own error, not the frames above it.
    private static string Last(string text)
    {
        string[] lines = text.TrimEnd().Split('\n');
        return lines.Length == 0 ? text.Trim() : lines[^1].Trim();
    }

    /// <summary>
    /// The Python executable itself, asked once of the configured command (py -3 is a launcher that would start Python
    /// as its own child); the command as given when the question cannot be answered.
    /// </summary>
    private IReadOnlyList<string> Resolved()
    {
        lock (_gate)
        {
            if (_resolved != null)
            {
                return _resolved;
            }
        }

        IReadOnlyList<string> resolved = _python;
        try
        {
            ProcessStartInfo ask = new(_python[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            for (int index = 1; index < _python.Count; index++)
            {
                ask.ArgumentList.Add(_python[index]);
            }

            ask.ArgumentList.Add("-I");
            ask.ArgumentList.Add("-c");
            ask.ArgumentList.Add("import sys; print(sys.executable)");
            using Process? process = Process.Start(ask);
            if (process != null)
            {
                process.StandardInput.Close();
                _ = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd().Trim();
                if (process.WaitForExit(ResolveWait) && process.ExitCode == 0 && output.Length > 0 && File.Exists(output))
                {
                    resolved = [output];
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The configured command as given; the parse reports what starting it did.
        }

        lock (_gate)
        {
            _resolved = resolved;
            return _resolved;
        }
    }
}

/// <summary>The save-time parse: main(...)'s parameters and the id constants, or why the source was refused.</summary>
internal sealed record ScriptSignature(string? Error, IReadOnlyList<ScriptParameter> Parameters, IReadOnlyList<JsonNode?> Ids)
{
    internal static ScriptSignature Refused(string error) => new(error, [], []);

    internal static ScriptSignature Parsed(IReadOnlyList<ScriptParameter> parameters, IReadOnlyList<JsonNode?> ids) =>
        new(null, parameters, ids);

    internal bool Ok => Error == null;
}
