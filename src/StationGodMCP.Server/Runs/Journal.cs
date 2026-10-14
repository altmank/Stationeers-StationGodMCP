using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The hub's journal (docs/architecture/hub.md, The journal): one file per hub process,
/// <c>&lt;journal-dir&gt;/&lt;pid&gt;-&lt;start unix ms&gt;.jsonl</c>, held with FileShare.None for the hub's life so no
/// other hub reads a live one. Each line is appended and flushed before the call it describes is sent: a write's call
/// line (its targets with their new value, and the before value where the hub read it itself, as a hold's restore
/// does) goes out ahead of the write, and its done line carries the before and after values the reply's rows gave, so
/// a hub that dies mid-run leaves what the next hub needs to report and undo it. A journal that cannot be written
/// turns itself off: the hub goes on, says so on stderr once and in its next reply.
/// </summary>
public sealed class Journal : IRunJournal, IDisposable
{
    internal static readonly JsonSerializerOptions LineOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly object _gate = new();
    private readonly FileStream? _file;
    private readonly Func<string?> _world;
    private readonly TextWriter _log;
    private readonly Dictionary<WriteEntry, int> _numbers = new(ReferenceEqualityComparer.Instance);
    private int _next;
    private string? _offReason;
    private bool _offTold;
    private bool _retired;

    private Journal(string path, string name, FileStream? file, Func<string?> world, TextWriter log)
    {
        FilePath = path;
        Name = name;
        _file = file;
        _world = world;
        _log = log;
    }

    /// <summary>The file, <c>&lt;pid&gt;-&lt;start unix ms&gt;.jsonl</c>.</summary>
    public string FilePath { get; }

    /// <summary>The hub's name in the journal: <c>&lt;pid&gt;-&lt;start unix ms&gt;</c>.</summary>
    public string Name { get; }

    public bool IsOff
    {
        get
        {
            lock (_gate)
            {
                return _offReason != null;
            }
        }
    }

    public bool WantsBefore => !IsOff;

    /// <summary>
    /// Opens this hub's journal and writes its hub line. A journal that cannot be opened is returned turned off (the
    /// hub still runs); its reason is told like a write failure.
    /// </summary>
    public static Journal Open(string directory, string token, bool mcp, Func<string?> world, TextWriter? log = null,
        string? target = null)
    {
        log ??= Console.Error;
        long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string name = pid + "-" + started.ToString(CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, name + ".jsonl");
        FileStream? file = null;
        string? problem = null;
        try
        {
            Directory.CreateDirectory(directory);
            while (File.Exists(path))
            {
                started++;
                name = pid + "-" + started.ToString(CultureInfo.InvariantCulture);
                path = Path.Combine(directory, name + ".jsonl");
            }

            file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            problem = exception.Message;
        }

        Journal journal = new(path, name, file, world, log);
        if (problem != null)
        {
            journal.TurnOff(problem);
            return journal;
        }

        journal.Append(new JsonObject
        {
            ["t"] = "hub",
            ["hub"] = name,
            ["token"] = token,
            ["world"] = world(),
            ["mcp"] = mcp,
            ["target"] = target
        });
        return journal;
    }

    /// <summary>"journal off: ..." once, for the next reply, after the journal turned itself off.</summary>
    public string? TakeNotice()
    {
        lock (_gate)
        {
            if (_offReason == null || _offTold)
            {
                return null;
            }

            _offTold = true;
            return $"journal off: {_offReason}; runs are not journalled now, so a run cut off by an abrupt end cannot be " +
                   "reported or undone by the next session";
        }
    }

    /// <summary>The file's lines as written so far, read through the hub's own handle.</summary>
    public List<string> Lines()
    {
        lock (_gate)
        {
            List<string> lines = [];
            if (_file == null)
            {
                return lines;
            }

            long end = _file.Position;
            _file.Position = 0;
            byte[] all = new byte[end];
            _file.ReadExactly(all);
            _file.Position = end;
            foreach (string line in Encoding.UTF8.GetString(all).Split('\n'))
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
    }

    /// <summary>The retired line: every run in the journal ended and was reported.</summary>
    public void Retire()
    {
        lock (_gate)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
        }

        Append(new JsonObject { ["t"] = "retired" });
    }

