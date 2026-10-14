#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>
/// Where an error code's help is: the verb and what (build finish), or a shared topic and subtopic (positions), that
/// `help` answers. catalogue/verbs/_errors.json gives each code one or the other; every error the mod answers
/// carries the pointer its code has there.
/// </summary>
internal sealed class ErrorSee
{
    internal ErrorSee(string? verb, string? what, string? topic, string? subtopic)
    {
        Verb = verb;
        What = what;
        Topic = topic;
        Subtopic = subtopic;
    }

    /// <summary>The verb whose help explains the code; null when the pointer names a topic instead.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Verb { get; }

    /// <summary>The what of that verb; null for a verb's own help.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? What { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Topic { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Subtopic { get; }
}

/// <summary>
/// The verb catalogue's error pointers, by code, for the whole process: set once when the mod loads its document,
/// read by every error view. A code the document does not register has none.
/// </summary>
internal static class ErrorGuide
{
    private static volatile Dictionary<string, ErrorSee> _pointers = new Dictionary<string, ErrorSee>(StringComparer.Ordinal);

    /// <summary>Takes the pointers of a loaded verb document.</summary>
    internal static void Use(VerbDocument document) => _pointers = document.Errors;

    /// <summary>Takes the pointers themselves.</summary>
    internal static void Use(Dictionary<string, ErrorSee> pointers) => _pointers = pointers;

    /// <summary>The code's pointer, or null for a code the document gives none.</summary>
    internal static ErrorSee? SeeOf(string code) => _pointers.TryGetValue(code, out ErrorSee? see) ? see : null;
}
