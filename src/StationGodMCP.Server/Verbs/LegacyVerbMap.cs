using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Translates a checked verb call into the mod's handler calls (catalogue/verbs, handlers). A what with one handler maps its options by the argument table; the whats with several choose by their
/// English when, one method each below, named after the what. Every handler call carries the rename envelope built from
/// the same table, so the mod's messages come back in verb names.
/// </summary>
internal sealed class LegacyVerbMap
{
    internal const string RunsPiece = "lands with the runs piece";

    private static readonly string[] JobPrefixes = ["place-", "remove-", "run-", "replace-", "upgrade-"];

    private readonly VerbCatalogue _catalogue;
    private readonly HandlerSchemas _schemas;
    private readonly ArgMapper _mapper;
    private readonly RenameEnvelope _rename;

    internal LegacyVerbMap(VerbCatalogue catalogue, HandlerSchemas schemas)
    {
        _catalogue = catalogue;
        _schemas = schemas;
        _mapper = new ArgMapper(catalogue, schemas);
        _rename = new RenameEnvelope(catalogue, schemas);
    }

    internal RenameEnvelope Rename => _rename;

    /// <summary>The handler calls for one checked call; a list id has been split before this where the what splits.</summary>
    internal Translation Translate(WhatEntry what, JsonObject options) => what.Key switch
    {
        "find.item" => FindItem(what, options),
        "find.device" => FindDevice(what, options),
        "inspect.chip" => InspectChip(what, options),
        "inspect.job" => InspectJob(what, options),
        "read.logic" => ReadLogic(what, options),
        "read.sample" => ReadSample(what, options),
        "write.logic" => WriteLogic(what, options),
        "write.chip" => WriteChip(what, options),
        "write.label" => WriteLabel(what, options),
        "plan.route" => PlanRoute(what, options),
        "plan.aim" => PlanAim(what, options),
        "swap.cable" => Swap(what, options, "upgrade_cables", "clean_cables"),
        "swap.pipe" => Swap(what, options, "upgrade_pipes", "clean_pipes"),
        "move.item" => MoveItem(what, options),
        "inspect.controllers" => InspectControllers(what, options),
        "undo" => Undo(what, options),
        _ => Single(what, options)
    };

    // ---- the whats with one handler ------------------------------------------------------------------------------

    private Translation Single(WhatEntry what, JsonObject options)
    {
        if (what.Handlers.Count != 1)
        {
            throw new InvalidOperationException($"{what.Label} has no handler choice.");
        }

        HandlerEntry handler = what.Handlers[0];
        if (handler.IsHub)
        {
            return NotAvailable($"{what.Label} {RunsPiece}");
        }

        if (handler.Pending != null || !_schemas.Has(handler.Name))
        {
            return NotAvailable($"{what.Label} is not in this mod version");
        }

        return Call(what, handler.Name, options);
    }

    /// <summary>One handler call for the options, or the refusal that lists what the handler cannot take.</summary>
    internal Translation Call(WhatEntry what, string handlerName, JsonObject options, string? part = null)
    {
        HandlerEntry handler = what.Handler(handlerName) ??
                               throw new InvalidOperationException($"{what.Label} has no such form.");
        MappedArgs mapped = _mapper.Map(what, handler, options);
        if (!mapped.TakesShaping)
        {
            foreach (string shaping in ArgMapper.ShapingArgs)
            {
                if (options[shaping] != null)
                {
                    mapped.Problems.Add(new OptionProblem(shaping, $"not taken by {what.Label} (its reply stays small)"));
                }
            }
        }

        if (mapped.Problems.Count > 0)
        {
            return new Translation.Answered(OptionProblem.Refusal(what.Label, mapped.Problems));
        }

        if (mapped.Unavailable.Count > 0)
        {
            return NotAvailable($"{what.Label}: {string.Join(", ", mapped.Unavailable)} lands with the verb layer; " +
                                "today's handler takes no such option");
        }

        return new Translation.Calls(new HandlerCall(handlerName, mapped.Args, _rename.For(what, handlerName))
        {
            Part = part,
            Files = FilesOf(handler)
        });
    }

