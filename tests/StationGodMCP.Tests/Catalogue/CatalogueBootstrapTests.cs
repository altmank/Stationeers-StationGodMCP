#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// One-shot: writes the catalogue/ sources from the sidecar's tool definitions (STATIONGOD_BOOTSTRAP_CATALOGUE=1).
/// Descriptions and schemas are copied as tools/list publishes them; texts and property schemas many tools share
/// become includes and $refs; classes, costs and the other x- fields come from the tables below (catalogue.md); reply
/// keys from the view classes each handler returns; integer ranges from the handlers' literal bounds; error codes
/// from every refusal in the mod. What it cannot work out is printed for completing by hand.
/// </summary>
public sealed class CatalogueBootstrapTests
{
    private static readonly JsonSerializerOptions Write = CatalogueFiles.WriteOptions;

    [Fact]
    public void Bootstrap()
    {
        string? mode = Environment.GetEnvironmentVariable("STATIONGOD_BOOTSTRAP_CATALOGUE");
        string root = Path.Combine(CatalogueFiles.RepositoryRoot(), "catalogue");
        StringBuilder notes = new StringBuilder();
        if (mode == "errors")
        {
            CatalogueFiles.WriteJson(Path.Combine(root, "errors.json"), Errors(notes));
            return;
        }

        if (mode != "1")
        {
            return;
        }

        JsonSerializerOptions options = (JsonSerializerOptions)typeof(Program)
            .GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        JsonArray tools = JsonSerializer.SerializeToNode(ToolDefinitions.All, options)!.AsArray();

        Dictionary<string, string> texts = SharedTexts(tools);
        foreach ((string file, string text) in texts)
        {
            CatalogueFiles.WriteText(Path.Combine(root, "defs", "text", file + ".txt"), text);
        }

        Dictionary<string, JsonNode> defs = SharedProperties(tools);
        foreach ((string name, JsonNode schema) in defs)
        {
            CatalogueFiles.WriteJson(Path.Combine(root, "defs", name + ".json"), WithIncludes(schema.DeepClone(), texts));
        }

        CatalogueFiles.WriteJson(Path.Combine(root, "server.json"), new JsonObject
        {
            ["name"] = "StationGodMCP",
            ["instructions"] = (string)typeof(Program)
                .GetField("Instructions", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!
        });

        foreach (JsonNode? toolNode in tools)
        {
            JsonObject tool = toolNode!.AsObject();
            string name = (string)tool["name"]!;
            JsonObject method = Method(tool, defs, texts, notes);
            CatalogueFiles.WriteJson(Path.Combine(root, "methods", name + ".json"), method);
        }

        CatalogueFiles.WriteJson(Path.Combine(root, "errors.json"), Errors(notes));
        CatalogueFiles.WriteJson(Path.Combine(root, "shared.json"), SharedReplyKeys());

        string scratch = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "StationGodMCP", "stage2");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "bootstrap-notes.txt"), notes.ToString());
    }

    // ---- methods ----

    private static JsonObject Method(JsonObject tool, Dictionary<string, JsonNode> defs, Dictionary<string, string> texts,
        StringBuilder notes)
    {
        string name = (string)tool["name"]!;
        JsonObject input = tool["inputSchema"]!.DeepClone().AsObject();
        JsonObject properties = input["properties"]!.AsObject();
        properties.Remove(ReplyShaping.OutputFileArgument);
        properties.Remove(ReplyShaping.FieldsArgument);

        Ranges(name, properties, notes);
        foreach (string property in properties.Select(pair => pair.Key).ToList())
        {
            if (defs.TryGetValue(property, out JsonNode? shared) && JsonNode.DeepEquals(shared, properties[property]))
            {
                properties[property] = new JsonObject { ["$ref"] = $"defs/{property}.json" };
            }
        }

        JsonObject method = new JsonObject
        {
            ["name"] = name,
            ["description"] = Description((string)tool["description"]!, texts),
            ["class"] = Classes.TryGetValue(name, out string? cls) ? cls :
                (bool)tool["annotations"]!["readOnlyHint"]! ? "read" : "write"
        };
        if (ClassRules(name) is JsonArray classRules)
        {
            method["x-class-when"] = classRules;
        }

        (string cost, JsonArray? costRules) = Cost(name, properties);
        method["cost"] = cost;
        if (costRules != null)
        {
            method["x-cost-when"] = costRules;
        }

        if (Effects.TryGetValue(name, out string[]? effects))
        {
            method["x-effects"] = new JsonArray(effects.Select(effect => (JsonNode)effect).ToArray());
        }

        method["params"] = WithIncludes(input, texts);
        (JsonObject reply, JsonArray? views) = Reply(name, notes);
        method["reply"] = reply;
        if (views != null)
        {
            method["x-views"] = views;
        }

        method["x-shaping"] = ToolDefinitions.SmallReplies.Contains(name) ? "none" : "lists";
        if (name == "sample_logic")
        {
            method["x-duration"] = new JsonObject { ["param"] = "duration_seconds", ["max_s"] = 30 };
            method["x-runs-in"] = "sidecar";
        }

        if (properties.ContainsKey("job_id") && name != "undo_job")
        {
            method["x-job"] = new JsonObject { ["poll_param"] = "job_id" };
        }

        if (NeedsMod.TryGetValue(name, out string? mod))
        {
            method["x-needs-mod"] = new JsonArray(mod);
        }

        if (ReadBy(name, properties, notes) is JsonObject readBy)
        {
            method["x-read-by"] = readBy;
        }

        if (Aliases.TryGetValue(name, out (string Old, string New) alias))
        {
            method["deprecated_aliases"] = new JsonObject { [alias.Old] = alias.New };
        }

        return method;
    }

    private static (JsonObject, JsonArray?) Reply(string method, StringBuilder notes)
    {
        List<string> views = new List<string>();
        bool complete = true;
        if (Handler.All.TryGetValue(method, out Handler? handler))
        {
            ReplyDerivation derived = ReplyDerivation.Of(handler);
            views.AddRange(derived.Views);
            if (ExtraViews.TryGetValue(method, out string[]? extra))
            {
                views.AddRange(extra.Where(view => !views.Contains(view)));
            }
            else if (derived.Unresolved.Count > 0)
            {
                complete = false;
                notes.AppendLine($"{method}: reply incomplete; unresolved: {string.Join("; ", derived.Unresolved)}");
            }
        }
        else
        {
            complete = false;
            notes.AppendLine($"{method}: no handler in ApiHost; reply by hand");
        }

        JsonObject properties = new JsonObject();
        foreach (string view in views)
        {
            foreach (Newtonsoft.Json.Serialization.JsonProperty property in ViewShapes.Properties(ViewShapes.Find(view)!))
            {
                JsonObject schema = ViewShapes.SchemaOf(property);
                string key = property.PropertyName!;
                if (properties[key] is JsonObject known)
                {
                    properties[key] = MergeTypes(known, schema);
                }
                else
                {
                    properties[key] = schema;
                }
            }
        }

        JsonObject reply = new JsonObject { ["type"] = "object", ["properties"] = properties };
        return (reply, complete && views.Count > 0
            ? new JsonArray(views.Select(view => (JsonNode)view).ToArray())
            : null);
    }

    private static JsonObject MergeTypes(JsonObject a, JsonObject b)
    {
        static IEnumerable<string> Types(JsonObject schema) => schema["type"] switch
        {
            null => Array.Empty<string>(),
            JsonArray list => list.Select(item => (string)item!),
            JsonNode single => new[] { (string)single! }
        };

        if (a["type"] == null || b["type"] == null)
        {
            return new JsonObject();
        }

        List<string> types = Types(a).Concat(Types(b)).Distinct().ToList();
        if (types.Remove("null"))
        {
            types.Add("null");
        }

        return types.Count == 1
            ? new JsonObject { ["type"] = types[0] }
            : new JsonObject { ["type"] = new JsonArray(types.Select(type => (JsonNode)type).ToArray()) };
    }

    // Top-level parameters the handler's files never read through Args, each with the file that names it as a literal
    // (first the handler's own files, then any file under Api/).
    private static JsonObject? ReadBy(string method, JsonObject properties, StringBuilder notes)
    {
        if (!Handler.All.TryGetValue(method, out Handler? handler))
        {
            return null;
        }

        HashSet<string> read = handler.Files().SelectMany(ArgRead.In).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        JsonObject readBy = new JsonObject();
        foreach (string name in properties.Select(pair => pair.Key))
        {
            if (read.Contains(name))
            {
                continue;
            }

            SourceFile? file = handler.Files().Concat(ModSource.Instance.Files.Where(f => f.Path.StartsWith("Api/", StringComparison.Ordinal)))
                .FirstOrDefault(candidate => candidate.NamesLiteral(name));
            if (file == null)
            {
                notes.AppendLine($"{method}: {name} is read nowhere the tests can see");
                continue;
            }

            readBy[name] = file.Path;
        }

        return readBy.Count > 0 ? readBy : null;
    }

    // Integer ranges the handler's files pass as literals: minimum and maximum on the top-level integer property.
    private static void Ranges(string method, JsonObject properties, StringBuilder notes)
    {
        if (!Handler.All.TryGetValue(method, out Handler? handler))
        {
            return;
        }

        foreach ((string name, IntRange range) in IntRange.Expected(handler, line => notes.AppendLine(line)))
        {
            if (properties[name] is JsonObject property && (string?)property["type"] == "integer")
            {
                property["minimum"] = range.Minimum;
                property["maximum"] = range.Maximum;
            }
        }
    }

    // ---- descriptions and shared pieces ----

    // ToolDefinitions' long const texts used in two or more descriptions, by file name.
    private static Dictionary<string, string> SharedTexts(JsonArray tools)
    {
        List<string> descriptions = new List<string>();
        foreach (JsonNode? tool in tools)
        {
            CollectDescriptions(tool!, descriptions);
        }

        Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(ToolDefinitions).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.FieldType != typeof(string))
            {
                continue;
            }

            string text = (string)field.GetRawConstantValue()!;
            if (text.Length >= 100 && descriptions.Count(description => description.Contains(text, StringComparison.Ordinal)) >= 2)
            {
                texts[Snake(field.Name)] = text;
            }
        }

        return texts;
    }

    private static void CollectDescriptions(JsonNode node, List<string> into)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (key == "description" && value is JsonValue text && text.TryGetValue(out string? description))
                {
                    into.Add(description);
                }
                else if (value != null)
                {
                    CollectDescriptions(value, into);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item != null)
                {
                    CollectDescriptions(item, into);
                }
            }
        }
    }

    // Top-level property schemas given identically, under the same name, by three or more tools.
    private static Dictionary<string, JsonNode> SharedProperties(JsonArray tools)
    {
        Dictionary<string, Dictionary<string, (JsonNode Schema, int Count)>> seen =
            new Dictionary<string, Dictionary<string, (JsonNode, int)>>(StringComparer.Ordinal);
        foreach (JsonNode? tool in tools)
        {
            foreach ((string name, JsonNode? schema) in tool!["inputSchema"]!["properties"]!.AsObject())
            {
                if (name == ReplyShaping.OutputFileArgument || name == ReplyShaping.FieldsArgument)
                {
                    continue;
                }

                string key = schema!.ToJsonString();
                if (!seen.TryGetValue(name, out Dictionary<string, (JsonNode, int)>? variants))
                {
                    seen[name] = variants = new Dictionary<string, (JsonNode, int)>(StringComparer.Ordinal);
                }

                variants[key] = variants.TryGetValue(key, out (JsonNode Schema, int Count) known)
                    ? (known.Schema, known.Count + 1)
                    : (schema.DeepClone(), 1);
            }
        }

        Dictionary<string, JsonNode> defs = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach ((string name, Dictionary<string, (JsonNode Schema, int Count)> variants) in seen)
        {
            (JsonNode Schema, int Count) best = variants.Values.OrderByDescending(variant => variant.Count).First();
            if (best.Count >= 3)
            {
                defs[name] = best.Schema;
            }
        }

        return defs;
    }

    // Every description in the schema with the shared texts written as includes.
    private static JsonNode WithIncludes(JsonNode node, Dictionary<string, string> texts)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(pair => pair.Key).ToList())
            {
                JsonNode? value = obj[key];
                if (key == "description" && value is JsonValue text && text.TryGetValue(out string? description))
                {
                    obj[key] = Description(description, texts);
                }
                else if (value != null)
                {
                    obj[key] = WithIncludes(value.DeepClone(), texts);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] != null)
                {
                    array[index] = WithIncludes(array[index]!.DeepClone(), texts);
                }
            }
        }

        return node;
    }

    // A description as a string, or an array of strings and includes where it holds shared texts.
    private static JsonNode Description(string description, Dictionary<string, string> texts)
    {
        List<JsonNode> parts = new List<JsonNode>();
        string rest = description;
        while (rest.Length > 0)
        {
            int at = -1;
            KeyValuePair<string, string> found = default;
            foreach (KeyValuePair<string, string> text in texts.OrderByDescending(pair => pair.Value.Length))
            {
                int index = rest.IndexOf(text.Value, StringComparison.Ordinal);
                if (index >= 0 && (at < 0 || index < at))
                {
                    at = index;
                    found = text;
                }
            }

            if (at < 0)
            {
                parts.Add(rest);
                break;
            }

            if (at > 0)
            {
                parts.Add(rest.Substring(0, at));
            }

            parts.Add(new JsonObject { ["$include"] = $"defs/text/{found.Key}.txt" });
            rest = rest.Substring(at + found.Value.Length);
        }

        return parts.Count == 1 && parts[0] is JsonValue ? JsonValue.Create(description)! : new JsonArray(parts.ToArray());
    }

    private static string Snake(string pascal)
    {
        string trimmed = Regex.Replace(pascal, "(Description|Text)$", string.Empty);
        return Regex.Replace(trimmed, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
    }

    // ---- errors ----

    private static JsonObject Errors(StringBuilder notes)
    {
        SortedDictionary<string, JsonObject> errors = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach ((string code, string description, string resend) in ProtocolCodes)
        {
            errors[code] = Entry(description, "protocol", resend);
        }

        errors["game_unavailable"] = Entry(
            "The sidecar could not reach the mod (no pipe, or another client held it), or the mod took the call and " +
            "sent no reply in time; in that case the call may still have run.", "client", "if_read");

        foreach (ErrorSite site in ErrorSite.All())
        {
            if (!errors.ContainsKey(site.Code))
            {
                errors[site.Code] = Entry(site.Message ?? $"Refused with {site.Code}.", "tool", "never");
            }
        }

        JsonObject result = new JsonObject();
        foreach ((string code, JsonObject entry) in errors)
        {
            result[code] = entry;
        }

        notes.AppendLine($"errors: {errors.Count} codes");
        return result;
    }

    private static JsonObject Entry(string description, string origin, string resend)
    {
        JsonObject entry = new JsonObject { ["description"] = description, ["origin"] = origin };
        if (resend != "never")
        {
            entry["caller_may_resend"] = resend;
        }

        return entry;
    }

    private static readonly (string Code, string Description, string Resend)[] ProtocolCodes =
    {
        ("protocol_error", "A message the protocol does not allow: not a JSON object, unknown type, unknown top-level key, a call before welcome, a repeated hello, sign-in not finished within 10 seconds of connecting. The connection is closed after the reply.", "never"),
        ("unsupported_protocol", "No common major version.", "never"),
        ("unauthorized", "Bad or missing key proof, unknown client name, auth.client not equal to hello.client.name, an anonymous TCP connection, or a key not allowed on this transport; today also the TCP bridge refusing the shared secret. The connection is closed.", "never"),
        ("permission_denied", "The method, at the arguments given, needs a higher level than the connection has; or run_console_command was asked to run a stationgod command. Nothing ran.", "never"),
        ("cheat_not_armed", "The connection's level allows cheat, but the owner has not approved it now. Nothing ran.", "never"),
        ("method_not_found", "No such method.", "never"),
        ("invalid_argument", "The arguments break the catalogue (version 2) or a tool's own reading (both versions). Nothing ran.", "never"),
        ("invalid_shape", "Version 2 only: shape is malformed. Nothing ran.", "never"),
        ("duplicate_id", "The id is already in flight on this connection. Nothing ran.", "never"),
        ("too_many_in_flight", "More calls in flight than limits.max_in_flight. Nothing ran.", "yes"),
        ("request_too_large", "A message longer than limits.max_request_bytes. The connection is closed.", "never"),
        ("reply_too_large", "The reply would exceed shape.max_bytes or limits.max_reply_bytes. The method ran.", "if_read"),
        ("game_timeout", "The call was not started before its deadline. Nothing ran.", "yes"),
        ("cancelled", "Cancelled before it started. Nothing ran.", "yes"),
        ("shutting_down", "The mod is stopping or the world is unloading; the call was not started.", "yes"),
        ("game_changed", "A game member this method needs is missing in this game build.", "never"),
        ("internal_error", "A bug. The method may have partly run.", "never"),
        ("subscription_limit", "A subscription would exceed a per-subscription, connection or global limit; poll instead.", "never"),
        ("unknown_subscription", "unsubscribe of an id this connection does not have.", "never")
    };

    private static JsonObject SharedReplyKeys() => new JsonObject
    {
        ["shared_reply_keys"] = new JsonArray(
            new JsonObject
            {
                ["key"] = "resolved_networks",
                ["schema"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The network handles this request resolved, when any: {argument, given, network_id} each (a plain network id that names the network itself is not listed)."
                },
                ["applies_when"] = "takes_network_handles"
            },
            new JsonObject
            {
                ["key"] = "gas_hold",
                ["schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Present when a gas hold applies to the run, or acknowledge_gas_lost was given: whether the hold applies and what acknowledging lifts."
                },
                ["applies_when"] = "may_touch_pipe_networks"
            })
    };

    // ---- the tables (catalogue.md, Method classes and costs) ----

    private static readonly Dictionary<string, string> Classes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["run_console_command"] = "cheat",
        ["move_gas"] = "cheat",
        ["write_memory"] = "cheat",
        ["plant_genes"] = "cheat",
        ["paste_blueprint"] = "cheat"
    };

    private static readonly string[] DryRunByDefault =
    {
        "place_cables", "place_pipes", "place_chutes", "remove_cables", "remove_pipes", "remove_chutes",
        "upgrade_cables", "upgrade_pipes", "clean_cables", "clean_pipes", "replace_walls", "replace_frames",
        "remove_structure", "undo_job", "vault_deposit", "vault_withdraw"
    };

    private static JsonArray? ClassRules(string method)
    {
        static JsonObject Rule(JsonObject when, string cls) => new JsonObject { ["when"] = when, ["class"] = cls };
        static JsonObject Is(string name, JsonNode matcher) => new JsonObject { [name] = matcher };
        static JsonObject Equal(JsonNode value) => new JsonObject { ["equals"] = value };
        static JsonObject Absent() => new JsonObject { ["absent"] = true };
        static JsonObject Present() => new JsonObject { ["present"] = true };

        if (method == "place_structure")
        {
            return new JsonArray(
                Rule(new JsonObject { ["free"] = Equal(true), ["dry_run"] = Equal(false) }, "cheat"),
                Rule(Is("dry_run", Absent()), "read"),
                Rule(Is("dry_run", Equal(true)), "read"));
        }

        if (DryRunByDefault.Contains(method))
        {
            return new JsonArray(Rule(Is("dry_run", Absent()), "read"), Rule(Is("dry_run", Equal(true)), "read"));
        }

        return method switch
        {
            "move_gas" => new JsonArray(Rule(Is("dry_run", Equal(true)), "read"), Rule(Is("transfer_id", Present()), "read")),
            "trader_buy" or "trader_sell" => new JsonArray(Rule(Is("dry_run", Equal(true)), "read")),
            "plant_genes" => new JsonArray(Rule(Is("genes", Absent()), "read")),
            "paste_blueprint" => new JsonArray(Rule(Is("status", Equal(true)), "read")),
            "rocket_flight_log" => new JsonArray(Rule(Is("action", Absent()), "read"),
                Rule(Is("action", new JsonObject { ["in"] = new JsonArray("read", "list") }), "read")),
            _ => null
        };
    }

    private static readonly HashSet<string> WorldScans = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_devices", "find_things", "find_items", "item_totals", "list_containers", "thing_health", "grid_survey",
        "lint_layout", "rooms", "plants", "outer_frames", "deep_miner_spots", "wall_map", "consumables",
        "water_sources", "ignition_risk", "network_snapshot"
    };

    private static readonly HashSet<string> Planners = new HashSet<string>(StringComparer.Ordinal)
    {
        "plan_cable_route", "plan_pipe_route", "plan_chute_route", "plan_removal", "find_spot", "rocket_forecast",
        "feed_paths"
    };

    private static readonly Dictionary<string, string> Batches = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["read_logic_many"] = "reads",
        ["write_logic_many"] = "writes",
        ["read_devices"] = "items",
        ["check_replaceable"] = "reference_ids"
    };

    private static (string, JsonArray?) Cost(string method, JsonObject properties)
    {
        static JsonObject Rule(JsonObject when, string cost, string? perItem = null)
        {
            JsonObject rule = new JsonObject { ["when"] = when, ["cost"] = cost };
            if (perItem != null)
            {
                rule["per_item"] = perItem;
            }

            return rule;
        }

        static JsonObject Is(string name, JsonNode matcher) => new JsonObject { [name] = matcher };
        static JsonObject Present() => new JsonObject { ["present"] = true };

        if (method == "sample_logic")
        {
            return ("stream", null);
        }

        if (Batches.TryGetValue(method, out string? items))
        {
            return ("bounded", new JsonArray(Rule(Is(items, Present()), "bounded", items)));
        }

        if (method == "thing_health")
        {
            return ("world", new JsonArray(
                Rule(Is("reference_ids", Present()), "bounded", "reference_ids"),
                Rule(Is("reference_id", Present()), "instant"),
                Rule(Is("network_id", Present()), "bounded")));
        }

        if (method == "list_devices")
        {
            return ("world", new JsonArray(Rule(Is("prefab_hash", Present()), "bounded"),
                Rule(Is("name_contains", Present()), "bounded")));
        }

        if (method == "rooms")
        {
            return ("world", new JsonArray(Rule(Is("reference_id", Present()), "bounded")));
        }

        if (WorldScans.Contains(method))
        {
            return ("world", null);
        }

        if (Planners.Contains(method))
        {
            return ("plan", null);
        }

        if (method == "undo_job")
        {
            return ("job", new JsonArray(Rule(Is("dry_run", new JsonObject { ["absent"] = true }), "plan"),
                Rule(Is("dry_run", new JsonObject { ["equals"] = true }), "plan")));
        }

        if (method == "place_structure" || DryRunByDefault.Contains(method) && properties.ContainsKey("job_id"))
        {
            return ("job", new JsonArray(Rule(Is("job_id", Present()), "instant"),
                Rule(Is("dry_run", new JsonObject { ["absent"] = true }), "plan"),
                Rule(Is("dry_run", new JsonObject { ["equals"] = true }), "plan")));
        }

        if (method == "paste_blueprint")
        {
            return ("job", new JsonArray(Rule(Is("status", new JsonObject { ["equals"] = true }), "instant")));
        }

        if (method == "connections" || method == "plant_genes")
        {
            return ("bounded", null);
        }

        return ("instant", null);
    }

    private static readonly Dictionary<string, string[]> Effects = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["highlight"] = new[] { "display" },
        ["show_preview"] = new[] { "display" },
        ["rocket_flight_log"] = new[] { "server_state", "files" }
    };

    private static readonly Dictionary<string, string> NeedsMod = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["paste_blueprint"] = "BlueprintMod",
        ["vault_contents"] = "IngotVault",
        ["vault_deposit"] = "IngotVault",
        ["vault_withdraw"] = "IngotVault"
    };

    private static readonly Dictionary<string, (string Old, string New)> Aliases =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["thing_health"] = ("min_ratio", "min_damage_ratio"),
            ["water_sources"] = ("min_moles", "min_mol")
        };

    // Views the handlers reach through code the derivation cannot follow (the job slot's stored views).
    private static readonly Dictionary<string, string[]> ExtraViews = Build();

    private static Dictionary<string, string[]> Build()
    {
        Dictionary<string, string[]> extra = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string run in new[] { "place_cables", "place_pipes", "place_chutes", "remove_cables", "remove_pipes", "remove_chutes" })
        {
            extra[run] = new[] { "RunJobView", "JobQueuedView", "JobBusyView", "JobDroppedView" };
        }

        foreach (string build in new[] { "place_structure", "remove_structure" })
        {
            extra[build] = new[] { "BuildJobView", "JobQueuedView", "JobBusyView", "JobDroppedView" };
        }

        foreach (string upgrade in new[] { "upgrade_cables", "upgrade_pipes", "clean_cables", "clean_pipes" })
        {
            extra[upgrade] = new[] { "UpgradeJobView", "JobQueuedView", "JobBusyView", "JobDroppedView" };
        }

        foreach (string swap in new[] { "replace_walls", "replace_frames" })
        {
            extra[swap] = new[] { "StructureSwapJobView", "JobQueuedView", "JobBusyView", "JobDroppedView" };
        }

        extra["label"] = new[] { "BatchResultView" };
        return extra;
    }
}

