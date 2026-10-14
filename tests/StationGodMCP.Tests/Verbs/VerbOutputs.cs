#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using StationGodMCP.Tests.CatalogueChecks;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// Every file generated from catalogue/verbs, under generated/verbs: tools.json (the MCP tools array plus
/// instructions), help/ (the direct pages and topics), stubs/ (the Python stub per what) and sizes.json; and the mod's
/// typed options, under src/StationGodMCP.Mod/Pure/Verbs/Generated (VerbOptionsCode). Set STATIONGOD_WRITE_CATALOGUE=1
/// and run VerbCatalogueTests.GeneratedFilesAreCurrent to rewrite them.
/// </summary>
internal static class VerbOutputs
{
    internal static string Root => Path.Combine(CatalogueFiles.RepositoryRoot(), "generated", "verbs");

    /// <summary>Where the mod's generated options classes go.</summary>
    internal static string ModRoot =>
        Path.Combine(CatalogueFiles.RepositoryRoot(), "src", "StationGodMCP.Mod", "Pure", "Verbs", "Generated");

    /// <summary>The generated files, by path under Root (forward slashes), with their text.</summary>
    internal static SortedDictionary<string, string> All()
    {
        SortedDictionary<string, string> files = new(StringComparer.Ordinal);
        JsonObject tools = VerbToolList.Build();
        files["tools.json"] = CatalogueFiles.Serialize(VerbText.Parsed(tools));
        files["sizes.json"] = CatalogueFiles.Serialize(VerbText.Parsed(VerbSizes.Table(tools)));
        files["help/_index.txt"] = VerbHelp.Index();
        foreach (string topic in VerbHelp.TopicKeys())
        {
            files["help/topic." + topic + ".txt"] = VerbHelp.Topic(topic);
        }

        foreach (JsonObject verb in VerbCatalogue.Verbs)
        {
            string name = VerbCatalogue.NameOf(verb);
            files["help/" + name + ".txt"] = VerbHelp.VerbPage(verb);
            foreach ((string what, JsonObject _) in VerbCatalogue.WhatsOf(verb))
            {
                bool whatless = what == VerbCatalogue.WhatlessKey;
                if (!whatless)
                {
                    files["help/" + name + "." + what + ".txt"] = VerbHelp.WhatPage(verb, what);
                }

                // A hub-only verb (run_script) has no stub: sg has no function for it (help is sg.help).
                if (!VerbCatalogue.IsHubOnly(verb))
                {
                    files["stubs/" + name + (whatless ? "" : "." + what) + ".py"] = VerbStubs.Stub(verb, what);
                }
            }
        }

        return files;
    }

    internal static void Write(SortedDictionary<string, string> files, string? root = null)
    {
        root ??= Root;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        foreach ((string path, string text) in files)
        {
            CatalogueFiles.WriteText(Path.Combine(root, path), text);
        }
    }

    /// <summary>The paths whose file on disk is missing or differs, and files on disk nothing generates.</summary>
    internal static List<string> Stale(SortedDictionary<string, string> files, string? root = null)
    {
        root ??= Root;
        List<string> stale = new();
        foreach ((string path, string text) in files)
        {
            string file = Path.Combine(root, path);
            if (!File.Exists(file) || File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal) != text)
            {
                stale.Add(path);
            }
        }

        if (Directory.Exists(root))
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string path = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!files.ContainsKey(path))
                {
                    stale.Add(path + " (not generated)");
                }
            }
        }

        return stale;
    }
}
