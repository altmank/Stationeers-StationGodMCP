using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// A run's reply text (hub.md, Run replies): the state line, the report header, the returned value and printed output
/// (each cut at 4 KB), the failed-run text, the stop's rows and fix, then the footer: writes by outcome, warnings,
/// passed guards, contested values, left:, undo and the cost line. Rendering a finished run's text is when its printed
/// and returned values, its report rows and its stop rows become seen: the text is about to be sent.
/// </summary>
internal static class RunText
{
    internal const int CutBytes = 4096;

    private const int CompactItems = 8;
    private const int ReportedRows = 10;
    private const int ShownWarnings = 10;

    // A warning's shape: its numbers (ids, cells, pressures) masked, so warnings alike but for those are told once.
    private static readonly Regex Numbers = new(@"-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);

    /// <summary>
    /// The whole reply of a finished run, adding what it shows to the seen set; whole lifts the 4 KB cut of its returned
    /// value and printed output (inspect job whole: true).
    /// </summary>
    internal static string Final(RunRecord run, SeenSet? seen, bool whole = false)
    {
        StringBuilder text = new();
        lock (run.Sync)
        {
            text.Append(StateLine(run)).Append('\n');
            if (run.State == RunState.Report)
            {
                text.Append("limits: reads after a reported write show the old value; a waiting loop runs once; writes on " +
                            "what this run builds are not shown");
                if (run.PassEndLine is { } passEnd)
                {
                    text.Append("; the pass ended at line ").Append(passEnd.ToString(CultureInfo.InvariantCulture));
                }

                text.Append('\n').Append("to act, send the same code again with confirm: true\n");
            }

            if (run.Returned != null)
            {
                string returned = Cut(Run.Shown(run.Returned), whole, out bool cut);
                seen?.ShownPlain(returned);
                text.Append("returned: ").Append(returned);
                if (cut)
                {
                    text.Append($" ... (cut at 4 KB; {AllOfIt(run)})");
                }

                text.Append('\n');
            }

            if (run.Printed.Length > 0)
            {
                string printed = Cut(run.Printed.TrimEnd('\n'), whole, out bool cut);
                seen?.ShownPlain(printed);
                text.Append(printed).Append('\n');
                if (cut)
                {
                    text.Append($"... (printed output cut at 4 KB; {AllOfIt(run)})\n");
                }
            }

            if (run.PrintedCut)
            {
                text.Append($"... (the journal kept the first {Run.JournalledPrinted} characters of its printed output)\n");
            }

            if (run.Error is { Length: > 0 } error)
            {
                text.Append(error.TrimEnd('\n')).Append('\n');
            }

            Footer(run, seen, text, whole);
        }

        return text.ToString();
    }

    /// <summary>A live run's reply: where it is, and how to follow it.</summary>
    internal static string Running(RunRecord run, int? line, int writes, string? lastWrite, StreamCost? cost)
    {
        double wall = Wall(run);
        string added = writes == 0 ? "+0 writes" : $"+{writes} {lastWrite}";
        return $"{run.Id} running: line {line?.ToString(CultureInfo.InvariantCulture) ?? "?"}, {Number(wall, 0)} s in, " +
               $"{added}, {Number(Rate(cost?.Calls ?? run.Calls, wall), 1)} calls/s, " +
               $"{Number(Rate(cost?.MainThreadMs ?? 0, wall), 1)} ms/s game\n" +
               $"follow it with inspect {{what: \"job\", id: \"{run.Id}\"}}";
    }

    /// <summary>
    /// cost script-N (first line): C calls, R calls/s, W writes, G game ms, M ms/s, F frames over budget, S samples,
    /// X ms sampling, U updates, ...
    /// </summary>
    internal static string CostLine(RunRecord run, StreamCost? cost, int writes)
    {
        double wall = Wall(run);
        long calls = cost?.Calls ?? run.Calls;
        double gameMs = cost?.MainThreadMs ?? 0;
        // stream_costs carries no held-tick counter, so the tick held is not measured per run yet.
        return $"cost {run.Id} ({run.FirstLine}): {calls} calls, {Number(Rate(calls, wall), 1)} calls/s, {writes} writes, " +
               $"{Number(gameMs, 1)} game ms, {Number(Rate(gameMs, wall), 1)} ms/s, {cost?.OverBudgetFrames ?? 0} frames over " +
               $"budget, {cost?.Samples ?? 0} samples, {Number(cost?.SampleMs ?? 0, 1)} ms sampling, " +
               $"{cost?.Updates ?? 0} updates, tick held 0 ms, {Number(wall, 1)} s wall";
    }

    internal static string StateLine(RunRecord run) => run.State switch
    {
        RunState.Stopped => $"{run.Id} stopped: {run.StopCode ?? "stopped"} ({run.StopMessage ?? "the run was stopped"})",
        _ => $"{run.Id} {Run.StateWord(run.State)}"
    };

    // Under the run's lock.
    private static void Footer(RunRecord run, SeenSet? seen, StringBuilder text, bool whole)
    {
        if (run.StopRows is { Count: > 0 } stopRows)
        {
            text.Append("these targets were refused (now seen):\n");
            foreach (JsonNode? row in stopRows)
            {
                Shown(row, seen);
                text.Append("  ").Append(JsonNodes.Text(row?["text"]) ?? row?.ToJsonString()).Append('\n');
            }
        }

        if (run.StopFix != null)
        {
            text.Append("fix: ").Append(run.StopFix).Append('\n');
        }

        WriteGroups(run, text);
        ReportedGroups(run, seen, text, whole);
        RefusedGroups(run, text);
        Warnings(text, run.Warnings, whole);
        Joined(text, "PASSED", run.Passed);
        Joined(text, "CONTESTED", run.Contested);
        if (run.State is RunState.Stopped or RunState.Killed && run.Left.Count > 0)
        {
            text.Append("left: ").Append(string.Join(", ", run.Left)).Append('\n');
        }

        List<string> classes = [];
        int acted = 0;
        foreach (WriteEntry write in run.Writes)
        {
            if (write.Acted)
            {
                acted++;
                if (!classes.Contains(write.Undo))
                {
                    classes.Add(write.Undo);
                }
            }
        }

        if (acted > 0)
        {
            text.Append($"undo {run.Id} (classes: {string.Join(", ", classes)})\n");
        }

        text.Append(CostLine(run, run.Cost, acted));
    }

    // DONE / HELD / CLEANUP verb what xN [mark]: up to 8 compact items, in the order first written.
    private static void WriteGroups(RunRecord run, StringBuilder text)
    {
        List<string> keys = [];
        Dictionary<string, List<WrittenTarget>> items = new(StringComparer.Ordinal);
        foreach (WriteEntry write in run.Writes)
        {
            if (!write.Acted)
            {
                continue;
            }

            string key = GroupWord(write.Group) + "\n" + write.Label + "\n" + write.UndoMark +
                         (write.MaybeRan ? " (maybe ran)" : "");
            if (!items.TryGetValue(key, out List<WrittenTarget>? list))
            {
                list = [];
                items[key] = list;
                keys.Add(key);
            }

            foreach (WrittenTarget target in write.Targets)
            {
                if (target.Acted)
                {
                    list.Add(target);
                }
            }
        }

        foreach (string key in keys)
        {
            List<WrittenTarget> list = items[key];
            List<string> shown = [];
            for (int index = 0; index < list.Count && index < CompactItems; index++)
            {
                shown.Add(Run.Item(list[index]));
            }

            string[] parts = key.Split('\n');
            text.Append($"{parts[0]} {parts[1]} x{list.Count} {parts[2]}".TrimEnd());
            if (shown.Count > 0)
            {
                text.Append(": ").Append(string.Join(", ", shown));
                if (list.Count > CompactItems)
                {
                    text.Append($", ... {list.Count - CompactItems} more");
                }
            }

            text.Append('\n');
        }
    }

    // REPORTED verb what xN [mark]: the first 10 rows (every row when whole), then "... N more". Every row becomes
    // seen, shown or not: a report run lists its targets so the same code can act on them with confirm.
    private static void ReportedGroups(RunRecord run, SeenSet? seen, StringBuilder text, bool whole)
    {
        List<string> keys = [];
        Dictionary<string, List<JsonObject>> groups = new(StringComparer.Ordinal);
        foreach (ListedRow listed in run.Reported)
        {
            string key = listed.Label + "\n" + listed.UndoMark;
            if (!groups.TryGetValue(key, out List<JsonObject>? rows))
            {
                rows = [];
                groups[key] = rows;
                keys.Add(key);
            }

            rows.Add(listed.Row);
        }

        foreach (string key in keys)
        {
            List<JsonObject> rows = groups[key];
            int cut = key.IndexOf('\n');
            text.Append($"REPORTED {key[..cut]} x{rows.Count} {key[(cut + 1)..]}:\n".Replace(" :\n", ":\n"));
            int shown = whole ? rows.Count : Math.Min(rows.Count, ReportedRows);
            for (int index = 0; index < rows.Count; index++)
            {
                Shown(rows[index], seen);
                if (index < shown)
                {
                    text.Append("  ").Append(JsonNodes.Text(rows[index]["text"]) ?? rows[index].ToJsonString())
                        .Append('\n');
                }
            }

            if (rows.Count > shown)
            {
                text.Append($"  ... {rows.Count - shown} more (seen too; every row: {Every(run)})\n");
            }
        }
    }

    // refused verb what xN: id (why), for writes refused after a stop.
    private static void RefusedGroups(RunRecord run, StringBuilder text)
    {
        List<string> keys = [];
        Dictionary<string, List<string>> groups = new(StringComparer.Ordinal);
        foreach (ListedRow listed in run.Refused)
        {
            if (!groups.TryGetValue(listed.Label, out List<string>? items))
            {
                items = [];
                groups[listed.Label] = items;
                keys.Add(listed.Label);
            }

            items.Add($"{Run.Shown(listed.Row["reference_id"])} ({JsonNodes.Text(listed.Row["error"]?["message"])})");
        }

        foreach (string key in keys)
        {
            List<string> items = groups[key];
            List<string> shown = items.Count <= CompactItems ? items : items.GetRange(0, CompactItems);
            text.Append($"refused {key} x{items.Count}: {string.Join(", ", shown)}");
            if (items.Count > CompactItems)
            {
                text.Append($", ... {items.Count - CompactItems} more");
            }

            text.Append('\n');
        }
    }

    // WARNINGS: one per shape (numbers masked) with how many there were, the first 10 shapes (all when whole).
    private static void Warnings(StringBuilder text, List<string> warnings, bool whole)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        List<string> shapes = [];
        Dictionary<string, (string First, int Count)> alike = new(StringComparer.Ordinal);
        foreach (string warning in warnings)
        {
            string shape = Numbers.Replace(warning, "#");
            if (alike.TryGetValue(shape, out (string First, int Count) standing))
            {
                alike[shape] = (standing.First, standing.Count + 1);
            }
            else
            {
                alike[shape] = (warning, 1);
                shapes.Add(shape);
            }
        }

        int shown = whole ? shapes.Count : Math.Min(shapes.Count, ShownWarnings);
        List<string> items = new(shown);
        for (int index = 0; index < shown; index++)
        {
            (string first, int count) = alike[shapes[index]];
            items.Add(count > 1 ? $"{first} (x{count} alike)" : first);
        }

        text.Append("WARNINGS: ").Append(string.Join("; ", items));
        if (shapes.Count > shown)
        {
            text.Append($"; ... {shapes.Count - shown} more kinds");
        }

        text.Append('\n');
    }

