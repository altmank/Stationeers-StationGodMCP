#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Tests.CatalogueChecks;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The verb catalogue's source, catalogue/verbs: one file per verb (its tool description, the tool list's short option
/// texts, and per what its summary, options, help text, handlers, reply shape and, for a write, the write rules) and
/// _shared.json (the verb order, shared options, instructions, small types and help topics). An option that names a
/// shared definition with "use" takes that definition, with its own keys replacing the shared ones. Everything the
/// verbs publish (the MCP tools array, help pages, Python stubs) is generated from these files.
/// </summary>
internal static class VerbCatalogue
{
    internal const string WhatlessKey = "-";

    internal static readonly string[] Shaping = { "fields", "omit", "list_limits", "output_file", "whole", "repeat", "as_player" };

    internal static readonly string[] Routes = { "acts", "confirm" };

    internal static readonly string[] UndoClasses = { "undo", "cannot", "mixed" };

    internal static readonly string[] TargetRoles = { "acts-on", "pays-from", "spread" };

    private static readonly Lazy<JsonObject> SharedFile = new(() => Read("_shared"));

    private static readonly Lazy<List<JsonObject>> VerbFiles = new(() =>
        SharedFile.Value["verbs"]!.AsArray().Select(name => Read((string)name!)).ToList());

    internal static string SourceRoot => Path.Combine(CatalogueFiles.SourceRoot, "verbs");

    internal static JsonObject Shared => SharedFile.Value;

    internal static IReadOnlyList<JsonObject> Verbs => VerbFiles.Value;

    internal static JsonObject Verb(string name) => Verbs.Single(verb => (string)verb["verb"]! == name);

    internal static string NameOf(JsonObject verb) => (string)verb["verb"]!;

    internal static IEnumerable<(string Name, JsonObject What)> WhatsOf(JsonObject verb)
    {
        foreach ((string name, JsonNode? what) in verb["whats"]!.AsObject())
        {
            yield return (name, what!.AsObject());
        }
    }

    /// <summary>Every what of every verb, in catalogue order.</summary>
    internal static IEnumerable<(string Verb, string What, JsonObject Source)> Whats()
    {
        foreach (JsonObject verb in Verbs)
        {
            foreach ((string name, JsonObject what) in WhatsOf(verb))
            {
                yield return (NameOf(verb), name, what);
            }
        }
    }

    internal static bool HasWhats(JsonObject verb) =>
        !(verb["whats"]!.AsObject().Count == 1 && verb["whats"]!.AsObject().ContainsKey(WhatlessKey));

    /// <summary>
    /// A what the hub answers itself ("hub": true): its reply comes from the store or the runs, help pages and stubs
    /// are generated for it like any other, and the mod never sees it - VerbSurface and the generated mod options
    /// leave it out (tool list, help and stubs move together; the mod's surface does not).
    /// </summary>
    internal static bool IsHub(JsonObject what) => (bool?)what["hub"] ?? false;

    /// <summary>A verb every what of which is hub-answered (run_script): the mod dispatches none of it, and sg gets no
    /// generated function for it (help is the hub's own page server too).</summary>
    internal static bool IsHubOnly(JsonObject verb) => WhatsOf(verb).All(pair => IsHub(pair.What));

    /// <summary>
    /// A what's agent options, each resolved against its shared definition: everything the tool list, help and stubs
    /// show. A wire option (x-wire: the hub and sg send it, an agent does not) is left out.
    /// </summary>
    internal static IEnumerable<(string Name, JsonObject Option)> OptionsOf(JsonObject what) =>
        WireOptionsOf(what).Where(option => !IsWire(option.Option));

    /// <summary>A what's options as the game takes them: the agent options and the wire options, in catalogue order.</summary>
    internal static IEnumerable<(string Name, JsonObject Option)> WireOptionsOf(JsonObject what)
    {
        foreach ((string name, JsonNode? option) in what["options"]?.AsObject() ?? new JsonObject())
        {
            yield return (name, Resolve(option!.AsObject()));
        }
    }

    /// <summary>An option only the hub and sg send (x-wire: true), never shown to an agent.</summary>
    internal static bool IsWire(JsonObject resolved) => (bool?)resolved["x-wire"] ?? false;

    /// <summary>A shared option's definition, by name.</summary>
    internal static JsonObject SharedOption(string name) => Shared["options"]![name]!.AsObject();

    internal static JsonObject Resolve(JsonObject option)
    {
        if (option["use"] is not JsonValue use)
        {
            return option;
        }

        JsonObject resolved = SharedOption((string)use!).DeepClone().AsObject();
        foreach ((string key, JsonNode? value) in option)
        {
            if (key != "use")
            {
                resolved[key] = value?.DeepClone();
            }
        }

        return resolved;
    }

    /// <summary>The what's write rules, or null for a what that writes nothing.</summary>
    internal static JsonObject? WriteOf(JsonObject what) => what["write"] as JsonObject;

    /// <summary>The what's id option takes a list of ids.</summary>
    internal static bool IdTakesList(JsonObject what) =>
        what["options"]?["id"] is JsonObject id && VerbText.TypesOf(Resolve(id)["schema"]!.AsObject()).Contains("array");

    /// <summary>
    /// An id list answers one row per id; false where the what takes no id list, or takes it as the entries of another
    /// list option (id_list: move item's moves, read sample's reads).
    /// </summary>
    internal static bool TakesIdList(JsonObject what) => IdTakesList(what) && what["id_list"] == null;

    private static JsonObject Read(string name) => CatalogueFiles.ReadJson(Path.Combine(SourceRoot, name + ".json")).AsObject();
}
