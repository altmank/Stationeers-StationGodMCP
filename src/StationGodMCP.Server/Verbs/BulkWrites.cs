using System.Globalization;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>One bulk write (write-N): the what, what it acted on, and its reply, kept for inspect job and undo.</summary>
internal sealed class BulkWrite(string id, WhatEntry what, JsonNode? reply)
{
    internal string Id { get; } = id;

    internal WhatEntry What { get; } = what;

    internal JsonNode? Reply { get; } = reply;

    internal bool Undone { get; set; }

    /// <summary>inspect {what: "job", id: "write-N"}.</summary>
    internal JsonObject View()
    {
        JsonObject view = new()
        {
            ["job_id"] = Id,
            ["verb"] = What.Verb,
            ["what"] = What.Name,
            ["status"] = Undone ? "undone" : "applied",
            ["steps"] = BulkWrites.Rows(Reply).Count
        };
        JsonArray undo = [];
        foreach (BulkStep step in BulkWrites.Inverse(this))
        {
            undo.Add(step.Row.DeepClone());
        }

        view["undo"] = undo;
        return view;
    }
}

/// <summary>One step of a bulk write's undo: the handler call that puts one target back, and its report row.</summary>
internal sealed class BulkStep(string handler, JsonObject args, JsonObject row)
{
    internal string Handler { get; } = handler;

    internal JsonObject Args { get; } = args;

    internal JsonObject Row { get; } = row;
}

/// <summary>
/// The hub's bulk writes (writes topic): a direct write that acts on a list, a ref or a list of entries is kept as
/// write-N. Its undo writes the previous values back from the reply's rows, newest first: logic values (previous_value),
/// labels (previous custom name), paint (previous_color) and slot moves (back to from); merged moves cannot be undone.
/// </summary>
internal sealed class BulkWrites
{
    internal const string Prefix = "write-";

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

    /// <summary>A reply's rows: its results, or the reply itself when it answers one target.</summary>
    internal static List<JsonObject> Rows(JsonNode? reply)
    {
        List<JsonObject> rows = [];
        if (reply?["results"] is JsonArray results)
        {
            foreach (JsonNode? row in results)
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

    /// <summary>The undo steps, newest first; rows that cannot be undone are left out.</summary>
    internal static List<BulkStep> Inverse(BulkWrite write)
    {
        List<BulkStep> steps = [];
        List<JsonObject> rows = Rows(write.Reply);
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            JsonObject row = rows[index];
            if (row["ok"] is { } ok && !JsonNodes.IsTrue(ok))
            {
                continue;
            }

            string? id = JsonNodes.Text(row["reference_id"]);
            if (id == null || Step(write.What.Key, id, row) is not { } step)
            {
                continue;
            }

            steps.Add(step);
        }

        return steps;
    }

    private static BulkStep? Step(string what, string id, JsonObject row)
    {
        switch (what)
        {
            case "write.logic" when row["previous_value"] is { } previous && JsonNodes.Number(previous) != null:
            {
                JsonNode? logic = row["logic_type"]?["name"] ?? row["logic_type"]?["id"] ?? row["logic_type"];
                JsonObject args = new()
                {
                    ["writes"] = new JsonArray(new JsonObject
                    {
                        ["reference_id"] = id, ["logic_type"] = logic?.DeepClone(), ["value"] = previous.DeepClone()
                    })
                };
                return new BulkStep("write_logic_many", args, Row(id, row["current_value"] ?? row["requested_value"], previous));
            }
            case "write.label" when row["previous"] is JsonObject previous:
            {
                JsonNode? name = previous["custom_name"] ?? JsonValue.Create(string.Empty);
                JsonObject args = new()
                {
                    ["labels"] = new JsonArray(new JsonObject { ["reference_id"] = id, ["name"] = name?.DeepClone() })
                };
                return new BulkStep("label", args, Row(id, row["current"]?["custom_name"], name));
            }
            case "write.paint" when row["previous_color"] is { } color:
            {
                JsonObject args = new()
                {
                    ["items"] = new JsonArray(new JsonObject { ["reference_id"] = id, ["color"] = color.DeepClone() })
                };
                return new BulkStep("paint", args, Row(id, row["color"], color));
            }
            case "move.item" when row["merged_into"] is null && row["from"] is JsonObject from:
            {
                JsonObject args = new()
                {
                    ["moves"] = new JsonArray(new JsonObject
                    {
                        ["reference_id"] = id, ["to_id"] = from["id"]?.DeepClone(), ["to_slot"] = from["slot"]?.DeepClone()
                    }),
                    ["dry_run"] = false
                };
                return new BulkStep("move_item", args, Row(id, row["to"]?["id"], from["id"]));
            }
            default:
                return null;
        }
    }

    private static JsonObject Row(string id, JsonNode? current, JsonNode? back) =>
        new() { ["id"] = id, ["current"] = current?.DeepClone(), ["new"] = back?.DeepClone(), ["undo"] = "[undo]" };
}
