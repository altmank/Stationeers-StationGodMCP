#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Tests.Verbs;

/// <summary>Verb handler tables for tests: the mod's own is empty until a phase builds a verb.</summary>
internal static class TestVerbs
{
    /// <summary>
    /// A handler for every (verb, what), answering {verb, what, options}: the wire rigs' table, so any verb call is
    /// admitted and reaches the fake main thread, which answers it itself.
    /// </summary>
    internal static VerbDispatcher Every { get; } =
        new(VerbSurface.Verbs.SelectMany(verb => verb.Whats).Select(Echo));

    /// <summary>A table holding only these handlers.</summary>
    internal static VerbDispatcher Only(params VerbHandler[] handlers) => new(handlers);

    /// <summary>A handler that answers the what and the options it was given, as given.</summary>
    internal static VerbHandler Echo(WhatSpec what) =>
        VerbHandler.For(what, options => options, options => new VerbOutcome.Answered(new JObject
        {
            ["verb"] = what.Verb,
            ["what"] = what.Name,
            ["options"] = new JObject(what.OptionNames.Where(options.Has)
                .Select(name => new JProperty(name, options.Raw(name)!.DeepClone())))
        }));

    /// <summary>
    /// The params of read {what: "clock"}, the wire tests' plain call (it takes no options), with more keys put in for
    /// the fake main thread (hold_ms, reply_bytes) on rigs that do not check options on the reader thread.
    /// </summary>
    internal static JObject Clock(JObject? more = null)
    {
        JObject parameters = new() { ["what"] = "clock" };
        foreach (JProperty key in more?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            parameters[key.Name] = key.Value.DeepClone();
        }

        return parameters;
    }

    internal static IReadOnlyList<string> Names(JToken? list) =>
        list is JArray array ? array.Select(entry => (string)entry!).ToList() : new List<string>();
}
