#nullable enable

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// What the verbs' tool list costs, in Claude tokens, by one tokenizer calibrated once against Claude Code
/// (tools/tool_list_cost.py: a one-turn Opus run with every schema loaded minus one with no MCP server, both with
/// ENABLE_TOOL_SEARCH=false). The tokenizer counts pieces of the compact JSON (a run of letters, a run of digits, a run
/// of spaces, or any other single character) and multiplies by TokensPerPiece, fitted on today's 104-tool list (89,000
/// tokens measured, 97,672 pieces) and the served probe's verb list (24,339 tokens, 27,498 pieces): error +0.6 % and
/// -2.3 %. On the probe's runs it also predicts what each run's ToolSearch loads added to the context (ratio 1.00).
/// </summary>
internal static class VerbSizes
{
    internal const double TokensPerPiece = 0.9055;

    /// <summary>Today's full tools/list plus instructions (104 tools), measured with the same method.</summary>
    internal const int TodayFullTokens = 89_000;

    /// <summary>
    /// Today per fresh context on the test bed's 34 tasks (phase 0 baseline, 80 runs, deferred loading): the deferred
    /// listing (1,832, measured) plus the median context the run's ToolSearch loads added (3,315, RED-8-03's method,
    /// red8_toolsearch.py; Opus alone 3,447).
    /// </summary>
    internal const int TodayPerFreshContext = 1_832 + 3_315;

    /// <summary>The verbs' deferred listing (17 tool names plus the instructions), measured on the generated list.</summary>
    internal const int VerbListingTokens = 646;

    internal const double FullListShareMax = 0.25;

    internal const int PerVerbMax = 1_500;

    /// <summary>The verbs a run loads at the median (the probe: find 46, help 44, inspect 36, read 28, write 25 of 46 runs).</summary>
    internal static readonly string[] CommonVerbs = { "find", "help", "inspect", "read", "write" };

    private static readonly Regex Piece = new(@"[A-Za-z]+|\d+|\s+|[^\sA-Za-z\d]", RegexOptions.Compiled);

    internal static int Tokens(string text) => (int)Math.Round(Piece.Matches(text).Count * TokensPerPiece);

    internal static int ToolTokens(JsonNode tool) => Tokens(tool.ToJsonString(VerbText.Compact));

    internal static int FullTokens(JsonObject list) =>
        Tokens(list["tools"]!.ToJsonString(VerbText.Compact) + (string)list["instructions"]!);

    internal static int CommonLoadTokens(JsonObject list) =>
        list["tools"]!.AsArray().Where(tool => CommonVerbs.Contains((string)tool!["name"]!)).Sum(tool => ToolTokens(tool!));

    internal static int ScriptLoadTokens(JsonObject list) =>
        ToolTokens(list["tools"]!.AsArray().Single(tool => (string)tool!["name"]! == "run_script")!);

    /// <summary>The size table: the whole list and each tool, against today and the targets.</summary>
    internal static JsonObject Table(JsonObject list)
    {
        JsonObject perTool = new();
        foreach (JsonNode? tool in list["tools"]!.AsArray())
        {
            perTool[(string)tool!["name"]!] = ToolTokens(tool);
        }

        int full = FullTokens(list);
        int common = CommonLoadTokens(list);
        return new JsonObject
        {
            ["method"] = "pieces x " + TokensPerPiece.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                         ", calibrated on Claude Code all-loaded minus no-MCP runs (ENABLE_TOOL_SEARCH=false)",
            ["full_list"] = new JsonObject
            {
                ["today"] = TodayFullTokens,
                ["verbs"] = full,
                ["share"] = Math.Round((double)full / TodayFullTokens, 3),
                ["target_share"] = FullListShareMax
            },
            ["per_fresh_context"] = new JsonObject
            {
                ["today"] = TodayPerFreshContext,
                ["verbs_listing"] = VerbListingTokens,
                ["verbs_common_load"] = common,
                ["verbs"] = VerbListingTokens + common,
                ["common_verbs"] = new JsonArray(CommonVerbs.Select(verb => (JsonNode)JsonValue.Create(verb)!).ToArray())
            },
            // run_script loads only on script tasks (waits, polls, timed or repeated work): its own row, added to the
            // common load for such a task
            ["script_task"] = new JsonObject
            {
                ["run_script_load"] = ScriptLoadTokens(list),
                ["verbs"] = VerbListingTokens + common + ScriptLoadTokens(list)
            },
            ["instructions"] = Tokens((string)list["instructions"]!),
            ["per_tool_max"] = PerVerbMax,
            ["per_tool"] = perTool
        };
    }
}
