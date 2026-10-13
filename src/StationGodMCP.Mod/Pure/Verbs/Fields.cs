#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// What the reply holds (PLAN 2.0, shaping): fields keeps keys in each entry, omit leaves key paths out, list_limits
/// keeps more of a list cut by default (all three the call's shape), include adds parts, verbose gives the long form.
/// </summary>
internal sealed record Fields(ShapeRequest? Shape, IReadOnlyList<string> Include, bool Verbose)
{
    internal static Fields None { get; } = new Fields(null, Array.Empty<string>(), false);

    internal static Fields Bind(OptionReader options) =>
        new Fields(options.Shape, options.Words("include") ?? Array.Empty<string>(), options.Flag("verbose") ?? false);

    /// <summary>True when include names the part, in any case.</summary>
    internal bool Includes(string part)
    {
        foreach (string named in Include)
        {
            if (string.Equals(named.Trim(), part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The call's shape: the wire shape with the options fields, omit and list_limits (the shape's limit) put in it, an
    /// option replacing the wire shape's key. Null when neither gives one; problems get what cannot be used.
    /// </summary>
    internal static ShapeRequest? ShapeOf(JToken? wireShape, JObject? parameters, ICollection<string>? replyLists,
        List<string> problems)
    {
        JToken? fields = Given(parameters, "fields");
        JToken? omit = Given(parameters, "omit");
        JToken? limits = Given(parameters, "list_limits");
        if (fields == null && omit == null && limits == null)
        {
            return ShapeRequest.Strict(wireShape, replyLists, problems);
        }

        JObject shape = wireShape is JObject given ? (JObject)given.DeepClone() : new JObject();
        if (fields != null)
        {
            shape["fields"] = fields.DeepClone();
        }

        if (omit != null)
        {
            shape["omit"] = omit.DeepClone();
        }

        if (limits != null)
        {
            shape["limit"] = limits.DeepClone();
        }

        return ShapeRequest.Strict(shape, replyLists, problems);
    }

    private static JToken? Given(JObject? parameters, string name) =>
        parameters?[name] is { Type: not JTokenType.Null } value ? value : null;
}