    // A handler's file arguments in the caller's names (source_file into source: code_file into code).
    private List<VerbFileArgument> FilesOf(HandlerEntry handler)
    {
        List<VerbFileArgument> files = [];
        foreach (FileArgument file in _schemas.Files(handler.Name))
        {
            string? option = null;
            string? into = null;
            foreach (ArgEntry entry in handler.Args)
            {
                if (entry.Arg == file.Name)
                {
                    option = entry.Option;
                }
                else if (entry.Arg == file.Into)
                {
                    into = entry.Option;
                }
            }

            if (option != null && into != null)
            {
                files.Add(new VerbFileArgument(file, option, into));
            }
        }

        return files;
    }

    private static Translation NotAvailable(string message) =>
        new Translation.Answered(new VerbReply.Failed(VerbFailure.NotAvailable, message + "."));

    private static Translation Invalid(WhatEntry what, string path, string problem) =>
        new Translation.Answered(OptionProblem.Refusal(what.Label, [new OptionProblem(path, problem)]));

    // ---- the whats with several handlers, and the ones whose options need shaping first ---------------------------

    /// <summary>group_by place sums per prefab and place (item_totals); a list or the other groupings is find_items.</summary>
    internal Translation FindItem(WhatEntry what, JsonObject options) =>
        JsonNodes.Text(options["group_by"]) == "place"
            ? Call(what, "item_totals", Without(options, "group_by"))
            : Call(what, "find_items", options);

    /// <summary>
    /// list_devices walks every device and takes a prefab name or hash only: prefab parts and paging are applied here to
    /// its whole list, as the catalogue declares.
    /// </summary>
    internal Translation FindDevice(WhatEntry what, JsonObject options)
    {
        JsonObject sent = Copy(options);
        List<string>? parts = null;
        if (JsonNodes.Text(options["prefab"]) is { } prefab && prefab.Contains('*'))
        {
            parts = ArgMapper.PrefabParts(prefab);
            sent.Remove("prefab");
        }

        int offset = (int)(JsonNodes.Number(options["offset"]) ?? 0);
        int? limit = JsonNodes.Number(options["limit"]) is { } given ? (int)given : null;
        sent.Remove("offset");
        sent.Remove("limit");
        Translation translation = Call(what, "list_devices", sent);
        if (translation is not Translation.Calls calls || (parts == null && offset == 0 && limit == null))
        {
            return translation;
        }

        HandlerCall call = calls.List[0];
        return new Translation.Calls(new HandlerCall(call.Handler, call.Args, call.Rename)
        {
            Reshape = reply => DevicePage(reply, parts, offset, limit)
        });
    }

    private static JsonNode? DevicePage(JsonNode? reply, List<string>? parts, int offset, int? limit)
    {
        if (reply is not JsonObject whole || whole["devices"] is not JsonArray devices)
        {
            return reply;
        }

        List<JsonNode?> kept = [];
        foreach (JsonNode? device in devices)
        {
            if (parts == null || HoldsEveryPart(JsonNodes.Text(device?["prefab_name"]), parts))
            {
                kept.Add(device);
            }
        }

        JsonArray page = [];
        int end = limit is { } most ? Math.Min(kept.Count, offset + most) : kept.Count;
        for (int index = offset; index < end; index++)
        {
            page.Add(kept[index]?.DeepClone());
        }

        JsonObject shaped = (JsonObject)whole.DeepClone();
        shaped["devices"] = page;
        shaped["count"] = page.Count;
        shaped["total"] = kept.Count;
        shaped["offset"] = offset;
        shaped["has_more"] = end < kept.Count;
        return shaped;
    }

