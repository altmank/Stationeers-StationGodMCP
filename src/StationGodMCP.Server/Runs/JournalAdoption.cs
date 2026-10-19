using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>What adoption needs of a link that says hello with an earlier hub's resume token.</summary>
internal interface ITokenLink
{
    Task<bool> ConnectAsync(CancellationToken cancellation);

    JsonElement? Welcome { get; }

    Task SessionEndAsync();

    ValueTask CloseAsync(bool bye);
}

/// <summary>A GameLink with the old token, as adoption uses it.</summary>
internal sealed class GameTokenLink(GameLink link) : ITokenLink
{
    public Task<bool> ConnectAsync(CancellationToken cancellation) => link.ConnectAsync(cancellation);

    public JsonElement? Welcome => link.Welcome;

    public Task SessionEndAsync() => link.SessionEndAsync();

    public ValueTask CloseAsync(bool bye) => link.CloseAsync(bye);
}

/// <summary>
/// A journal an earlier hub left (hub.md, The journal), taken with its lock: its runs as records named
/// <c>script-&lt;pid&gt;.N</c>, the old resume token, and the lines adoption appends (restores, retired). A journal whose
/// lock another hub holds is never opened, so a live hub's runs are neither listed nor undone here.
/// </summary>
internal sealed class AdoptedJournal : IDisposable
{
    private readonly FileStream _file;
    private readonly Dictionary<string, List<(string Id, string Logic, JsonNode? After)>> _holds =
        new(StringComparer.Ordinal);

    private AdoptedJournal(string path, FileStream file, string pid, string prefix)
    {
        FilePath = path;
        _file = file;
        Pid = pid;
        _prefix = prefix;
    }

    // What its runs' ids start with after "script-": the pid, or pid-<start ms, short> when two journals share a pid.
    private readonly string _prefix;

    /// <summary>The game the old hub served (pipe or host:port), from its hub line; null when it did not say.</summary>
    internal string? Target { get; private set; }

