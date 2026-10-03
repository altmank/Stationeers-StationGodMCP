#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Xunit;
using ModCatalogue = StationGodMCP.Pure.Catalogue.Catalogue;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The catalogue cannot drift from the code (catalogue.md, The consistency tests): the assembled file is current and
/// valid; it lists exactly the mod's methods; every argument the handlers read is declared and every declared one is
/// read; literal integer bounds agree; reply views agree where declared; every error code is registered; one version.
/// Set STATIONGOD_WRITE_CATALOGUE=1 and run CatalogueFileIsCurrent to write catalogue.json after editing catalogue/.
/// </summary>
public sealed class CatalogueConsistencyTests
{
    private static readonly Lazy<JsonObject> Assembled = new Lazy<JsonObject>(() =>
        CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath).AsObject());

    // One instance: the schema library keeps every schema it builds by $id, and a second build of the same $id fails.
    private static readonly Lazy<JsonSchema> Schema = new Lazy<JsonSchema>(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(CatalogueFiles.SourceRoot, "catalogue.schema.json"))));

    private static IEnumerable<JsonObject> Methods() =>
        Assembled.Value["methods"]!.AsArray().Select(method => method!.AsObject());

    private static JsonObject Method(string name) => Methods().Single(method => (string)method["name"]! == name);

    // ---- 1. the file is current ----

    [Fact]
    public void CatalogueFileIsCurrent()
    {
        string expected = CatalogueFiles.Assemble();
        if (Environment.GetEnvironmentVariable("STATIONGOD_WRITE_CATALOGUE") == "1")
        {
            File.WriteAllText(CatalogueFiles.AssembledPath, expected, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(CatalogueFiles.AssembledPath) &&
                    File.ReadAllText(CatalogueFiles.AssembledPath) == expected,
            "catalogue.json differs from what catalogue/ assembles to: run CatalogueFileIsCurrent with STATIONGOD_WRITE_CATALOGUE=1.");
    }

    [Fact]
    public void CatalogueFileValidatesAgainstItsSchema()
    {
        JsonSchema schema = Schema.Value;
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(CatalogueFiles.AssembledPath));
        EvaluationResults results = schema.Evaluate(document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(results.IsValid, string.Join("\n", (results.Details ?? new List<EvaluationResults>())
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
            .Take(30)));
    }

    [Fact]
    public void TheSchemaRefusesAMethodWithoutItsReply()
    {
        JsonSchema schema = Schema.Value;
        JsonObject broken = CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath).AsObject();
        broken["methods"]![0]!.AsObject().Remove("reply");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(broken.ToJsonString());

        Assert.False(schema.Evaluate(document.RootElement).IsValid);
    }

    [Fact]
    public void SourcesKeepTheCatalogueLayout()
    {
        List<string> unformatted = new List<string>();
        foreach (string path in Directory.GetFiles(CatalogueFiles.SourceRoot, "*.json", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            string formatted = CatalogueFiles.Serialize(JsonNode.Parse(text)!);
            if (text == formatted)
            {
                continue;
            }

            if (Environment.GetEnvironmentVariable("STATIONGOD_WRITE_CATALOGUE") == "1")
            {
                File.WriteAllText(path, formatted, new UTF8Encoding(false));
            }
            else
            {
                unformatted.Add(Path.GetRelativePath(CatalogueFiles.SourceRoot, path));
            }
        }

        Assert.True(unformatted.Count == 0,
            "Not in the catalogue's layout (two-space indents, one line when it fits in 120 columns; STATIONGOD_WRITE_CATALOGUE=1 rewrites them): " +
            string.Join(", ", unformatted));
    }

    [Fact]
    public void TheHashIsTheSameInCSharpAndPython()
    {
        string csharp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(CatalogueFiles.AssembledPath))).ToLowerInvariant();
        System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("py",
            new[] { "-3.12", "-c", "import hashlib, sys; print(hashlib.sha256(open(sys.argv[1], 'rb').read()).hexdigest())",
                CatalogueFiles.AssembledPath })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string python;
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
            python = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No Python launcher on this machine: nothing to compare against.
            return;
        }

        Assert.Equal(csharp, python);
    }

    [Fact]
    public void TheModLoadsTheCatalogue()
    {
        ModCatalogue catalogue = ModCatalogue.Load(File.ReadAllText(CatalogueFiles.AssembledPath));

        Assert.Equal(Methods().Count(), catalogue.MethodCount);
    }

    // ---- 2. same methods ----

    [Fact]
    public void TheCatalogueListsExactlyTheModsMethods()
    {
        // sample_logic runs over many frames in the subscription lane (SubscriptionHub), not as an ApiHost handler.
        HashSet<string> catalogued = Methods().Where(method => (string?)method["x-runs-in"] != "sidecar")
            .Select(method => (string)method["name"]!)
            .Where(name => name != StationGodMCP.Protocol.SubscriptionHub.SampleLogicMethod)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> handled = Handler.All.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.True(catalogued.SetEquals(handled),
            $"Only in the catalogue: {string.Join(", ", catalogued.Except(handled))}. " +
            $"Only in ApiHost.Methods: {string.Join(", ", handled.Except(catalogued))}.");
        Assert.Empty(Methods().Where(method => (string?)method["x-runs-in"] == "sidecar"));
        Assert.Contains(Methods(), method => (string?)method["name"] == StationGodMCP.Protocol.SubscriptionHub.SampleLogicMethod);
        // The protocol methods are exactly those the protocol layer answers itself.
        Assert.Equal(StationGodMCP.Protocol.ProtocolMethods.Names.OrderBy(name => name, StringComparer.Ordinal),
            Assembled.Value["protocol_methods"]!.AsArray().Select(method => (string)method!["name"]!)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    // ---- 3. same arguments ----

    [Fact]
    public void EveryArgumentAHandlerReadsIsDeclared()
    {
        HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
        StringBuilder described = new StringBuilder();
        foreach (JsonObject method in Methods())
        {
            CatalogueFiles.CollectPropertyNames(method["params"]!, declared);
            CollectDescriptions(method["params"]!, described);
        }

        string prose = described.ToString();
        List<string> missing = new List<string>();
        foreach (SourceFile file in ModSource.Instance.Files.Where(file => file.Path.StartsWith("Api/", StringComparison.Ordinal)))
        {
            foreach (ArgRead read in ArgRead.In(file))
            {
                // Declared as a property somewhere, or (inside an object documented in prose) named in a description.
                if (!declared.Contains(read.Name) && !Regex.IsMatch(prose, $@"\b{Regex.Escape(read.Name)}\b"))
                {
                    missing.Add(read.ToString());
                }
            }
        }

        Assert.True(missing.Count == 0, "Read through Args but declared by no method's params: " + string.Join("; ", missing));
    }

    [Fact]
    public void EveryDeclaredArgumentIsRead()
    {
        List<string> unread = new List<string>();
        foreach (JsonObject method in Methods())
        {
            string name = (string)method["name"]!;
            if (!Handler.All.TryGetValue(name, out Handler? handler))
            {
                continue;
            }

            HashSet<string> read = handler.Files().SelectMany(ArgRead.In).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            JsonObject readBy = method["x-read-by"] as JsonObject ?? new JsonObject();
            foreach ((string argument, JsonNode? _) in method["params"]!["properties"]!.AsObject())
            {
                if (readBy[argument] is JsonValue file)
                {
                    if (!ModSource.Instance.File((string)file!).NamesLiteral(argument))
                    {
                        unread.Add($"{name}: x-read-by names {file} for {argument}, which does not name it");
                    }
                }
                else if (!read.Contains(argument))
                {
                    unread.Add($"{name}: {argument} is read nowhere in {string.Join(", ", handler.Files().Select(f => f.Path))}");
                }
            }

            foreach ((string argument, JsonNode? _) in readBy)
            {
                if (read.Contains(argument))
                {
                    unread.Add($"{name}: x-read-by lists {argument}, which its files read through Args");
                }

                if (method["params"]!["properties"]![argument] == null)
                {
                    unread.Add($"{name}: x-read-by lists {argument}, which params does not declare");
                }
            }
        }

        Assert.True(unread.Count == 0, string.Join("\n", unread));
    }

    // ---- 4. ranges agree ----

    [Fact]
    public void IntegerBoundsAgreeWithTheHandlers()
    {
        List<string> wrong = new List<string>();
        foreach ((string method, Handler handler) in Handler.All)
        {
            JsonObject properties = Method(method)["params"]!["properties"]!.AsObject();
            foreach ((string name, IntRange range) in IntRange.Expected(handler, _ => { }))
            {
                if (!(properties[name] is JsonObject property) || (string?)property["type"] != "integer")
                {
                    continue;
                }

                long? minimum = (long?)property["minimum"];
                long? maximum = (long?)property["maximum"];
                if (minimum != range.Minimum || maximum != range.Maximum)
                {
                    wrong.Add($"{method}.{name}: catalogue {minimum}..{maximum}, handler {range}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- 5. reply views agree ----

    [Fact]
    public void EveryMethodDescribesItsReplysTopLevel()
    {
        foreach (JsonObject method in Methods())
        {
            JsonObject reply = method["reply"]!.AsObject();
            Assert.Equal("object", (string?)reply["type"]);
            Assert.True(reply["properties"] is JsonObject { Count: > 0 }, $"{method["name"]}: reply lists no keys");
        }
    }

    [Fact]
    public void ReplyKeysAgreeWithTheDeclaredViews()
    {
        JsonArray shared = Assembled.Value["shared_reply_keys"]!.AsArray();
        List<string> wrong = new List<string>();
        foreach (JsonObject method in Methods().Where(method => method["x-views"] != null))
        {
            string name = (string)method["name"]!;
            List<Type> views = ViewTypes(name, method["x-views"]!.AsArray(), wrong);
            Dictionary<string, List<Type>> entryViews = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            foreach ((string list, JsonNode? listed) in method["x-entry-views"] as JsonObject ?? new JsonObject())
            {
                entryViews[list] = ViewTypes(name, listed!.AsArray(), wrong);
            }

            Dictionary<string, bool> fromViews = ViewShapes.KeysOf(views);
            foreach (JsonObject key in CatalogueFiles.SharedKeysFor(method, shared))
            {
                fromViews[(string)key["key"]!] = (string?)key["schema"]!["type"] == "array";
            }

            JsonObject reply = method["reply"]!["properties"]!.AsObject();
            AgreeKeys(name, fromViews, reply, wrong);
            AgreeEntries(name, string.Empty, views, reply, entryViews, wrong);
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- x-costly: what a handler may skip is a described entry key, and the handler asks about exactly those ----

    [Fact]
    public void CostlyPartsAreDescribedEntryKeysTheHandlerAsksAbout()
    {
        List<string> wrong = new List<string>();
        foreach (JsonObject method in Methods())
        {
            string name = (string)method["name"]!;
            HashSet<(string List, string Key)> declared = new HashSet<(string List, string Key)>();
            foreach (JsonNode? part in method["x-costly"] as JsonArray ?? new JsonArray())
            {
                string list = (string)part!["list"]!;
                string key = (string)part["key"]!;
                declared.Add((list, key));
                if (method["reply"]!["properties"]![list] is not JsonObject listSchema || !TypeNames(listSchema).Contains("array"))
                {
                    wrong.Add($"{name}: x-costly names {list}, which is not a list of its reply");
                }
                else if (listSchema["items"]?["properties"]?[key] == null)
                {
                    wrong.Add($"{name}: x-costly names {list}.{key}, which the list's entry schema does not describe");
                }
            }

            HashSet<(string List, string Key)> asked = Handler.All.TryGetValue(name, out Handler? handler)
                ? CostlyAsks.In(handler)
                : new HashSet<(string List, string Key)>();
            foreach ((string list, string key) in asked.Except(declared))
            {
                wrong.Add($"{name}: the handler asks Shape.Wants(\"{list}\", \"{key}\"), which x-costly does not declare");
            }

            foreach ((string list, string key) in declared.Except(asked))
            {
                wrong.Add($"{name}: x-costly declares {list}.{key}, which the handler never asks Shape.Wants about");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ThingHealthDeclaresItsNetworksCostly()
    {
        JsonArray costly = Method("thing_health")["x-costly"]!.AsArray();

        Assert.Equal(new[] { "results.networks", "things.networks" },
            costly.Select(part => $"{part!["list"]}.{part["key"]}").OrderBy(text => text, StringComparer.Ordinal));
    }

    private static void AgreeKeys(string where, Dictionary<string, bool> fromViews, JsonObject schema, List<string> wrong)
    {
        foreach ((string key, bool list) in fromViews)
        {
            if (schema[key] is not JsonObject property)
            {
                wrong.Add($"{where}: reply lacks {key}");
            }
            else if (list != TypeNames(property).Contains("array"))
            {
                wrong.Add($"{where}: {key} is {(list ? "" : "not ")}a list in the views but not in reply");
            }
        }

        foreach ((string key, JsonNode? _) in schema)
        {
            if (!fromViews.ContainsKey(key))
            {
                wrong.Add($"{where}: reply has {key}, which no view in x-views writes");
            }
        }
    }

    // Each list whose items describe their properties, held to its entry views, as deep as the items describe. A list
    // is named by its path (results, things[].networks); x-entry-views names the views of a list whose element type is
    // abstract (a batch's BatchItemView).
    private static void AgreeEntries(string method, string at, List<Type> views, JsonObject schema,
        Dictionary<string, List<Type>> entryViews, List<string> wrong)
    {
        foreach ((string key, JsonNode? node) in schema)
        {
            if (node?["items"]?["properties"] is not JsonObject entry)
            {
                continue;
            }

            string path = at.Length == 0 ? key : $"{at}[].{key}";
            List<Type> entries = ViewShapes.EntryTypes(views, key,
                entryViews.TryGetValue(path, out List<Type>? named) ? named : new List<Type>());
            string where = $"{method}: {path}[]";
            if (entries.Count == 0)
            {
                wrong.Add($"{where}: describes its entries, but no view writes them (an abstract entry type needs x-entry-views)");
                continue;
            }

            AgreeKeys(where, ViewShapes.KeysOf(entries), entry, wrong);
            AgreeEntries(method, path, entries, entry, entryViews, wrong);
        }
    }

    private static List<Type> ViewTypes(string method, JsonArray names, List<string> wrong)
    {
        List<Type> types = new List<Type>();
        foreach (JsonNode? view in names)
        {
            Type? type = ViewShapes.Find((string)view!);
            if (type == null)
            {
                wrong.Add($"{method}: no view class {view} in Api/Views or Api/Shared");
                continue;
            }

            types.Add(type);
        }

        return types;
    }

    // ---- 6. every error code is registered ----

    [Fact]
    public void EveryErrorCodeIsRegisteredAndEveryToolCodeUsed()
    {
        JsonObject errors = Assembled.Value["errors"]!.AsObject();
        List<ErrorSite> sites = ErrorSite.All();
        HashSet<string> used = sites.Select(site => site.Code).ToHashSet(StringComparer.Ordinal);

        List<string> unregistered = sites.Where(site => errors[site.Code] == null)
            .Select(site => $"{site.Code} ({site.Where})").Distinct().ToList();
        List<string> unused = errors.Where(entry => (string?)entry.Value!["origin"] == "tool" && !used.Contains(entry.Key))
            .Select(entry => entry.Key).ToList();

        Assert.True(unregistered.Count == 0, "Not in catalogue/errors.json: " + string.Join(", ", unregistered));
        Assert.True(unused.Count == 0, "In catalogue/errors.json as tool codes but used nowhere: " + string.Join(", ", unused));
    }

    // ---- 7. one version ----

    [Fact]
    public void TheCatalogueCarriesTheModsVersion()
    {
        string root = CatalogueFiles.RepositoryRoot();
        string mod = File.ReadAllText(Path.Combine(root, "src", "StationGodMCP.Mod", "StationGodMod.cs"));
        string about = File.ReadAllText(Path.Combine(root, "About", "About.xml"));

        Assert.Equal(CatalogueFiles.ModVersion(), (string?)Assembled.Value["mod_version"]);
        Assert.Equal(CatalogueFiles.ModVersion(), Regex.Match(mod, "const string Version = \"([^\"]+)\"").Groups[1].Value);
        Assert.Equal(CatalogueFiles.ModVersion(), Regex.Match(about, "<Version>([^<]+)</Version>").Groups[1].Value);
    }

    // ---- the x- fields name what exists ----

    [Fact]
    public void RulesPagingAndJobsNameDeclaredArgumentsAndReplyKeys()
    {
        List<string> wrong = new List<string>();
        foreach (JsonObject method in Methods())
        {
            string name = (string)method["name"]!;
            JsonObject parameters = method["params"]!["properties"]!.AsObject();
            JsonObject reply = method["reply"]!["properties"]!.AsObject();
            void Param(string? argument, string where)
            {
                if (argument != null && !parameters.ContainsKey(argument))
                {
                    wrong.Add($"{name}: {where} names {argument}, which params does not declare");
                }
            }

            void Key(string? key, string where)
            {
                if (key != null && !reply.ContainsKey(key))
                {
                    wrong.Add($"{name}: {where} names {key}, which reply does not list");
                }
            }

            foreach (string rules in new[] { "x-class-when", "x-cost-when" })
            {
                foreach (JsonNode? rule in method[rules] as JsonArray ?? new JsonArray())
                {
                    foreach ((string argument, JsonNode? _) in rule!["when"]!.AsObject())
                    {
                        Param(argument, rules);
                    }

                    Param((string?)rule["per_item"], rules + " per_item");
                }
            }

            if (method["x-paging"] is JsonObject paging)
            {
                Param((string?)paging["offset"], "x-paging offset");
                Param((string?)paging["limit"], "x-paging limit");
                Key((string?)paging["list"], "x-paging list");
                Key((string?)paging["total"], "x-paging total");
                Key((string?)paging["has_more"], "x-paging has_more");
            }

            Param((string?)method["x-job"]?["poll_param"], "x-job");
            Param((string?)method["x-duration"]?["param"], "x-duration");
            foreach ((string old, JsonNode? current) in method["deprecated_aliases"] as JsonObject ?? new JsonObject())
            {
                Param(old, "deprecated_aliases");
                Param((string?)current, "deprecated_aliases");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void AnUnsupportedSchemaKeywordFailsToLoadNamingIt()
    {
        JsonObject catalogue = CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath).AsObject();
        catalogue["methods"]![0]!["params"]!["properties"]!.AsObject()["bogus"] =
            new JsonObject { ["type"] = "string", ["format"] = "date-time" };

        Pure.Catalogue.CatalogueException refused = Assert.Throws<Pure.Catalogue.CatalogueException>(() =>
            ModCatalogue.Load(catalogue.ToJsonString()));

        Assert.Contains("'format'", refused.Message);
        Assert.Contains("params.properties.bogus", refused.Message);
        Assert.Contains("not in the subset the mod checks", refused.Message);
    }

    [Fact]
    public void AKeyGivenTwiceInTheCatalogueFailsToLoad()
    {
        Assert.Throws<Pure.Catalogue.CatalogueException>(() =>
            ModCatalogue.Load("""{"catalogue_version": 1, "catalogue_version": 1}"""));
    }

    private static HashSet<string> TypeNames(JsonObject schema) => schema["type"] switch
    {
        JsonArray list => list.Select(type => (string)type!).ToHashSet(),
        JsonValue single => new HashSet<string> { (string)single! },
        _ => new HashSet<string>()
    };

    private static void CollectDescriptions(JsonNode node, StringBuilder into)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (key == "description" && value is JsonValue text)
                {
                    into.Append((string)text!).Append('\n');
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
}