/// <summary>An integer argument read with literal bounds (OptionalInt("limit", 1, 500)).</summary>
internal sealed class IntRange
{
    private IntRange(string name, long minimum, long maximum, ArgRead read)
    {
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
        Read = read;
    }

    internal string Name { get; }

    internal long Minimum { get; }

    internal long Maximum { get; }

    internal ArgRead Read { get; }

    public override string ToString() => $"{Minimum}..{Maximum} at {Read.File.Path}:{Read.Line}";

    /// <summary>
    /// The bounds a method's integer arguments are read with: the handler file's own read when it has one, else the
    /// one bound every other file of the handler's agrees on. Several different bounds for a name in the handler file,
    /// or none agreed elsewhere, give no bound for it (each is reported through conflict).
    /// </summary>
    internal static Dictionary<string, IntRange> Expected(Handler handler, Action<string> conflict)
    {
        Dictionary<string, IntRange> expected = new Dictionary<string, IntRange>(StringComparer.Ordinal);
        foreach (IGrouping<string, IntRange> group in In(handler).GroupBy(range => range.Name))
        {
            List<IntRange> own = group.Where(range => range.Read.File == handler.File).ToList();
            List<IntRange> candidates = own.Count > 0 ? own : group.ToList();
            List<IntRange> distinct = candidates.GroupBy(range => (range.Minimum, range.Maximum)).Select(g => g.First()).ToList();
            if (distinct.Count == 1)
            {
                expected[group.Key] = distinct[0];
            }
            else
            {
                conflict($"{handler.Method}: {group.Key} read with several ranges: {string.Join("; ", distinct)}");
            }
        }

        return expected;
    }