    public bool Retired
    {
        get
        {
            lock (_gate)
            {
                return _retired && _offReason == null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
        }
    }

    // ---- IRunJournal --------------------------------------------------------------------------------------------------

    public void Started(RunRecord run) => Append(new JsonObject
    {
        ["t"] = "run",
        ["run"] = run.Id,
        ["first_line"] = run.FirstLine,
        ["confirm"] = run.Confirm,
        ["world"] = run.World
    });

    public void Before(RunRecord run, WriteEntry write)
    {
        JsonArray targets = [];
        int number;
        lock (run.Sync)
        {
            foreach (WrittenTarget target in write.Targets)
            {
                JsonObject line = new() { ["id"] = target.Id };
                if (target.Field != null)
                {
                    line[write.What == "logic" ? "logic" : "field"] = target.Field;
                }

                line["before"] = target.Before?.DeepClone();
                line["new"] = target.New?.DeepClone();
                targets.Add(line);
            }
        }

        lock (_gate)
        {
            _next++;
            number = _next;
            _numbers[write] = number;
        }

        Append(new JsonObject
        {
            ["t"] = "call",
            ["n"] = number,
            ["run"] = run.Id,
            ["verb"] = write.Verb,
            ["what"] = write.What,
            ["targets"] = targets,
            ["undo"] = write.Undo,
            ["world"] = run.World
        });
    }

    public void After(RunRecord run, WriteEntry write)
    {
        if (Number(write) is not { } number)
        {
            return;
        }

        JsonArray after = [];
        lock (run.Sync)
        {
            foreach (WrittenTarget target in write.Targets)
            {
                if (!target.Acted)
                {
                    continue;
                }

                JsonObject line = new() { ["id"] = target.Id };
                if (target.Field != null)
                {
                    line[write.What == "logic" ? "logic" : "field"] = target.Field;
                }

                line["value"] = (target.After ?? target.New)?.DeepClone();
                line["before"] = target.Before?.DeepClone();
                after.Add(line);
            }
        }

        JsonObject done = new() { ["t"] = "done", ["n"] = number, ["ok"] = write.Acted, ["after"] = after };
        if (write.MaybeRan)
        {
            done["maybe_ran"] = true;
        }

        Append(done);
    }

    public void NotSent(RunRecord run, WriteEntry write)
    {
        if (Number(write) is { } number)
        {
            Append(new JsonObject { ["t"] = "not_sent", ["n"] = number });
        }
    }

    public void Held(RunRecord run, string id, string logic, JsonNode? after) => Append(new JsonObject
    {
        ["t"] = "hold",
        ["run"] = run.Id,
        ["id"] = id,
        ["logic"] = logic,
        ["after"] = after?.DeepClone()
    });

    public void HoldCleared(RunRecord run, string id, string logic) => Append(new JsonObject
    {
        ["t"] = "hold_cleared",
        ["run"] = run.Id,
        ["id"] = id,
        ["logic"] = logic
    });

    public void Ended(RunRecord run)
    {
        JsonArray left = [];
        string state;
        lock (run.Sync)
        {
            state = Run.StateWord(run.State);
            foreach (LeftTarget target in run.Left)
            {
                left.Add(new JsonObject
                {
                    ["id"] = target.Id,
                    ["logic"] = target.Logic,
                    ["value"] = target.Value?.DeepClone(),
                    ["before"] = target.Before?.DeepClone()
                });
            }
        }

        Append(new JsonObject { ["t"] = "end", ["run"] = run.Id, ["state"] = state, ["left"] = left });
    }

    public void WorldChanged(string? world, List<string> holdsDropped)
    {
        JsonArray runs = [];
        foreach (string id in holdsDropped)
        {
            runs.Add(id);
        }

        Append(new JsonObject { ["t"] = "world", ["world"] = world, ["holds_dropped"] = runs });
    }

    // ---- writing -----------------------------------------------------------------------------------------------------

    private int? Number(WriteEntry write)
    {
        lock (_gate)
        {
            if (!_numbers.TryGetValue(write, out int number))
            {
                return null;
            }

            _numbers.Remove(write);
            return number;
        }
    }

    // One line, written and flushed to the operating system before the caller goes on (so it outlives the hub's process).
    private void Append(JsonObject line)
    {
        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(line.ToJsonString(LineOptions));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // A journal fault must never stop the hub: it turns off and says so.
            TurnOff("a line could not be written as JSON: " + exception.Message);
            return;
        }

        lock (_gate)
        {
            if (_offReason != null || _file == null)
            {
                return;
            }

            try
            {
                // One write per line: a line torn by a crash never runs into the next one.
                byte[] whole = new byte[bytes.Length + 1];
                bytes.CopyTo(whole, 0);
                whole[^1] = NewLine[0];
                _file.Write(whole);
                _file.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ObjectDisposedException or NotSupportedException)
            {
                TurnOffLocked(exception.Message);
            }
        }
    }

    private void TurnOff(string reason)
    {
        lock (_gate)
        {
            TurnOffLocked(reason);
        }
    }

    private void TurnOffLocked(string reason)
    {
        if (_offReason != null)
        {
            return;
        }

        _offReason = reason;
        try
        {
            _log.WriteLine($"[StationGodMCP] the journal is off ({FilePath}): {reason}");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // stderr is gone too; the reply still says it.
        }
    }
}