    private static bool HoldsEveryPart(string? prefab, List<string> parts)
    {
        if (prefab == null)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!prefab.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One call per part asked (status, source, selectors; status when none is): one part answers its reply as it is,
    /// several answer {status, source, selectors}.
    /// </summary>
    internal Translation InspectChip(WhatEntry what, JsonObject options)
    {
        List<string> parts = [];
        if (options["parts"] is JsonArray asked)
        {
            foreach (JsonNode? part in asked)
            {
                if (JsonNodes.Text(part)?.Trim().ToLowerInvariant() is { } name && !parts.Contains(name))
                {
                    parts.Add(name);
                }
            }
        }

        if (parts.Count == 0)
        {
            parts.Add("status");
        }

        List<HandlerCall> calls = [];
        foreach (string part in parts)
        {
            string handler = part switch
            {
                "source" => "get_ic_source",
                "selectors" => "resolve_ic_selectors",
                _ => "get_ic_status"
            };
            // Each part is its own call, so status never carries the source (parts: ["status", "source"] would ask
            // get_ic_status for it): the source part answers it.
            JsonObject partOptions = OnlyFor(what, handler, options);
            partOptions.Remove("parts");
            Translation one = Call(what, handler, partOptions, part);
            if (one is not Translation.Calls made)
            {
                return one;
            }

            calls.Add(made.List[0]);
        }

        return new Translation.Calls(calls);
    }

    /// <summary>
    /// A job id polls the job (place-, remove-, run-, replace-, upgrade-) until it is final or max_wait passes; "paste"
    /// is the last blueprint paste's status; script-N and write-N runs and the list of runs come with the runs and refs
    /// pieces; any other id is a gas move's transfer id.
    /// </summary>
    internal Translation InspectJob(WhatEntry what, JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id == null)
        {
            return NotAvailable($"inspect job with no id (the runs with notices) {RunsPiece}");
        }

        if (id.StartsWith("script-", StringComparison.OrdinalIgnoreCase))
        {
            return NotAvailable($"inspect job for a script run {RunsPiece}");
        }

        if (id.StartsWith("write-", StringComparison.OrdinalIgnoreCase))
        {
            return NotAvailable("inspect job for a bulk write lands with the refs piece");
        }

        if (string.Equals(id, "paste", StringComparison.OrdinalIgnoreCase))
        {
            return new Translation.Calls(new HandlerCall("paste_blueprint", new JsonObject { ["status"] = true },
                _rename.For(what, "paste_blueprint")));
        }

        if (JobPoller(id) is { } poller)
        {
            JsonObject args = new() { ["job_id"] = id };
            if (options["verbose"] is { } verbose)
            {
                args["verbose"] = verbose.DeepClone();
            }

            return new Translation.Calls(new HandlerCall(poller, args, _rename.For(what, poller)) { PollsJob = true });
        }

        return Call(what, "move_gas", options);
    }