    /// <summary>True when the journal held restores (hold lines not cleared): its outcomes matter for retiring.</summary>
    internal bool HasHolds
    {
        get
        {
            foreach (List<(string Id, string Logic, JsonNode? After)> holds in _holds.Values)
            {
                if (holds.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Lines that could not be read (torn by an abrupt end): skipped, and counted here.</summary>
    internal int TornLines { get; private set; }

    internal string FilePath { get; }

    /// <summary>The old hub's process id: its runs are script-&lt;pid&gt;.N here.</summary>
    internal string Pid { get; }

    internal string? Token { get; private set; }

    internal List<RunRecord> Runs { get; } = [];

    /// <summary>True once the old token's restore outcomes are known (read now, or by an earlier adoption).</summary>
    internal bool HasRestores { get; private set; }

    internal bool Retired { get; private set; }

    /// <summary>
    /// Every journal in the folder but this hub's own whose lock can be taken, that is not retired, and that served the
    /// same game (when both say). Two journals of the same pid (a pid reused) name their runs script-&lt;pid&gt;-&lt;start
    /// ms, last 5 digits&gt;.N so their ids never collide.
    /// </summary>
    internal static List<AdoptedJournal> TakeAll(string directory, string? own, string? target = null, TextWriter? log = null)
    {
        List<AdoptedJournal> taken = [];
        if (!Directory.Exists(directory))
        {
            return taken;
        }

        List<string> paths = [];
        Dictionary<string, int> pids = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(directory, "*.jsonl"))
        {
            if (own != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            paths.Add(path);
            string pid = PidOf(Path.GetFileNameWithoutExtension(path));
            pids[pid] = pids.TryGetValue(pid, out int count) ? count + 1 : 1;
        }

        foreach (string path in paths)
        {
            bool shared = pids[PidOf(Path.GetFileNameWithoutExtension(path))] > 1;
            if (TryTake(path, shared, log) is not { } journal)
            {
                continue;
            }

            if (target != null && journal.Target != null && journal.Target != target)
            {
                // Another game's journal: its runs and restores belong to a hub of that game.
                journal.Dispose();
                continue;
            }

            taken.Add(journal);
        }

        return taken;
    }

    private static string PidOf(string name)
    {
        int dash = name.IndexOf('-');
        return dash > 0 ? name[..dash] : name;
    }

    internal static AdoptedJournal? TryTake(string path, bool longIds = false, TextWriter? log = null)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another hub holds it: it is live, and its runs are its own.
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        string pid = PidOf(name);
        string started = name.Length > pid.Length + 1 ? name[(pid.Length + 1)..] : "";
        string prefix = longIds ? pid + "-" + (started.Length > 5 ? started[^5..] : started) : pid;
        AdoptedJournal journal = new(path, file, pid, prefix);
        try
        {
            journal.Read();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // One unreadable journal is skipped, and its handle let go; the others are adopted.
            (log ?? Console.Error).WriteLine($"[StationGodMCP] the journal {path} could not be read: {exception.Message}");
            journal.Dispose();
            return null;
        }

        if (journal.Retired)
        {
            journal.Dispose();
            return null;
        }

        file.Seek(0, SeekOrigin.End);
        return journal;
    }

    /// <summary>
    /// The old token's held-restore outcomes (hub.md, The journal): connect with it; when the mod resumed it (inside its
    /// window), send session_end, close without bye, and connect again after a moment; the welcome's restores (from the
    /// last connection) are appended as a restores line, then bye. False when the game could not be reached.
    /// </summary>
    internal async Task<bool> ReadRestoresAsync(Func<string, ITokenLink> links, TimeSpan reconnect, CancellationToken cancellation)
    {
        if (HasRestores || Token == null)
        {
            return HasRestores;
        }

        ITokenLink link = links(Token);
        bool closed = false;
        try
        {
            if (!await link.ConnectAsync(cancellation).ConfigureAwait(false))
            {
                return false;
            }

            if (Resumed(link.Welcome))
            {
                await link.SessionEndAsync().ConfigureAwait(false);
                closed = true;
                await link.CloseAsync(bye: false).ConfigureAwait(false);
                await Task.Delay(reconnect, cancellation).ConfigureAwait(false);
                link = links(Token);
                closed = false;
                if (!await link.ConnectAsync(cancellation).ConfigureAwait(false))
                {
                    return false;
                }
            }

            JsonObject line = new() { ["t"] = "restores" };
            JsonNode? restores = link.Welcome is { } welcome && welcome.TryGetProperty("restores", out JsonElement given)
                ? JsonNodes.FromElement(given)
                : null;
            line["applied"] = (restores?["applied"] as JsonArray)?.DeepClone() ?? new JsonArray();
            line["skipped"] = (restores?["skipped"] as JsonArray)?.DeepClone() ?? new JsonArray();
            line["dropped"] = restores?["dropped"]?.DeepClone() ?? 0;
            Append(line);
            TakeRestores(line);
            closed = true;
            await link.CloseAsync(bye: true).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            // Every way out closes the link: without bye, so nothing of the old token is cleared unread.
            if (!closed)
            {
                await link.CloseAsync(bye: false).ConfigureAwait(false);
            }
        }
    }

    internal void Retire()
    {
        if (Retired)
        {
            return;
        }

        Retired = true;
        Append(new JsonObject { ["t"] = "retired" });
    }

    public void Dispose() => _file.Dispose();

    private static bool Resumed(JsonElement? welcome) =>
        welcome is { } given && given.TryGetProperty("resume", out JsonElement resume) &&
        resume.ValueKind == JsonValueKind.Object && resume.TryGetProperty("resumed", out JsonElement resumed) &&
        resumed.ValueKind == JsonValueKind.True;

    private void Append(JsonObject line)
    {
        try
        {
            // A torn last line gets its newline first, so this line starts a line of its own.
            string text = line.ToJsonString(Journal.LineOptions) + "\n";
            if (_file.Length > 0)
            {
                _file.Position = _file.Length - 1;
                bool newline = _file.ReadByte() == '\n';
                _file.Position = _file.Length;
                if (!newline)
                {
                    text = "\n" + text;
                }
            }

            _file.Write(Encoding.UTF8.GetBytes(text));
            _file.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // The outcome still reaches this session's reply; only the file lacks it.
        }
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    private void Read()
    {
        _file.Position = 0;
        using StreamReader reader = new(_file, Encoding.UTF8, false, 4096, leaveOpen: true);
        Dictionary<string, RunRecord> runs = new(StringComparer.Ordinal);
        Dictionary<long, (RunRecord Run, WriteEntry Write)> calls = new();
        HashSet<string> ended = new(StringComparer.Ordinal);
        Dictionary<RunRecord, StringBuilder> printed = new(ReferenceEqualityComparer.Instance);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // A line torn by an abrupt end: the lines before it stand.
                TornLines++;
                continue;
            }

            if (entry == null)
            {
                continue;
            }

            if (JsonNodes.Text(entry["t"]) == "printed")
            {
                Printed(entry, runs, printed);
            }
            else
            {
                Take(entry, runs, calls, ended);
            }
        }

        foreach (RunRecord run in Runs)
        {
            if (printed.TryGetValue(run, out StringBuilder? output) && run.Printed.Length == 0)
            {
                run.Printed = output.ToString();
            }

            if (!ended.Contains(run.Id))
            {
                // The hub ended before the run did: what it changed is reckoned from its writes.
                run.State = RunState.Killed;
                run.EndedByHubRestart = true;
                run.Error = "ended by hub restart: the hub that ran it ended before the run did";
                LeftFromWrites(run);
            }
        }
    }

    private void Take(JsonObject entry, Dictionary<string, RunRecord> runs,
        Dictionary<long, (RunRecord Run, WriteEntry Write)> calls, HashSet<string> ended)
    {
        string? kind = JsonNodes.Text(entry["t"]);
        RunRecord? run = JsonNodes.Text(entry["run"]) is { } runId && runs.TryGetValue(runId, out RunRecord? known) ? known : null;
        switch (kind)
        {
            case "hub":
                Token = JsonNodes.Text(entry["token"]);
                Target = JsonNodes.Text(entry["target"]);
                break;
            case "run" when JsonNodes.Text(entry["run"]) is { } id:
                RunRecord adopted = new(AdoptedId(id), JsonNodes.Text(entry["first_line"]) ?? "", JsonNodes.IsTrue(entry["confirm"]), 0,
                    JsonNodes.Text(entry["world"])) { Adopted = true };
                runs[id] = adopted;
                Runs.Add(adopted);
                break;
            case "call" when run != null && JsonNodes.Number(entry["n"]) is { } n:
                calls[(long)n] = (run, Call(entry, run));
                break;
            case "done" when JsonNodes.Number(entry["n"]) is { } n && calls.TryGetValue((long)n, out var done):
                Done(entry, done.Write);
                break;
            case "not_sent" when JsonNodes.Number(entry["n"]) is { } n && calls.TryGetValue((long)n, out var unsent):
                unsent.Run.Writes.Remove(unsent.Write);
                break;
            case "hold" when run != null && JsonNodes.Text(entry["id"]) is { } holdId && JsonNodes.Text(entry["logic"]) is { } logic:
                if (!_holds.TryGetValue(run.Id, out var holds))
                {
                    holds = [];
                    _holds[run.Id] = holds;
                }

                holds.Add((holdId, logic, entry["after"]?.DeepClone()));
                break;
            case "hold_cleared" when run != null && JsonNodes.Text(entry["id"]) is { } clearedId &&
                                     JsonNodes.Text(entry["logic"]) is { } clearedLogic:
                if (_holds.TryGetValue(run.Id, out var held))
                {
                    held.RemoveAll(hold => hold.Id == clearedId && hold.Logic == clearedLogic);
                }

                break;
            case "end" when run != null:
                ended.Add(run.Id);
                run.State = StateOf(JsonNodes.Text(entry["state"]));
                run.Returned = entry["returned"]?.DeepClone();
                run.Error = JsonNodes.Text(entry["error"]);
                run.StopCode = JsonNodes.Text(entry["stop_code"]);
                run.StopMessage = JsonNodes.Text(entry["stop"]);
                run.EndedByHubRestart = run.StopCode == RunHost.HubRestartCode;
                foreach (JsonNode? left in entry["left"] as JsonArray ?? [])
                {
                    if (JsonNodes.Text(left?["id"]) is { } leftId && JsonNodes.Text(left!["logic"]) is { } leftLogic)
                    {
                        run.Left.Add(new LeftTarget(leftId, leftLogic, left["value"]?.DeepClone(), left["before"]?.DeepClone()));
                    }
                }

                break;
            case "restores":
                TakeRestores(entry);
                break;
            case "retired":
                Retired = true;
                break;
        }
    }

    // A printed line: one piece of the run's output, in order; a cut line says the run printed more than was journalled.
    private static void Printed(JsonObject entry, Dictionary<string, RunRecord> runs, Dictionary<RunRecord, StringBuilder> printed)
    {
        if (JsonNodes.Text(entry["run"]) is not { } id || !runs.TryGetValue(id, out RunRecord? run))
        {
            return;
        }

        if (JsonNodes.IsTrue(entry["cut"]))
        {
            run.PrintedCut = true;
            return;
        }

        if (!printed.TryGetValue(run, out StringBuilder? output))
        {
            output = new StringBuilder();
            printed[run] = output;
        }

        output.Append(JsonNodes.Text(entry["text"]));
    }

    private string AdoptedId(string id) =>
        id.StartsWith("script-", StringComparison.Ordinal) ? "script-" + _prefix + "." + id["script-".Length..] : id;

    // A call line: the write as sent, acted until its done line says otherwise (a call with no done line may have run).
    private static WriteEntry Call(JsonObject entry, RunRecord run)
    {
        string? what = JsonNodes.Text(entry["what"]);
        List<WrittenTarget> targets = [];
        foreach (JsonNode? target in entry["targets"] as JsonArray ?? [])
        {
            if (JsonNodes.Text(target?["id"]) is not { } id)
            {
                continue;
            }

            WrittenTarget written = new(id, JsonNodes.Text(target!["logic"]) ?? JsonNodes.Text(target["field"]),
                target["new"]?.DeepClone())
            {
                Before = target["before"]?.DeepClone()
            };
            targets.Add(written);
        }

        string undo = JsonNodes.Text(entry["undo"]) ?? "cannot";
        WriteEntry write = new((int)(JsonNodes.Number(entry["n"]) ?? 0), JsonNodes.Text(entry["verb"]) ?? "?", what, targets,
            undo, undo == "undo" ? "[undo]" : "[cannot undo]", WriteGroup.Done, null)
        {
            Acted = true,
            MaybeRan = true
        };
        run.Writes.Add(write);
        return write;
    }

    private static void Done(JsonObject entry, WriteEntry write)
    {
        write.MaybeRan = JsonNodes.IsTrue(entry["maybe_ran"]);
        write.Acted = JsonNodes.IsTrue(entry["ok"]) || write.MaybeRan;
        foreach (WrittenTarget target in write.Targets)
        {
            target.Acted = false;
        }

        foreach (JsonNode? after in entry["after"] as JsonArray ?? [])
        {
            string? id = JsonNodes.Text(after?["id"]);
            string? field = JsonNodes.Text(after?["logic"]) ?? JsonNodes.Text(after?["field"]);
            foreach (WrittenTarget target in write.Targets)
            {
                if (target.Id == id && target.Field == field && !target.Acted)
                {
                    target.Acted = true;
                    target.After = after!["value"]?.DeepClone();
                    target.Before = after["before"]?.DeepClone() ?? target.Before;
                    break;
                }
            }
        }
    }

    private static RunState StateOf(string? word) => word switch
    {
        "done" => RunState.Done,
        "REPORT" => RunState.Report,
        "stopped" => RunState.Stopped,
        "failed" => RunState.Failed,
        _ => RunState.Killed
    };

    // Per logic target: the last value written against the value before the run.
    private static void LeftFromWrites(RunRecord run)
    {
        List<string> order = [];
        Dictionary<string, (string Id, string Logic, JsonNode? Before, JsonNode? Last)> targets = new(StringComparer.Ordinal);
        foreach (WriteEntry write in run.Writes)
        {
            if (!write.Acted || write.What != "logic")
            {
                continue;
            }

            foreach (WrittenTarget target in write.Targets)
            {
                if (!target.Acted || target.Field == null)
                {
                    continue;
                }

                string key = target.Id + "\n" + target.Field;
                if (targets.TryGetValue(key, out var known))
                {
                    targets[key] = (known.Id, known.Logic, known.Before, target.After ?? target.New);
                }
                else
                {
                    order.Add(key);
                    targets[key] = (target.Id, target.Field, target.Before, target.After ?? target.New);
                }
            }
        }

        foreach (string key in order)
        {
            var target = targets[key];
            if (!Run.SameValue(target.Last, target.Before))
            {
                run.Left.Add(new LeftTarget(target.Id, target.Logic, target.Last?.DeepClone(), target.Before?.DeepClone()));
            }
        }
    }

    // The restores line: what the mod's held restores did for the old token, matched to the runs that held them. An
    // applied restore put its target back, so it is no longer left.
    private void TakeRestores(JsonObject line)
    {
        HasRestores = true;
        foreach (RunRecord run in Runs)
        {
            if (!_holds.TryGetValue(run.Id, out var holds))
            {
                continue;
            }

            List<string> applied = [];
            List<string> skipped = [];
            foreach ((string id, string logic, JsonNode? after) in holds)
            {
                if (Find(line["applied"], id, logic) != null)
                {
                    applied.Add($"{id} {logic}={Run.Shown(after)}");
                    run.Left.RemoveAll(left => left.Id == id && left.Logic == logic);
                }
                else if (Find(line["skipped"], id, logic) is { } skip)
                {
                    string? why = JsonNodes.Text(skip["reason"]);
                    skipped.Add(why == null ? $"{id} {logic}" : $"{id} {logic} ({why})");
                }
            }

            if (applied.Count > 0 || skipped.Count > 0)
            {
                run.Restores = "held restores " +
                               (applied.Count > 0 ? "applied: " + string.Join(", ", applied) : "applied: none") +
                               (skipped.Count > 0 ? "; skipped: " + string.Join(", ", skipped) : "");
            }
        }
    }

    private static JsonObject? Find(JsonNode? list, string id, string logic)
    {
        foreach (JsonNode? entry in list as JsonArray ?? [])
        {
            if (entry is not JsonObject restore || Run.IdText(restore["reference_id"]) != id)
            {
                continue;
            }

            JsonNode? type = restore["logic_type"];
            string? name = JsonNodes.Text(type?["name"]) ?? JsonNodes.Text(type) ?? Run.IdText(type?["id"]) ?? Run.IdText(type);
            if (name == null || string.Equals(name, logic, StringComparison.OrdinalIgnoreCase))
            {
                return restore;
            }
        }

        return null;
    }
}
