#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The verb catalogue (catalogue/verbs) holds what PLAN 2.0 asks of it: every write what declares its route, x-undo
/// and x-target; every option has x-script; no old tool or option name reaches verb help or errors;
/// script-run ids and mod job ids cannot be confused; generated reply lines match the declared shapes; the generated
/// files are current; the size targets hold.
/// </summary>
public sealed class VerbCatalogueTests
{
    private static readonly Lazy<SortedDictionary<string, string>> Generated = new(VerbOutputs.All);

    private static readonly Lazy<SortedDictionary<string, string>> ModOptions = new(VerbOptionsCode.Files);

    private static readonly Lazy<Dictionary<string, JsonObject>> Methods = new(() =>
        CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath)["methods"]!.AsArray()
            .ToDictionary(method => (string)method!["name"]!, method => method!.AsObject()));

    private readonly ITestOutputHelper _output;

    public VerbCatalogueTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- the generated files ----

    [Fact]
    public void GeneratedFilesAreCurrent()
    {
        if (Environment.GetEnvironmentVariable("STATIONGOD_WRITE_CATALOGUE") == "1")
        {
            VerbOutputs.Write(Generated.Value);
            VerbOutputs.Write(ModOptions.Value, VerbOutputs.ModRoot);
        }

        List<string> stale = VerbOutputs.Stale(Generated.Value);
        stale.AddRange(VerbOutputs.Stale(ModOptions.Value, VerbOutputs.ModRoot).Select(path => "mod options " + path));
        Assert.True(stale.Count == 0,
            "generated/verbs or the mod's options differ from what catalogue/verbs generates " +
            "(STATIONGOD_WRITE_CATALOGUE=1 rewrites them): " + string.Join(", ", stale.Take(20)));
    }

    [Fact]
    public void TheVerbsAreTheFifteenPlusHelpAndRunScript()
    {
        string[] expected =
        {
            "find", "inspect", "survey", "read", "write", "plan", "check", "build", "swap", "remove", "move", "trade",
            "show", "undo", "server", "help", "run_script"
        };
        Assert.Equal(expected, VerbCatalogue.Verbs.Select(VerbCatalogue.NameOf).ToArray());
        JsonArray tools = VerbToolList.Build()["tools"]!.AsArray();
        Assert.Equal(expected, tools.Select(tool => (string)tool!["name"]!).ToArray());
    }

    // ---- the write rules ----

    [Fact]
    public void EveryWriteWhatDeclaresItsWriteRules()
    {
        string[] writeVerbs = { "write", "build", "swap", "remove", "undo" };
        List<string> problems = new();
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            JsonObject? write = VerbCatalogue.WriteOf(source);
            string at = verb + " " + what;
            if (write == null)
            {
                if (writeVerbs.Contains(verb))
                {
                    problems.Add(at + ": a write verb's what without write rules");
                }

                continue;
            }

            Check(problems, at, "route", write, VerbCatalogue.Routes);
            Check(problems, at, "x-undo", write, VerbCatalogue.UndoClasses);
            if (string.IsNullOrWhiteSpace((string?)write["x-undo-mark"]))
            {
                problems.Add(at + ": no x-undo-mark (the footer's text)");
            }

            if (write["x-target"] is not JsonArray targets)
            {
                problems.Add(at + ": no x-target");
                continue;
            }

            HashSet<string> options = new(VerbCatalogue.OptionsOf(source).Select(option => option.Name));
            foreach (JsonNode? target in targets)
            {
                string path = (string)target!["path"]!;
                string top = path.Split('[', '.')[0];
                if (!options.Contains(top))
                {
                    problems.Add(at + ": x-target " + path + " names no option");
                }

                if (!VerbCatalogue.TargetRoles.Contains((string?)target["role"]))
                {
                    problems.Add(at + ": x-target " + path + " role " + target["role"]);
                }
            }

            if (VerbCatalogue.IdTakesList(source) && !targets.Any(target => (string)target!["path"]! == "id"))
            {
                problems.Add(at + ": takes an id list but id is not an x-target path");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheOneIdWritesRefuseLists()
    {
        // PLAN 2.0 and SPEC-NOTES note 21: chip, screen, uplink, flight_log, memory, genes.
        foreach (string what in new[] { "chip", "screen", "uplink", "flight_log", "memory", "genes" })
        {
            JsonObject source = VerbCatalogue.Verb("write")["whats"]![what]!.AsObject();
            Assert.Equal("string", (string?)VerbCatalogue.Resolve(source["options"]!["id"]!.AsObject())["schema"]!["type"]);
        }
    }

    [Fact]
    public void EveryOtherIdTakesAList()
    {
        string[] oneId = { "write chip", "write screen", "write uplink", "write flight_log", "write memory", "write genes", "server stop" };
        List<string> single = new();
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if (source["options"]?["id"] is JsonObject && !VerbCatalogue.IdTakesList(source) && !oneId.Contains(verb + " " + what))
            {
                single.Add(verb + " " + what);
            }
        }

        Assert.True(single.Count == 0, "an id that takes no list outside the one-id whats: " + string.Join(", ", single));
    }

    [Fact]
    public void AnIdListNamesAListOptionOfItsWhat()
    {
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if ((string?)source["id_list"] is { } list)
            {
                Assert.True(VerbCatalogue.IdTakesList(source), $"{verb} {what}: id_list on an id that takes no list");
                Assert.Contains("array", VerbText.TypesOf(VerbCatalogue.Resolve(source["options"]![list]!.AsObject())["schema"]!.AsObject()));
            }
        }
    }

    [Fact]
    public void TheC2aReviewFixesHold()
    {
        // move item (PLAN 2.0): typed ids act at once; an id list is one moves list with a slot per move
        JsonObject item = VerbCatalogue.Verb("move")["whats"]!["item"]!.AsObject();
        JsonObject write = VerbCatalogue.WriteOf(item)!;
        Assert.Equal("acts", (string?)write["route"]);
        Assert.Equal("moves", (string?)item["id_list"]);
        Assert.Null(item["example"]!["confirm"]);

        // decision M6: a write what's filter options are spreads the run gate sees
        string[] spreads = write["x-target"]!.AsArray().Where(target => (string)target!["role"]! == "spread")
            .Select(target => (string)target!["path"]!).ToArray();
        Assert.Equal(new[] { "prefab", "name", "location", "within", "near", "radius", "category" }, spreads);
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if (VerbCatalogue.WriteOf(source) is not { } rules)
            {
                continue;
            }

            foreach (string selector in new[] { "network", "room", "near", "box", "rocket" })
            {
                if (source["options"]?[selector] != null)
                {
                    Assert.True(rules["x-target"]!.AsArray().Any(target =>
                        (string)target!["path"]! == selector && (string)target["role"]! == "spread"), $"{verb} {what}.{selector}");
                }
            }
        }

        // undo: a script gives the id (sg.undo(id=...))
        JsonObject undo = VerbCatalogue.Verb("undo")["whats"]![VerbCatalogue.WhatlessKey]!.AsObject();
        Assert.NotEqual("drop", (string?)VerbCatalogue.Resolve(undo["options"]!["id"]!.AsObject())["x-script"]);
    }

    [Fact]
    public void PerIdRowsArePlansShape()
    {
        // PLAN 2.0 and SPEC-NOTES 14: per-id list rows are {index, ok, reference_id, payload | error}
        Regex old = new(@"reference_id, \.\.\.|\{reference_id, error\}");
        List<string> found = Generated.Value.Where(file => old.IsMatch(file.Value)).Select(file => file.Key).ToList();
        Assert.True(found.Count == 0, "old row shape in: " + string.Join(", ", found));
        Assert.Contains("{index, ok, reference_id, payload | error}", VerbHelp.IdListLine);
        Assert.Contains("{index, ok, reference_id, payload | error}", VerbStubs.IdListLine);
    }

    /// <summary>
    /// Write options reviewed as no target (C2b review, 08:45): they set values, place new things at positions, shape
    /// the reply or run, lift guards, or name consent and routing (gateway, allow_bridge, assume_removed), never a thing
    /// the write acts on, pays from or picks. Any other write option is an x-target path.
    /// </summary>
    private static readonly HashSet<string> NotTargets = new(StringComparer.Ordinal)
    {
        // run and reply shaping
        "confirm", "queue", "max_wait", "include", "verbose", "rows", "action", "csv", "slow_frame_ms", "lines",
        "interval_s", "capacity",
        // guards, consent and routing
        "allow", "acknowledge_gas_lost", "allow_bridge", "assume_removed", "gateway", "skip_unmatched", "free", "force",
        "split_long", "join", "join_trunk", "as_player",
        // values written
        "logic", "value", "start", "values", "code", "code_file", "run", "genes", "unit", "color", "label", "ratio",
        "joules", "quantity", "merge", "slot", "reagent", "at_most", "gases", "amount_mol", "delete", "joined", "command",
        "items", "state", "surface", "element", "pixel", "event", "text", "safe_ground", "from_step",
        // new things at positions (placement cells are not seen-checked)
        "placements", "at", "rotation", "spread", "facing", "up", "face", "orient", "above_floor", "build_state", "grade",
        "piece", "pieces", "branches", "extra_ends", "blueprint", "turn", "from_plan",
        // per what: build's prefab is what it places; build runs' cells and waypoints are new cells; server stop's id is
        // the hub's own run or job
        "build structure.prefab", "build cable.cells", "build cable.waypoints", "build pipe.cells", "build pipe.waypoints",
        "build chute.cells", "build chute.waypoints", "server stop.id"
    };

    [Fact]
    public void EveryWriteOptionThatPicksATargetIsAnXTarget()
    {
        List<string> unreviewed = new();
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if (VerbCatalogue.WriteOf(source) is not { } write)
            {
                continue;
            }

            HashSet<string> paths = new(write["x-target"]!.AsArray().Select(target => ((string)target!["path"]!).Split('[', '.')[0]));
            foreach ((string name, JsonObject _) in VerbCatalogue.OptionsOf(source))
            {
                bool perWhat = NotTargets.Contains(verb + " " + what + "." + name);
                bool general = NotTargets.Contains(name) && !paths.Contains(name);
                if (!paths.Contains(name) && !perWhat && !general)
                {
                    unreviewed.Add($"{verb} {what}.{name}");
                }
            }
        }

        Assert.True(unreviewed.Count == 0, "write options neither x-target nor reviewed as no target: " + string.Join(", ", unreviewed));
        JsonObject chip = VerbCatalogue.WriteOf(VerbCatalogue.Verb("write")["whats"]!["chip"]!.AsObject())!;
        Assert.Equal(6, chip["x-target"]!.AsArray().Count(target => ((string)target!["path"]!).StartsWith("pins.", StringComparison.Ordinal)));
    }

    private static void Check(List<string> problems, string at, string key, JsonObject write, string[] allowed)
    {
        string? value = (string?)write[key];
        if (value == null || !allowed.Contains(value))
        {
            problems.Add($"{at}: {key} is '{value}', not one of {string.Join(", ", allowed)}");
        }
    }

    // ---- scripts (PLAN v9 2.2 Discovery) ----

    [Fact]
    public void RunScriptAndTheInstructionsAreTheV9Discovery()
    {
        JsonObject runScript = VerbCatalogue.Verb("run_script");
        string description = (string)runScript["description"]!;
        foreach (string needed in new[] { "confirm: true", "sg.Stopped", "finally", "sg.hold", "30 s", "Footer:" })
        {
            Assert.Contains(needed, description);
        }

        // two routes: no repeat route, and sg.watch stays in sg.help('scripts')
        Assert.DoesNotContain("repeat", description);
        Assert.DoesNotContain("sg.watch", description);
        Assert.False(runScript["whats"]![VerbCatalogue.WhatlessKey]!["options"]!.AsObject().ContainsKey("repeat"));
        string instructions = (string)VerbCatalogue.Shared["instructions"]!;
        Assert.Contains("for waits, polls and timed or repeated work, run_script; for many targets, give id a list", instructions,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A script changes nothing unless the run has confirm: true", instructions);
        Assert.Contains("server {what: 'stop'} (load the server tool if it isn't)", instructions);
    }

    [Fact]
    public void NoScriptTextUsesTheV8Routes()
    {
        // v9: a report run is the preview; to act, send the code again with confirm: true. No run repeat, no
        // DIFFERS FROM REPORT, no continue-as-report. (The direct face's repeat: "r7" stays: face register D2.)
        Regex v8 = new(@"(?i)\brepeat\b|DIFFERS|report, then|unreported_target|value_changed|goes on as a report|continues as a report");
        List<string> found = new();
        foreach ((string path, string text) in Generated.Value)
        {
            if (path.StartsWith("stubs/", StringComparison.Ordinal) || path == "help/topic.scripts.txt" || path == "help/run_script.txt")
            {
                foreach (Match match in v8.Matches(text))
                {
                    found.Add(path + ": " + match.Value);
                }
            }
        }

        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            foreach (string line in VerbText.Lines(source["x-script-text"]))
            {
                if (v8.IsMatch(line))
                {
                    found.Add($"{verb} {what} x-script-text: {line}");
                }
            }

            foreach ((string name, JsonObject option) in VerbCatalogue.OptionsOf(source))
            {
                string script = (string)option["x-script"]!;
                string text = script == "same" ? (string?)option["description"] ?? "" : script == "drop" ? "" : script;
                if (v8.IsMatch(text))
                {
                    found.Add($"{verb} {what}.{name} x-script: {text}");
                }
            }
        }

        foreach (string tool in new[] { "run_script" })
        {
            string description = (string)VerbCatalogue.Verb(tool)["description"]!;
            if (v8.IsMatch(description))
            {
                found.Add(tool + " description");
            }
        }

        Assert.True(found.Count == 0, "v8 script wording:\n" + string.Join("\n", found));
    }

    // ---- options ----

    [Fact]
    public void EveryOptionHasItsSchemaTextAndXScript()
    {
        List<string> problems = new();
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            foreach ((string name, JsonObject option) in VerbCatalogue.OptionsOf(source))
            {
                string at = $"{verb} {what}.{name}";
                if (option["schema"] is not JsonObject)
                {
                    problems.Add(at + ": no schema");
                }

                if (string.IsNullOrWhiteSpace((string?)option["description"]))
                {
                    problems.Add(at + ": no description");
                }

                if (string.IsNullOrWhiteSpace((string?)option["x-script"]))
                {
                    problems.Add(at + ": no x-script (same, drop or a Python text)");
                }
            }
        }

        foreach ((string name, JsonNode? option) in VerbCatalogue.Shared["options"]!.AsObject())
        {
            if (string.IsNullOrWhiteSpace((string?)option!["x-script"]))
            {
                problems.Add("shared " + name + ": no x-script");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheRunLevelOptionsAreNotInScripts()
    {
        // D7: confirm, max_wait, queue and offset are run-level or walk-level in a script.
        foreach (string name in new[] { "confirm", "max_wait", "queue", "offset" })
        {
            Assert.Equal("drop", (string?)VerbCatalogue.SharedOption(name)["x-script"]);
        }

        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if (verb == "run_script")
            {
                continue;
            }

            foreach ((string name, JsonObject option) in VerbCatalogue.OptionsOf(source))
            {
                if (name is "confirm" or "max_wait" or "queue" or "offset")
                {
                    Assert.True((string?)option["x-script"] == "drop", $"{verb} {what}.{name} is in the stub");
                }
            }
        }
    }

    // ---- names ----

    /// <summary>
    /// Old names: today's tool names, and handler arguments the verbs renamed, merged or moved that are no option
    /// name now. Only names with an underscore count (a one-word old name such as label or paint is plain English and
    /// often a what), and a name that is a reply key somewhere stays allowed (reply keys keep today's names).
    /// </summary>
    internal static SortedSet<string> OldNames()
    {
        HashSet<string> current = new(StringComparer.Ordinal);
        HashSet<string> replyKeys = new(StringComparer.Ordinal);
        Regex key = new(@"\b(\w+):");
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            current.Add(verb);
            current.Add(what);
            foreach ((string name, JsonObject option) in VerbCatalogue.OptionsOf(source))
            {
                current.Add(name);
                CatalogueFiles.CollectPropertyNames(option["schema"]!, current);
            }

            foreach (JsonNode? variant in source["reply"]?["variants"]?.AsArray() ?? new JsonArray())
            {
                foreach ((string name, JsonNode? type) in variant!["keys"]?.AsObject() ?? new JsonObject())
                {
                    replyKeys.Add(name);
                    foreach (Match match in key.Matches((string?)type ?? ""))
                    {
                        replyKeys.Add(match.Groups[1].Value);
                    }
                }
            }
        }

        foreach (string small in VerbCatalogue.Shared["types"]!.AsObject().Select(pair => (string)pair.Value!))
        {
            foreach (Match match in key.Matches(small))
            {
                replyKeys.Add(match.Groups[1].Value);
            }
        }

        SortedSet<string> old = new(StringComparer.Ordinal);
        foreach (string tool in Methods.Value.Keys)
        {
            if (tool.Contains('_') && !current.Contains(tool) && !replyKeys.Contains(tool))
            {
                old.Add(tool);
            }
        }

        return old;
    }

    [Fact]
    public void NoOldToolOrOptionNameIsInVerbHelpOrErrors()
    {
        SortedSet<string> old = OldNames();
        Assert.Contains("find_things", old);
        Assert.Contains("advance_build_state", old);
        Regex word = new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b");
        List<string> found = new();
        void Scan(string where, string text)
        {
            foreach (Match match in word.Matches(text))
            {
                if (old.Contains(match.Value))
                {
                    found.Add($"{where}: {match.Value}");
                }
            }
        }

        foreach ((string path, string text) in Generated.Value)
        {
            if (path.StartsWith("help/", StringComparison.Ordinal) || path.StartsWith("stubs/", StringComparison.Ordinal))
            {
                Scan(path, text);
            }
        }

        foreach (JsonNode? tool in VerbToolList.Build()["tools"]!.AsArray())
        {
            Scan("tools.json " + tool!["name"], tool.ToJsonString(VerbText.Compact));
        }

        Scan("instructions", (string)VerbCatalogue.Shared["instructions"]!);
        foreach ((string code, JsonNode? error) in CatalogueFiles.ReadJson(Path.Combine(VerbCatalogue.SourceRoot, "_errors.json")).AsObject())
        {
            Scan("_errors.json " + code, error!.ToJsonString(VerbText.Compact));
        }

        Assert.True(found.Count == 0, "old names in verb help or errors:\n" + string.Join("\n", found.Distinct().Take(60)));
    }

    [Fact]
    public void EveryVerbErrorCodeIsTodaysAndPointsAtVerbHelp()
    {
        JsonObject errors = CatalogueFiles.ReadJson(Path.Combine(VerbCatalogue.SourceRoot, "_errors.json")).AsObject();
        JsonObject today = CatalogueFiles.ReadJson(Path.Combine(CatalogueFiles.SourceRoot, "errors.json")).AsObject();
        Assert.Equal(today.Select(pair => pair.Key).OrderBy(code => code, StringComparer.Ordinal),
            errors.Select(pair => pair.Key).OrderBy(code => code, StringComparer.Ordinal));
        HashSet<string> topics = new(VerbHelp.TopicKeys());
        foreach ((string code, JsonNode? error) in errors)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)error!["description"]), code);
            if (error["see"] is not JsonObject see)
            {
                continue;
            }

            if ((string?)see["verb"] is { } verb)
            {
                JsonObject source = VerbCatalogue.Verb(verb);
                Assert.True(see["what"] == null || source["whats"]![(string)see["what"]!] != null, code);
            }

            Assert.True(see["topic"] == null || topics.Contains((string)see["topic"]!), code);
        }
    }

    // ---- ids ----

    [Fact]
    public void ScriptRunIdsAndModJobIdsDoNotOverlap()
    {
        // SPEC-NOTES note 3: script runs are script-N, mod jobs keep their own prefixes, and the hub routes by prefix.
        Regex start = new(@"\.Start\(""([a-z_]+)""");
        HashSet<string> prefixes = new(StringComparer.Ordinal);
        string mod = Path.Combine(CatalogueFiles.RepositoryRoot(), "src", "StationGodMCP.Mod");
        foreach (string file in Directory.GetFiles(mod, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in start.Matches(File.ReadAllText(file)))
            {
                prefixes.Add(match.Groups[1].Value);
            }
        }

        Assert.Contains("place", prefixes);
        Assert.Contains("run", prefixes);
        // the hub's own ids: bulk writes (write-N) and script runs (script-N)
        string[] hub = { "script", "write" };
        foreach (string prefix in prefixes)
        {
            foreach (string own in hub)
            {
                Assert.False(prefix.StartsWith(own, StringComparison.Ordinal) || own.StartsWith(prefix, StringComparison.Ordinal),
                    $"mod job prefix '{prefix}-' and the hub's '{own}-' can be confused");
            }
        }
    }

    // ---- reply shapes ----

    [Fact]
    public void GeneratedReplyLinesMatchTheDeclaredShapes()
    {
        List<string> problems = new();
        foreach (JsonObject verb in VerbCatalogue.Verbs)
        {
            string name = VerbCatalogue.NameOf(verb);
            foreach ((string what, JsonObject source) in VerbCatalogue.WhatsOf(verb))
            {
                if (source["reply"] is not JsonObject reply || name == "run_script")
                {
                    continue;
                }

                string at = name + " " + what;
                JsonArray variants = reply["variants"]!.AsArray();
                List<List<string>> declared = variants.Select(variant => variant!["keys"] is JsonObject keys
                    ? keys.Select(pair => pair.Key).ToList()
                    : new List<string> { "job:" + (string)variant["job_view"]! }).ToList();

                // the direct page's reply line
                string page = VerbHelp.WhatPage(verb, what);
                string? line = page.Split('\n').FirstOrDefault(l => l.StartsWith("Reply: ", StringComparison.Ordinal));
                if (variants.Count > 0)
                {
                    List<List<string>> direct = ParseDirectReply(line?.Substring(7) ?? "", variants.Count);
                    if (!Same(declared, direct))
                    {
                        problems.Add(at + ": direct reply line " + line);
                    }

                    // the stub's reply block
                    List<List<string>> stub = ParseStubReply(VerbStubs.Stub(verb, what));
                    if (!Same(declared, stub))
                    {
                        problems.Add(at + ": stub reply block");
                    }
                }

                // the main list is a list in the reply
                if ((string?)reply["list"] is { } list && !variants.Any(variant =>
                        ((string?)variant!["keys"]?[list])?.StartsWith("list[", StringComparison.Ordinal) == true ||
                        variant["job_view"] != null))
                {
                    problems.Add(at + ": main list '" + list + "' is no list key of its reply");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static bool Same(List<List<string>> a, List<List<string>> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second));

    private static List<List<string>> ParseDirectReply(string line, int count)
    {
        List<List<string>> variants = new();
        foreach (string part in count > 1 ? SplitBars(line) : new List<string> { line })
        {
            string body = part;
            if (count > 1 && body.StartsWith('('))
            {
                body = body.Substring(body.IndexOf(") ", StringComparison.Ordinal) + 2);
            }

            Match job = Regex.Match(body, @"^a (\w+) \(help");
            variants.Add(job.Success
                ? new List<string> { "job:" + job.Groups[1].Value }
                : VerbText.SplitTop(body).Select(key => key.Split(' ')[0]).ToList());
        }

        return variants;
    }

    private static List<string> SplitBars(string line)
    {
        List<string> parts = new();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < line.Length; index++)
        {
            char c = line[index];
            depth += c is '{' or '[' or '(' ? 1 : c is '}' or ']' or ')' ? -1 : 0;
            if (depth == 0 && string.CompareOrdinal(line, index, " | ", 0, 3) == 0)
            {
                parts.Add(line.Substring(start, index - start));
                start = index + 3;
            }
        }

        parts.Add(line.Substring(start));
        return parts;
    }

    private static List<List<string>> ParseStubReply(string stub)
    {
        List<List<string>> variants = new();
        bool inReply = false;
        foreach (string line in stub.Split('\n'))
        {
            if (line.StartsWith("    Reply", StringComparison.Ordinal))
            {
                inReply = true;
                continue;
            }

            if (!inReply)
            {
                continue;
            }

            if (line.StartsWith("    (", StringComparison.Ordinal))
            {
                variants.Add(new List<string>());
            }
            else if (line.StartsWith("          ", StringComparison.Ordinal))
            {
                // a wrapped type
            }
            else if (line.StartsWith("      ", StringComparison.Ordinal))
            {
                if (variants.Count == 0)
                {
                    variants.Add(new List<string>());
                }

                string entry = line.Trim();
                variants[^1].Add(entry.Contains(": ") ? entry.Substring(0, entry.IndexOf(": ", StringComparison.Ordinal)) : "job:" + entry);
            }
            else
            {
                break;
            }
        }

        return variants;
    }

    // ---- sizes ----

    [Fact]
    public void TheSizeTargetsHold()
    {
        JsonObject list = VerbToolList.Build();
        JsonObject table = VerbSizes.Table(list);
        _output.WriteLine(CatalogueFiles.Serialize(VerbText.Parsed(table)));
        int full = (int)table["full_list"]!["verbs"]!;
        Assert.True(full <= VerbSizes.FullListShareMax * VerbSizes.TodayFullTokens,
            $"the verbs' full list is {full} tokens, over {VerbSizes.FullListShareMax:P0} of today's {VerbSizes.TodayFullTokens}");
        foreach ((string tool, JsonNode? tokens) in table["per_tool"]!.AsObject())
        {
            Assert.True((int)tokens! <= VerbSizes.PerVerbMax, $"{tool}'s schema is {tokens} tokens, over {VerbSizes.PerVerbMax}");
        }

        int fresh = (int)table["per_fresh_context"]!["verbs"]!;
        Assert.True(fresh <= VerbSizes.TodayPerFreshContext,
            $"listing plus the common load is {fresh} tokens, over today's {VerbSizes.TodayPerFreshContext} per fresh context");
    }

    [Fact]
    public void TheTokenizerStillMatchesTodaysMeasuredList()
    {
        // Calibrated once: today's tools/list plus instructions measured 89,000 tokens (tools/tool_list_cost.py).
        // A change to the tokenizer, or to today's catalogue large enough to matter, shows here.
        JsonArray tools = StationGodMCP.Server.ToolCatalogue.ToolsOf(CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath).AsObject());
        string instructions = (string)CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath)["server"]!["instructions"]!;
        int tokens = VerbSizes.Tokens(tools.ToJsonString(VerbText.Compact) + instructions);
        _output.WriteLine($"today's list: {tokens} tokens by the tokenizer, {VerbSizes.TodayFullTokens} measured");
        Assert.InRange(tokens, VerbSizes.TodayFullTokens * 0.97, VerbSizes.TodayFullTokens * 1.03);
    }
}