    /// <summary>Every OptionalInt / Int read with both bounds resolvable, in the handler's files.</summary>
    internal static List<IntRange> In(Handler handler)
    {
        List<IntRange> ranges = new List<IntRange>();
        foreach (SourceFile file in handler.Files())
        {
            foreach (ArgRead read in ArgRead.In(file))
            {
                if ((read.Reader != "OptionalInt" && read.Reader != "Int") || read.Call.ArgumentList.Arguments.Count < 3)
                {
                    continue;
                }

                long? minimum = ModSource.IntegerOf(read.Call.ArgumentList.Arguments[1].Expression, file);
                long? maximum = ModSource.IntegerOf(read.Call.ArgumentList.Arguments[2].Expression, file);
                if (minimum.HasValue && maximum.HasValue)
                {
                    ranges.Add(new IntRange(read.Name, minimum.Value, maximum.Value, read));
                }
            }

            // PageRequest.From(args, defaultLimit, maximumLimit) reads limit from 1 to its maximum (Api/Shared/Paging.cs).
            foreach (InvocationExpressionSyntax call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "PageRequest" }, Name.Identifier.Text: "From" } &&
                    call.ArgumentList.Arguments.Count == 3 &&
                    ModSource.IntegerOf(call.ArgumentList.Arguments[2].Expression, file) is long maximumLimit)
                {
                    ranges.Add(new IntRange("limit", 1, maximumLimit, new ArgRead("limit", "PageRequest.From", file, call)));
                }
            }
        }

        return ranges;
    }
}

