using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The help verb, answered from the generated pages: help {} the index, {verb} a verb's whats, {verb, what} one what in
/// full, {topic} and {topic, subtopic} a shared topic (types takes a type name); lang: "python" answers the what's stub
/// for a script instead.
/// </summary>
internal static class HelpPages
{
    internal const string NotFound = "help_not_found";

    internal static VerbReply Answer(VerbCatalogue catalogue, JsonObject options)
    {
        string? verbName = Word(options, "verb");
        string? whatName = Word(options, "what");
        string? topic = Word(options, "topic");
        string? subtopic = Word(options, "subtopic");
        bool python = Word(options, "lang") == "python";
        JsonObject reply = new();

        if (topic != null)
        {
            string key = "topic." + topic + (subtopic == null ? string.Empty : "." + subtopic);
            if (catalogue.HelpPage(key) is not { } page)
            {
                string asked = subtopic == null ? topic : topic + "." + subtopic;
                return Missing($"No help topic '{asked}'", Nearest.Of(asked, catalogue.Topics));
            }

            reply["topic"] = topic;
            if (subtopic != null)
            {
                reply["subtopic"] = subtopic;
            }

            reply["text"] = page;
            return new VerbReply.Done(reply);
        }

        if (verbName == null)
        {
            if (python || whatName != null)
            {
                return Missing("Name a verb: help {verb, what}", null);
            }

            reply["text"] = catalogue.HelpPage("_index");
            return new VerbReply.Done(reply);
        }

        VerbEntry? verb = catalogue.Verb(verbName);
        if (verb == null)
        {
            return Missing($"No verb '{verbName}'", Nearest.Of(verbName, catalogue.VerbNames));
        }

        reply["verb"] = verb.Name;
        string pageKey = verb.Name;
        if (whatName != null && verb.HasWhats)
        {
            if (verb.What(whatName) is not { } what)
            {
                return Missing($"No what '{whatName}' for {verb.Name}", Nearest.Of(whatName, verb.WhatNames));
            }

            reply["what"] = what.Name;
            pageKey = what.Key;
        }

        if (python)
        {
            if (verb.HasWhats && whatName == null)
            {
                return Missing($"lang: \"python\" gives one what's stub: help {{verb: \"{verb.Name}\", what, lang: \"python\"}}",
                    null);
            }

            reply["lang"] = "python";
            reply["text"] = catalogue.Stub(pageKey) ?? string.Empty;
            return catalogue.Stub(pageKey) == null
                ? Missing($"No Python stub for {pageKey} (run_script is not called from a script)", null)
                : new VerbReply.Done(reply);
        }

        reply["text"] = catalogue.HelpPage(pageKey);
        return new VerbReply.Done(reply);
    }

    private static string? Word(JsonObject options, string name) => JsonNodes.Text(options[name])?.Trim() is { Length: > 0 } word
        ? word
        : null;

    private static VerbReply.Failed Missing(string message, List<string>? nearest)
    {
        JsonObject? data = null;
        if (nearest is { Count: > 0 })
        {
            JsonArray names = [];
            foreach (string name in nearest)
            {
                names.Add(name);
            }

            data = new JsonObject { ["nearest"] = names };
            message += " (nearest: " + string.Join(", ", nearest) + ")";
        }

        return new VerbReply.Failed(NotFound, message + ".", data: data);
    }
}