    private static void Joined(StringBuilder text, string title, List<string> items)
    {
        if (items.Count > 0)
        {
            text.Append(title).Append(": ").Append(string.Join("; ", items)).Append('\n');
        }
    }

    // A row shown to the agent: its id is seen from now on.
    private static void Shown(JsonNode? row, SeenSet? seen)
    {
        if (Run.IdText(row?["id"]) is { } id)
        {
            seen?.Add(id);
        }
    }

    private static string GroupWord(WriteGroup group) => group switch
    {
        WriteGroup.Held => "HELD",
        WriteGroup.Cleanup => "CLEANUP",
        _ => "DONE"
    };

    private static string Every(RunRecord run) => $"inspect {{what: \"job\", id: \"{run.Id}\", whole: true}}";

    private static string AllOfIt(RunRecord run) =>
        $"all of it: inspect {{what: \"job\", id: \"{run.Id}\", whole: true}} or with output_file; in a script " +
        $"sg.ref('{run.Id}')";

    private static string Cut(string text, bool whole, out bool cut)
    {
        if (whole)
        {
            cut = false;
            return text;
        }

        return Cut(text, out cut);
    }

    /// <summary>The text cut at about 4 KB of UTF-8, on a character boundary.</summary>
    internal static string Cut(string text, out bool cut)
    {
        cut = Encoding.UTF8.GetByteCount(text) > CutBytes;
        if (!cut)
        {
            return text;
        }

        int bytes = 0;
        int index = 0;
        while (index < text.Length)
        {
            int width = char.IsSurrogatePair(text, index) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(text.AsSpan(index, width));
            if (bytes + size > CutBytes)
            {
                break;
            }

            bytes += size;
            index += width;
        }

        return text[..index];
    }

    private static double Wall(RunRecord run) => Math.Max(((run.Ended ?? DateTime.UtcNow) - run.Started).TotalSeconds, 0);

    private static double Rate(double amount, double seconds) => seconds > 0 ? amount / seconds : 0;

    private static string Number(double value, int decimals) =>
        Math.Round(value, decimals).ToString(decimals == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