/// <summary>A place the mod refuses with an error code: the code, and the message text there when it can be read.</summary>
internal sealed class ErrorSite
{
    private ErrorSite(string code, string? message, string where)
    {
        Code = code;
        Message = message;
        Where = where;
    }

    internal string Code { get; }

    internal string? Message { get; }

    internal string Where { get; }

    /// <summary>
    /// Every code passed as a literal or const string to ApiErrors.Refused, new ApiException or new ErrorView (both
    /// branches of a conditional), every const string whose name ends in Code, and every Code property returning a
    /// literal (GasHoldRule's verdicts, RunKind.ShortageCode).
    /// </summary>
    internal static List<ErrorSite> All()
    {
        List<ErrorSite> sites = new List<ErrorSite>();
        ModSource source = ModSource.Instance;
        foreach (SourceFile file in source.Files)
        {
            foreach (SyntaxNode node in file.Root.DescendantNodes())
            {
                ArgumentListSyntax? arguments = node switch
                {
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Refused" } } call =>
                        call.ArgumentList,
                    ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.Text: "ApiException" or "ErrorView" } } creation =>
                        creation.ArgumentList,
                    _ => null
                };
                if (arguments == null || arguments.Arguments.Count < 1)
                {
                    continue;
                }

                string? message = arguments.Arguments.Count > 1 ? MessageOf(arguments.Arguments[1].Expression) : null;
                foreach (ExpressionSyntax code in Branches(arguments.Arguments[0].Expression))
                {
                    if (source.StringOf(code) is string text)
                    {
                        sites.Add(new ErrorSite(text, message, $"{file.Path}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
                    }
                }
            }

            foreach (VariableDeclaratorSyntax variable in file.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (variable.Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword) &&
                    variable.Identifier.Text.EndsWith("Code", StringComparison.Ordinal) &&
                    variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    sites.Add(new ErrorSite(literal.Token.ValueText, null, file.Path));
                }
            }

            foreach (PropertyDeclarationSyntax property in file.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (property.Identifier.Text.EndsWith("Code", StringComparison.Ordinal) &&
                    property.ExpressionBody?.Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    sites.Add(new ErrorSite(literal.Token.ValueText, null, file.Path));
                }
            }
        }