    /// <summary>
    /// The handler that polls a job by its id's prefix (every job tool reads the one job registry; each answers the
    /// brief view of its own kind); null for an id that is not a job's.
    /// </summary>
    internal static string? JobPoller(string id)
    {
        foreach (string prefix in JobPrefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix switch
                {
                    "run-" => "place_cables",
                    "replace-" => "replace_walls",
                    "upgrade-" => "upgrade_cables",
                    _ => "place_structure"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// One id and one logic type: read_logic. Id and logic lists, or reads that are all {id, logic} pairs:
    /// read_logic_many, one row per pair. Any read with a logic list, slots, atmosphere or reagents, or include:
    /// read_devices, one row per item.
    /// </summary>
    internal Translation ReadLogic(WhatEntry what, JsonObject options)
    {
        JsonNode? ids = options["id"];
        JsonNode? logic = options["logic"];
        bool items = options["include"] != null;
        if (options["reads"] is JsonArray reads)
        {
            foreach (JsonNode? read in reads)
            {
                if (read is JsonObject entry && (entry["logic"] is JsonArray || entry["slots"] != null ||
                                                 entry["atmosphere"] != null || entry["reagents"] != null))
                {
                    items = true;
                }
            }

            return items ? Call(what, "read_devices", options) : Call(what, "read_logic_many", options);
        }

        if (ids == null || logic == null)
        {
            return Invalid(what, ids == null ? "id" : "logic", "required (or reads)");
        }

        if (!items && ids is not JsonArray && logic is not JsonArray)
        {
            return Call(what, "read_logic", options);
        }

        JsonObject built = Without(options, "id", "logic");
        JsonArray pairs = [];
        foreach (JsonNode? id in AsList(ids))
        {
            if (items)
            {
                pairs.Add(new JsonObject { ["id"] = id?.DeepClone(), ["logic"] = AsList(logic) });
                continue;
            }

            foreach (JsonNode? type in AsList(logic))
            {
                pairs.Add(new JsonObject { ["id"] = id?.DeepClone(), ["logic"] = type?.DeepClone() });
            }
        }

        built["reads"] = pairs;
        return Call(what, items ? "read_devices" : "read_logic_many", built);
    }

    /// <summary>sample_logic takes {id, logic} targets: id and logic (one or lists) become every pair.</summary>
    internal Translation ReadSample(WhatEntry what, JsonObject options)
    {
        if (options["reads"] != null)
        {
            return options["id"] != null || options["logic"] != null
                ? Invalid(what, options["id"] != null ? "id" : "logic", "give reads, or id with logic, not both")
                : Single(what, options);
        }

        if (options["id"] == null || options["logic"] == null)
        {
            return Invalid(what, options["id"] == null ? "id" : "logic", "required with " +
                (options["id"] == null ? "logic" : "id") + " (or reads)");
        }

        JsonObject built = Without(options, "id", "logic");
        JsonArray pairs = [];
        foreach (JsonNode? id in AsList(options["id"]!))
        {
            foreach (JsonNode? type in AsList(options["logic"]!))
            {
                pairs.Add(new JsonObject { ["id"] = id?.DeepClone(), ["logic"] = type?.DeepClone() });
            }
        }

        built["reads"] = pairs;
        return Single(what, built);
    }

    /// <summary>
    /// The controllers index is a protocol method of the mod (inspect_controllers, protocol.md): one device per call
    /// (x-list split).
    /// </summary>
    internal Translation InspectControllers(WhatEntry what, JsonObject options)
    {
        if (JsonNodes.Text(options["id"]) is not { } id)
        {
            return Invalid(what, "id", "required");
        }

        return new Translation.Calls(new HandlerCall("inspect_controllers", new JsonObject { ["reference_id"] = id },
            _rename.For(what, "controllers")));
    }

    /// <summary>One id with logic and value: write_logic. writes, or a list of ids: write_logic_many, in order.</summary>
    internal Translation WriteLogic(WhatEntry what, JsonObject options)
    {
        if (options["writes"] != null)
        {
            return Call(what, "write_logic_many", options);
        }

        if (options["id"] is not JsonArray ids)
        {
            return Call(what, "write_logic", options);
        }

        JsonObject built = Without(options, "id", "logic", "value");
        JsonArray writes = [];
        foreach (JsonNode? id in ids)
        {
            writes.Add(new JsonObject
            {
                ["id"] = id?.DeepClone(),
                ["logic"] = options["logic"]?.DeepClone(),
                ["value"] = options["value"]?.DeepClone()
            });
        }

        built["writes"] = writes;
        return Call(what, "write_logic_many", built);
    }

    /// <summary>label takes one id and name, or labels: a list of ids, all given the one label, become labels.</summary>
    internal Translation WriteLabel(WhatEntry what, JsonObject options)
    {
        if (options["id"] is not JsonArray ids)
        {
            return Single(what, options);
        }

        JsonObject built = Without(options, "id", "label");
        JsonArray labels = options["labels"] is JsonArray given ? (JsonArray)given.DeepClone() : [];
        foreach (JsonNode? id in ids)
        {
            labels.Add(new JsonObject { ["id"] = id?.DeepClone(), ["label"] = options["label"]?.DeepClone() });
        }

        built["labels"] = labels;
        return Single(what, built);
    }

    /// <summary>
    /// code (or code_file) sets the source, pins the pins, run the execution, in that order; one answers its reply,
    /// several answer {code, pins, run}, the first failure stopping the rest.
    /// </summary>
    internal Translation WriteChip(WhatEntry what, JsonObject options)
    {
        List<(string Part, string Handler)> parts = [];
        if (options["code"] != null || options["code_file"] != null)
        {
            parts.Add(("code", "set_ic_source"));
        }

        if (options["pins"] != null)
        {
            parts.Add(("pins", "set_ic_pins"));
        }

        if (options["run"] != null)
        {
            parts.Add(("run", "control_ic_execution"));
        }

        if (parts.Count == 0)
        {
            return Invalid(what, "code", "code, pins or run is required");
        }

        List<HandlerCall> calls = [];
        foreach ((string part, string handler) in parts)
        {
            Translation one = Call(what, handler, OnlyFor(what, handler, options), part);
            if (one is not Translation.Calls made)
            {
                return one;
            }

            calls.Add(made.List[0]);
        }

        return new Translation.Calls(calls);
    }

    /// <summary>kind (required) picks the planner: cable, pipe or chute.</summary>
    internal Translation PlanRoute(WhatEntry what, JsonObject options) =>
        Call(what, $"plan_{JsonNodes.Text(options["kind"])!.Trim().ToLowerInvariant()}_route", Without(options, "kind"));

    /// <summary>contact means a dish; otherwise a solar panel, and a dish when the mod says the id is not a panel.</summary>
    internal Translation PlanAim(WhatEntry what, JsonObject options)
    {
        if (options["contact"] != null)
        {
            return Call(what, "dish_aim", options);
        }

        Translation panelCall = Call(what, "solar_aim", options);
        if (panelCall is not Translation.Calls solar)
        {
            return panelCall;
        }

        HandlerCall? dish = Call(what, "dish_aim", options) is Translation.Calls dishCalls ? dishCalls.List[0] : null;
        HandlerCall panel = solar.List[0];
        return new Translation.Calls(new HandlerCall(panel.Handler, panel.Args, panel.Rename)
        {
            Fallback = dish,
            FallbackCode = "not_solar_panel"
        });
    }

    /// <summary>into upgrades, tidy cleans (true is [simplify_junctions]); one of them, not both.</summary>
    internal Translation Swap(WhatEntry what, JsonObject options, string upgrade, string clean)
    {
        bool into = options["into"] != null;
        JsonNode? tidy = options["tidy"];
        bool tidies = tidy != null && !(tidy is JsonValue && !JsonNodes.IsTrue(tidy));
        if (into && tidies)
        {
            return Invalid(what, "tidy", "give into or tidy, not both");
        }

        if (!into && !tidies)
        {
            return Invalid(what, "into", "into or tidy is required");
        }

        if (into)
        {
            return Call(what, upgrade, Without(options, "tidy"));
        }

        JsonObject cleaning = Copy(options);
        if (JsonNodes.IsTrue(tidy))
        {
            cleaning["tidy"] = new JsonArray(JsonValue.Create("simplify_junctions"));
        }

        return Call(what, clean, cleaning);
    }

    /// <summary>
    /// slot or moves: a slot move. Otherwise a vault or silo as to or source makes a store move (into, out of, vault to
    /// vault), which needs to know what those ids are: the hub reads each one's prefab (thing_health) first. Everything
    /// else is a slot move.
    /// </summary>
    internal Translation MoveItem(WhatEntry what, JsonObject options)
    {
        if (options["slot"] != null || options["moves"] != null)
        {
            return SlotMove(what, options);
        }

        string? source = JsonNodes.Text(options["source"]);
        string? to = JsonNodes.Text(options["to"]);
        bool toPlayer = IsPlayer(to);
        if (source != null)
        {
            return KindOf(what, source, from => to == null || toPlayer
                ? Withdraw(what, options, from, null)
                : KindOf(what, to, into => Withdraw(what, options, from, into)));
        }

        if (to != null && !toPlayer)
        {
            return KindOf(what, to, into => Deposit(what, options, into));
        }

        return SlotMove(what, options);
    }

    private static bool IsPlayer(string? id) =>
        id != null && string.Equals(id.Trim(), "player", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A slot move. move_item's to_id is a thing's id: "player" (as to, or a move's to) is resolved to the player's id
    /// first (player_vitals). force goes with one item only: moves carry no force.
    /// </summary>
    private Translation SlotMove(WhatEntry what, JsonObject options)
    {
        bool many = options["id"] is JsonArray || options["moves"] is JsonArray;
        if (JsonNodes.IsTrue(options["force"]) && many)
        {
            return Invalid(what, "force", "takes one item (one id, no moves): each forced move is its own call");
        }

        if (!NamesPlayer(options))
        {
            return Call(what, "move_item", ListShaped(what, options));
        }

        HandlerCall read = new("player_vitals", new JsonObject(), _rename.For(what, "player_vitals"));
        return new Translation.Probe(read, reply => reply switch
        {
            GameReply.Ok ok when JsonNodes.Text(JsonNodes.FromElement(ok.Result)?["reference_id"]) is { } player =>
                Call(what, "move_item", ListShaped(what, WithPlayer(options, player))),
            GameReply.Ok => NotAvailable($"{what.Label}: the player's id could not be read; give the holder's id as to"),
            _ => new Translation.Answered(VerbPipeline.Failure(reply, read, what))
        });
    }

    private static bool NamesPlayer(JsonObject options)
    {
        if (IsPlayer(JsonNodes.Text(options["to"])))
        {
            return true;
        }

        foreach (JsonNode? move in options["moves"] as JsonArray ?? [])
        {
            if (IsPlayer(JsonNodes.Text(move?["to"])))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject WithPlayer(JsonObject options, string player)
    {
        JsonObject resolved = Copy(options);
        if (IsPlayer(JsonNodes.Text(resolved["to"])))
        {
            resolved["to"] = player;
        }

        foreach (JsonNode? move in resolved["moves"] as JsonArray ?? [])
        {
            if (move is JsonObject entry && IsPlayer(JsonNodes.Text(entry["to"])))
            {
                entry["to"] = player;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The what's x-list-shape: a list of ids becomes the handler's list of entries (move item: moves), each entry
    /// taking the call's options the shape names (in verb names, renamed with the entry like any list entry), and every
    /// entry, given or made, gets the shape's fill where it says nothing (to_slot: auto).
    /// </summary>
    internal static JsonObject ListShaped(WhatEntry what, JsonObject options)
    {
        if (what.Write?.ListShape is not { } shape || shape["each"] is not JsonObject each)
        {
            return options;
        }

        string list = OptionOf(what, (string)shape["arg"]!);
        JsonArray entries;
        JsonObject built;
        if (options["id"] is JsonArray ids)
        {
            List<string> taken = [];
            foreach ((string _, JsonNode? option) in each)
            {
                taken.Add((string)option!);
            }

            built = Without(options, taken.ToArray());
            entries = options[list] is JsonArray earlier ? (JsonArray)earlier.DeepClone() : [];
            foreach (JsonNode? id in ids)
            {
                JsonObject entry = new();
                foreach (string option in taken)
                {
                    JsonNode? value = option == "id" ? id : options[option];
                    if (value != null)
                    {
                        entry[option] = value.DeepClone();
                    }
                }

                entries.Add(entry);
            }
        }
        else if (options[list] is JsonArray given)
        {
            built = Copy(options);
            entries = (JsonArray)given.DeepClone();
        }
        else
        {
            return options;
        }

        foreach ((string key, JsonNode? value) in shape["fill"] as JsonObject ?? [])
        {
            string? option = (string?)each[key];
            foreach (JsonNode? entry in entries)
            {
                if (option != null && entry is JsonObject named && named[option] == null)
                {
                    named[option] = value?.DeepClone();
                }
            }
        }

        built[list] = entries;
        return built;
    }

    // The option a handler argument comes from (its own name when the tables say nothing else).
    private static string OptionOf(WhatEntry what, string arg)
    {
        foreach (HandlerEntry handler in what.Handlers)
        {
            foreach (ArgEntry entry in handler.Args)
            {
                if (entry.Arg == arg && entry.Option.Length > 0)
                {
                    return entry.Option;
                }
            }
        }

        return arg;
    }

    private Translation Deposit(WhatEntry what, JsonObject options, StoreKind to) => to switch
    {
        StoreKind.Vault => Call(what, "vault_deposit", options),
        StoreKind.Silo => Call(what, "silo_deposit", options),
        _ => SlotMove(what, options)
    };

    // Out of a store: to "player" is the withdraw handlers' own default, so it is left out (their to_id wants an id).
    private Translation Withdraw(WhatEntry what, JsonObject options, StoreKind source, StoreKind? to)
    {
        JsonObject sent = IsPlayer(JsonNodes.Text(options["to"])) ? Without(options, "to") : options;
        return source switch
        {
            StoreKind.Vault when to == StoreKind.Vault => Call(what, "vault_transfer", sent),
            StoreKind.Vault => Call(what, "vault_withdraw", sent),
            StoreKind.Silo => Call(what, "silo_withdraw", sent),
            _ => Invalid(what, "source", "must be a vault or a silo")
        };
    }

    /// <summary>What kind of store an id is, read from its prefab name (Ingot or Remote Vault, SDB Silo).</summary>
    internal enum StoreKind
    {
        Other,
        Vault,
        Silo
    }

    private Translation KindOf(WhatEntry what, string id, Func<StoreKind, Translation> then)
    {
        HandlerCall read = new("thing_health", new JsonObject { ["reference_id"] = id.Trim() }, _rename.For(what, "thing_health"));
        return new Translation.Probe(read, reply => reply switch
        {
            GameReply.Ok ok => then(StoreOf(JsonNodes.Text(JsonNodes.FromElement(ok.Result)?["prefab_name"]))),
            GameReply.Refused refused => new Translation.Answered(new VerbReply.Failed(refused.Code, refused.Message,
                data: refused.Data is { } data ? JsonNodes.FromElement(data) : null)),
            GameReply.Unreachable unreachable => new Translation.Answered(
                new VerbReply.Failed(VerbFailure.GameUnavailable, unreachable.Message)),
            _ => throw new InvalidOperationException("A game reply is ok, refused or unreachable.")
        });
    }

    internal static StoreKind StoreOf(string? prefab) =>
        prefab == null ? StoreKind.Other
        : prefab.Contains("Silo", StringComparison.OrdinalIgnoreCase) ? StoreKind.Silo
        : prefab.Contains("Vault", StringComparison.OrdinalIgnoreCase) ? StoreKind.Vault
        : StoreKind.Other;

    /// <summary>A job id: undo_job. "paste": the newest blueprint paste. script-N and write-N are not available.</summary>
    internal Translation Undo(WhatEntry what, JsonObject options)
    {
        string id = JsonNodes.Text(options["id"])?.Trim() ?? string.Empty;
        if (id.StartsWith("script-", StringComparison.OrdinalIgnoreCase))
        {
            return NotAvailable($"undo of a script run {RunsPiece}");
        }

        if (id.StartsWith("write-", StringComparison.OrdinalIgnoreCase))
        {
            return NotAvailable("undo of a bulk write lands with the refs piece");
        }

        if (string.Equals(id, "paste", StringComparison.OrdinalIgnoreCase))
        {
            return new Translation.Calls(new HandlerCall("paste_blueprint", new JsonObject { ["undo"] = true },
                _rename.For(what, "paste_blueprint")));
        }

        return Call(what, "undo_job", options);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The options without the ones only another handler of the what takes, for a what that sends one call per part
    /// (write chip, inspect chip): each part takes its own options.
    /// </summary>
    internal static JsonObject OnlyFor(WhatEntry what, string handlerName, JsonObject options)
    {
        HandlerEntry handler = what.Handler(handlerName)!;
        JsonObject kept = new();
        foreach ((string name, JsonNode? value) in options)
        {
            if (Takes(handler, name) || !TakenByAnother(what, handler, name))
            {
                kept[name] = value?.DeepClone();
            }
        }

        return kept;
    }

    private static bool Takes(HandlerEntry handler, string option)
    {
        foreach (ArgEntry entry in handler.Args)
        {
            if (entry.Option == option || entry.Option.StartsWith(option + "+", StringComparison.Ordinal) ||
                entry.Option.EndsWith("+" + option, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TakenByAnother(WhatEntry what, HandlerEntry handler, string option)
    {
        foreach (HandlerEntry other in what.Handlers)
        {
            if (other != handler && Takes(other, option))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject Copy(JsonObject options) => (JsonObject)options.DeepClone();

    private static JsonObject Without(JsonObject options, params string[] names)
    {
        JsonObject kept = new();
        foreach ((string name, JsonNode? value) in options)
        {
            if (Array.IndexOf(names, name) < 0)
            {
                kept[name] = value?.DeepClone();
            }
        }

        return kept;
    }

    private static JsonArray AsList(JsonNode value)
    {
        if (value is JsonArray list)
        {
            return (JsonArray)list.DeepClone();
        }

        return new JsonArray(value.DeepClone());
    }
}
