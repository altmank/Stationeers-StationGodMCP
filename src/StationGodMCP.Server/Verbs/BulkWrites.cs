using System.Globalization;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// One bulk write (write-N): the what, its reply, and its undo steps (newest first, numbered from 1), with the steps
/// already undone so a second undo resumes, and the one undo in progress (one confirm wins).
/// </summary>
internal sealed class BulkWrite
{
    private int _undoing;

    internal BulkWrite(string id, WhatEntry what, JsonNode? reply)
    {
        Id = id;
        What = what;
        Reply = reply;
        Steps = BulkWrites.Inverse(what, reply);
    }

    internal string Id { get; }

    internal WhatEntry What { get; }

    internal JsonNode? Reply { get; }

    internal List<BulkStep> Steps { get; }

    /// <summary>Numbers of the steps already written back.</summary>
    internal HashSet<int> Done { get; } = [];

    internal int Undoable
    {
        get
        {
            int count = 0;
            foreach (BulkStep step in Steps)
            {
                if (step.Back != null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Every step that can be undone has been.</summary>
    internal bool Undone => Undoable > 0 && Done.Count >= Undoable;

    /// <summary>Takes the undo for one caller; false when another undo is running.</summary>
    internal bool BeginUndo() => Interlocked.CompareExchange(ref _undoing, 1, 0) == 0;

    internal void EndUndo() => Volatile.Write(ref _undoing, 0);

    /// <summary>inspect {what: "job", id: "write-N"}.</summary>
    internal JsonObject View()
    {
        JsonArray steps = [];
        foreach (BulkStep step in Steps)
        {
            steps.Add(step.RowFor(Done.Contains(step.Number)));
        }

        return new JsonObject
        {
            ["job_id"] = Id,
            ["verb"] = What.Verb,
            ["what"] = What.Name,
            ["status"] = Undone ? "undone" : Done.Count > 0 ? "partly_undone" : "applied",
            ["steps"] = steps
        };
    }
}

/// <summary>
/// One step of a bulk write's undo: the options of the same what that put one target back (null when it cannot be
/// undone), its report row with its own undo mark, and what the write left there (the drift check reads it again first).
/// </summary>
internal sealed class BulkStep(int number, JsonObject? back, JsonObject row, BulkCheck? check)
{
    internal int Number { get; } = number;

    internal JsonObject? Back { get; } = back;

    internal JsonObject Row { get; } = row;

    internal BulkCheck? Check { get; } = check;

    internal JsonObject RowFor(bool done)
    {
        JsonObject row = (JsonObject)Row.DeepClone();
        row["step"] = Number;
        if (done)
        {
            row["done"] = true;
        }

        return row;
    }
}

/// <summary>What a write left on a target: a logic value, or a custom name.</summary>
internal sealed class BulkCheck(string id, JsonNode? logic, JsonNode? left)
{
    internal string Id { get; } = id;

    /// <summary>The logic type for a logic value; null for a name.</summary>
    internal JsonNode? Logic { get; } = logic;

    internal JsonNode? Left { get; } = left;
}

/// <summary>
/// The hub's bulk writes (writes topic): a direct write that acts on a list, a ref or a list of entries is kept as
/// write-N. Its undo writes previous values back newest first: logic values of held settings (previous_value), labels
/// (the previous custom name), paint (previous_color) and slot moves (back to from). Pulse and action logic types,
/// merged moves and store moves are listed as steps that cannot be undone.
/// </summary>
internal sealed class BulkWrites
{
    internal const string Prefix = "write-";

    // Logic types that fire an action rather than hold a setting: writing the old value back undoes nothing.
    internal static readonly string[] ActionTypes = ["Activate", "ClearMemory", "Reset", "Pulse", "Clear"];

    private readonly object _gate = new();
    private readonly Dictionary<string, BulkWrite> _writes = new(StringComparer.OrdinalIgnoreCase);
    private int _next;

    internal BulkWrite Mint(WhatEntry what, JsonNode? reply)
    {
        lock (_gate)
        {
            _next++;
            BulkWrite write = new(Prefix + _next.ToString(CultureInfo.InvariantCulture), what, reply?.DeepClone());
            _writes[write.Id] = write;
            return write;
        }
    }

    internal BulkWrite? Get(string id)
    {
        lock (_gate)
        {
            return _writes.TryGetValue(id.Trim(), out BulkWrite? write) ? write : null;
        }
    }

    internal static bool IsBulkId(string? id) =>
        id != null && id.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A store move's reply (into or out of a vault or silo).</summary>
    internal static bool IsStoreReply(JsonNode? reply) =>
        reply is JsonObject store && (store["vault"] != null || store["silo"] != null || store["from_vault"] != null);

    /// <summary>A reply's rows: its results (a store move's items.results), or the reply itself for one target.</summary>
    internal static List<JsonObject> Rows(JsonNode? reply)
    {
        List<JsonObject> rows = [];
        JsonNode? results = reply?["results"] ?? reply?["items"]?["results"];
        if (results is JsonArray list)
        {
            foreach (JsonNode? row in list)
            {
                if (row is JsonObject entry)
                {
                    rows.Add(entry);
                }
            }
        }
        else if (reply is JsonObject single)
        {
            rows.Add(single);
        }

        return rows;
    }

    /// <summary>True for a logic type that fires an action (by name, any case).</summary>
    internal static bool IsAction(JsonNode? logic)
    {
        string? name = JsonNodes.Text(logic) ?? JsonNodes.Text(logic?["name"]);
        if (name == null)
        {
            return false;
        }

        foreach (string action in ActionTypes)
        {
            if (string.Equals(action, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The undo steps, newest first, numbered from 1; failed rows are left out, cannot-undo rows kept.</summary>
    internal static List<BulkStep> Inverse(WhatEntry what, JsonNode? reply)
    {
        List<BulkStep> steps = [];
        bool store = IsStoreReply(reply);
        List<JsonObject> rows = Rows(reply);
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            JsonObject row = rows[index];
            if (row["ok"] is { } ok && !JsonNodes.IsTrue(ok))
            {
                continue;
            }

            if (JsonNodes.Text(row["reference_id"]) is not { } id)
            {
                continue;
            }

            steps.Add(Step(steps.Count + 1, what, id, row, store));
        }

        return steps;
    }

    private static BulkStep Step(int number, WhatEntry what, string id, JsonObject row, bool store)
    {
        switch (what.Key)
        {
            case "write.logic":
            {
                JsonNode? logic = row["logic_type"]?["name"] ?? row["logic_type"]?["id"] ?? row["logic_type"];
                JsonNode? left = row["current_value"] ?? row["requested_value"];
                if (IsAction(logic))
                {
                    return Cannot(number, id, left, "[cannot undo: action]");
                }

                if (row["previous_value"] is not { } previous || JsonNodes.Number(previous) == null)
                {
                    return Cannot(number, id, left, "[cannot undo: no previous value]");
                }

                JsonObject back = new()
                {
                    ["writes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = id, ["logic"] = logic?.DeepClone(), ["value"] = previous.DeepClone()
                    })
                };
                return new BulkStep(number, back, Row(id, left, previous, "[undo]"),
                    new BulkCheck(id, logic, left));
            }
            case "write.label" when row["previous"] is JsonObject previous:
            {
                JsonNode? name = previous["custom_name"] ?? JsonValue.Create(string.Empty);
                JsonNode? left = row["current"]?["custom_name"];
                JsonObject back = new()
                {
                    ["labels"] = new JsonArray(new JsonObject { ["id"] = id, ["label"] = name?.DeepClone() })
                };
                return new BulkStep(number, back, Row(id, left, name, "[undo]"), new BulkCheck(id, null, left));
            }
            case "write.paint" when row["previous_color"] is { } color:
            {
                JsonObject back = new()
                {
                    ["items"] = new JsonArray(new JsonObject { ["id"] = id, ["color"] = color.DeepClone() })
                };
                return new BulkStep(number, back, Row(id, row["color"], color, "[undo]"), null);
            }
            case "move.item":
            {
                if (store)
                {
                    return Cannot(number, id, null, "[cannot undo: store move]");
                }

                if (JsonNodes.Text(row["merged_into"]) is { } into)
                {
                    return Cannot(number, id, row["to"]?["id"], $"[cannot undo: merged into {into}]");
                }

                if (row["from"] is not JsonObject from)
                {
                    return Cannot(number, id, row["to"]?["id"], "[cannot undo: no slot it came from]");
                }

                JsonObject back = new()
                {
                    ["moves"] = new JsonArray(new JsonObject
                    {
                        ["id"] = id, ["to"] = from["id"]?.DeepClone(), ["slot"] = from["slot"]?.DeepClone()
                    })
                };
                return new BulkStep(number, back, Row(id, row["to"]?["id"], from["id"], "[undo]"), null);
            }
            default:
                return Cannot(number, id, null, "[cannot undo]");
        }
    }

    private static BulkStep Cannot(int number, string id, JsonNode? left, string mark) =>
        new(number, null, Row(id, left, null, mark), null);

    private static JsonObject Row(string id, JsonNode? current, JsonNode? back, string mark) =>
        VerbPipeline.Row(id, null, null, current, back, mark);
}