        return sites;
    }

    private static IEnumerable<ExpressionSyntax> Branches(ExpressionSyntax expression) => expression switch
    {
        ConditionalExpressionSyntax conditional => Branches(conditional.WhenTrue).Concat(Branches(conditional.WhenFalse)),
        ParenthesizedExpressionSyntax parenthesized => Branches(parenthesized.Expression),
        _ => new[] { expression }
    };

    // The message as written, interpolation holes shown as <name>; null when it is not literal text.
    private static string? MessageOf(ExpressionSyntax expression) => RawMessageOf(expression)?.Trim();

    private static string? RawMessageOf(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(content =>
            content is InterpolatedStringTextSyntax plain ? plain.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}") : $"<{Hole(((InterpolationSyntax)content).Expression)}>")),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum =>
            RawMessageOf(sum.Left) is string left && RawMessageOf(sum.Right) is string right ? left + right : null,
        ParenthesizedExpressionSyntax parenthesized => RawMessageOf(parenthesized.Expression),
        _ => null
    };

    // A hole by the name it reads (id, ReferenceId as reference_id), or an ellipsis for anything computed.
    private static string Hole(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => Snake(name.Identifier.Text),
        MemberAccessExpressionSyntax member when member.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax
                                                 or ThisExpressionSyntax => Snake(member.Name.Identifier.Text),
        _ => "…"
    };

    private static string Snake(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
