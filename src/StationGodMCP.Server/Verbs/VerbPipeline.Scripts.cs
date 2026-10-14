using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The four script whats (SPEC-NOTES 5b, the store's part): write script saves the next version, find script lists what
/// a run in this world can see, inspect script gives one in full, remove script deletes one with its history. They are
/// served here, wherever help is answered (the MCP face, the --hub face and a run's own session) and never sent to the
/// mod: the store is under --scripts-dir.
/// </summary>
public sealed partial class VerbPipeline
{
    /// <summary>The store, when this hub has one (--scripts-dir); null in a hub built without one.</summary>
    internal ScriptStore? Scripts => _scripts;

    /// <summary>True for the whats the store answers itself ("hub": true).</summary>
    internal static bool IsScriptWhat(WhatEntry what) => what.Hub;

    private Task<VerbReply> ScriptAsync(VerbCall call, WhatEntry what, JsonObject options)
    {
        if (_scripts == null)
        {
            return Task.FromResult<VerbReply>(new VerbReply.Failed(VerbFailure.NotAvailable,
                $"{what.Label} needs the script store; this hub was started without --scripts-dir"));
        }

        ScriptStore store = _scripts;
        string? world = WorldOf(call.Stream) ?? _link.WorldId;
        switch (what.Key)
        {
            case "write.script":
                return Task.FromResult(WriteScript(store, options, world));
            case "find.script":
                return Task.FromResult(FindScript(store, options, world));
            case "inspect.script":
                return Task.FromResult(InspectScript(store, options, world));
            case "remove.script":
                return Task.FromResult(RemoveScript(store, options, world));
            default:
                return Task.FromResult<VerbReply>(new VerbReply.Failed(VerbFailure.NotAvailable,
                    $"{what.Label} is hub-marked but this hub has no handler for it"));
        }
    }

    // write script {name, code, description, scope}: the next version, kept beside every older one.
    private static VerbReply WriteScript(ScriptStore store, JsonObject options, string? world)
    {
        string name = (JsonNodes.Text(options["name"]) ?? string.Empty).Trim();
        string code = JsonNodes.Text(options["code"]) ?? string.Empty;
        string description = (JsonNodes.Text(options["description"]) ?? string.Empty).Trim();
        if (description.Length == 0 || description.Contains('\n') || description.Contains('\r'))
        {
            return OptionProblem.Refusal("write script",
                [new OptionProblem("description", "one line saying what the script does: it is what find script shows")]);
        }

        ScriptScope scope = string.Equals((JsonNodes.Text(options["scope"]) ?? string.Empty).Trim(), "world",
            StringComparison.OrdinalIgnoreCase)
            ? ScriptScope.World
            : ScriptScope.Shared;
        switch (store.Save(name, code, description, scope, world))
        {
            case ScriptOutcome<SavedScript>.Refused refused:
                return Failed(refused);
            case ScriptOutcome<SavedScript>.Done saved:
                JsonObject reply = new()
                {
                    ["name"] = saved.Value.Name,
                    ["scope"] = saved.Value.ScopeWord,
                    ["version"] = saved.Value.Version,
                    ["parameters"] = ParameterLines(saved.Value.Parameters),
                    ["file"] = saved.Value.Path
                };
                if (saved.Value.World != null)
                {
                    reply["world"] = saved.Value.World;
                }

                return new VerbReply.Done(reply);
            default:
                throw new InvalidOperationException("A save is done or refused.");
        }
    }

    // find script {text?}: what a run in this world can see, by name.
    private static VerbReply FindScript(ScriptStore store, JsonObject options, string? world)
    {
        JsonArray scripts = [];
        List<SavedScript> found = store.Find(JsonNodes.Text(options["text"]), world);
        foreach (SavedScript script in found)
        {
            JsonObject entry = new()
            {
                ["name"] = script.Name,
                ["scope"] = script.ScopeWord,
                ["version"] = script.Version,
                ["description"] = script.Description,
                ["parameters"] = ParameterLines(script.Parameters)
            };
            if (script.World != null)
            {
                entry["world"] = script.World;
            }

            scripts.Add(entry);
        }

        return new VerbReply.Done(new JsonObject { ["scripts"] = scripts, ["count"] = scripts.Count });
    }

    // inspect script {name}: the source, the parameters, the version, the last run that used it.
    private static VerbReply InspectScript(ScriptStore store, JsonObject options, string? world)
    {
        string name = (JsonNodes.Text(options["name"]) ?? string.Empty).Trim();
        switch (store.Open(name, world))
        {
            case ScriptOutcome<SavedScript>.Refused refused:
                return Failed(refused);
            case ScriptOutcome<SavedScript>.Done opened:
                SavedScript script = opened.Value;
                JsonObject reply = new()
                {
                    ["name"] = script.Name,
                    ["scope"] = script.ScopeWord,
                    ["version"] = script.Version,
                    ["description"] = script.Description,
                    ["parameters"] = ParameterLines(script.Parameters),
                    ["file"] = script.Path,
                    ["source"] = script.Source,
                    ["last_used"] = LastUsed(store.LastUsed(script.Name))
                };
                if (script.World != null)
                {
                    reply["world"] = script.World;
                }

                return new VerbReply.Done(reply);
            default:
                throw new InvalidOperationException("An open is done or refused.");
        }
    }

    // remove script {name}: the current file and every version kept beside it.
    private static VerbReply RemoveScript(ScriptStore store, JsonObject options, string? world)
    {
        string name = (JsonNodes.Text(options["name"]) ?? string.Empty).Trim();
        switch (store.Remove(name, world))
        {
            case ScriptOutcome<SavedScript>.Refused refused:
                return Failed(refused);
            case ScriptOutcome<SavedScript>.Done removed:
                JsonObject reply = new()
                {
                    ["name"] = removed.Value.Name,
                    ["scope"] = removed.Value.ScopeWord,
                    ["version"] = removed.Value.Version,
                    ["removed"] = true
                };
                if (removed.Value.World != null)
                {
                    reply["world"] = removed.Value.World;
                }

                return new VerbReply.Done(reply);
            default:
                throw new InvalidOperationException("A removal is done or refused.");
        }
    }

    private static JsonNode? LastUsed(ScriptUse? used) => used == null
        ? null
        : new JsonObject
        {
            ["run"] = used.RunId,
            ["at"] = used.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
        };

    private static VerbReply Failed(ScriptOutcome<SavedScript>.Refused refused) =>
        new VerbReply.Failed(refused.Code, refused.Message, data: refused.Data);

    /// <summary>main(...)'s parameters as the signature reads them: "who: str", "times: int = 1", "*extra", "**kw".</summary>
    internal static JsonArray ParameterLines(IReadOnlyList<ScriptParameter> parameters)
    {
        JsonArray lines = [];
        foreach (ScriptParameter parameter in parameters)
        {
            string prefix = parameter.Kind switch
            {
                ScriptParameter.VarPositional => "*",
                ScriptParameter.VarKeyword => "**",
                _ => string.Empty
            };
            string annotation = parameter.Type is { } type ? ": " + type : string.Empty;
            string fallback = parameter.HasDefault ? " = ..." : string.Empty;
            lines.Add(prefix + parameter.Name + annotation + fallback);
        }

        return lines;
    }
}
