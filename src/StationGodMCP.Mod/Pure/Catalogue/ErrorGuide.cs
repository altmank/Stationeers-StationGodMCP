#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>
/// Where an error code's help is: a node tool_info answers, {tool, topic, subtopic} with only topic required (a shared
/// topic when tool is absent). Every error the mod answers carries the pointer its code has in the catalogue.
/// </summary>
internal sealed class ErrorSee
{
    internal ErrorSee(string? tool, string topic, string? subtopic)
    {
        Tool = tool;
        Topic = topic;
        Subtopic = subtopic;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Tool { get; }

    public string Topic { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Subtopic { get; }
}

/// <summary>
/// The catalogue's error pointers, by code, for the whole process: set once when the mod loads its catalogue, read by
/// every error view. A code the catalogue does not register has none.
/// </summary>
internal static class ErrorGuide
{
    private static volatile Dictionary<string, ErrorSee> _pointers = new Dictionary<string, ErrorSee>(StringComparer.Ordinal);

    /// <summary>Takes the pointers of a loaded catalogue.</summary>
    internal static void Use(Catalogue catalogue) => _pointers = catalogue.ErrorPointers;

    /// <summary>The code's pointer, or null for a code the catalogue gives none.</summary>
    internal static ErrorSee? SeeOf(string code) => _pointers.TryGetValue(code, out ErrorSee see) ? see : null;
}
