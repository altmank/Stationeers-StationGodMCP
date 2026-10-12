#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The top-level argument names each method takes (the catalogue's params), so a pipe client is held to them as the
/// sidecar holds an MCP client: an argument the method does not take is invalid_argument, naming the nearest one it
/// does take and every one it takes, before the method runs. Without the check a misspelt filter (prefab for
/// prefab_contains) was dropped and the call answered the whole world. Null is an omitted argument. Nested objects
/// are left to each method. A method the catalogue does not list is not checked. The refusal names arguments and the
/// method as the call's rename map says (Vocabulary).
/// </summary>
internal sealed class DeclaredArguments
{
    private readonly Catalogue _catalogue;

    internal DeclaredArguments(Catalogue catalogue)
    {
        _catalogue = catalogue;
    }

    internal int ToolCount => _catalogue.MethodCount;

    /// <summary>The method's declared names, or null when the catalogue does not list it.</summary>
    internal ArgumentNames? NamesOf(string method) =>
        _catalogue.TryGet(method, out CatalogueMethod? found) ? found.ArgumentNames : null;

    /// <summary>Whether the catalogue marks the method as answered by the MCP server, not the mod.</summary>
    internal bool RunsInSidecar(string method) =>
        _catalogue.TryGet(method, out CatalogueMethod? found) && found.RunsInSidecar;

    /// <summary>
    /// Refuses (invalid_argument) a call that gives an argument its method does not take, unless its connection's
    /// reader thread already refused every such name (CallRequest.ArgumentNamesChecked).
    /// </summary>
    internal void Check(CallRequest call)
    {
        if (!call.ArgumentNamesChecked)
        {
            Check(call.Method, call.Params);
        }
    }

    /// <summary>Refuses (invalid_argument) a request that gives an argument its method does not take.</summary>
    internal void Check(string method, JObject? parameters)
    {
        ArgumentNames? names = NamesOf(method);
        if (parameters == null || names == null)
        {
            return;
        }

        StringBuilder? problems = null;
        foreach (JProperty property in parameters.Properties())
        {
            if (property.Value.Type == JTokenType.Null || names.Contains(property.Name))
            {
                continue;
            }

            problems ??= new StringBuilder();
            if (_catalogue.TryGet(method, out CatalogueMethod? declared) && declared.ServerOnlyArgument(property.Name) is string note)
            {
                problems.Append(note).Append(' ');
                continue;
            }

            string? nearest = NearestName.Of(property.Name, names.Sorted);
            problems.Append(nearest != null
                ? $"Unknown argument '{Vocabulary.Arg(property.Name)}'; did you mean '{Vocabulary.Arg(nearest)}'? "
                : $"Unknown argument '{Vocabulary.Arg(property.Name)}'. ");
        }

        if (problems != null)
        {
            string list = names.Sorted.Count == 0 ? "none" : Listed(names);
            throw ApiErrors.InvalidArgument($"{problems}{Vocabulary.Tool(method)} takes: {list}. Nothing was run.");
        }
    }

    // The names as the caller says them, each phrase once (a map may say min and max both as box).
    private static string Listed(ArgumentNames names)
    {
        if (Vocabulary.Current == null)
        {
            return string.Join(", ", names.Sorted);
        }

        List<string> said = new List<string>(names.Sorted.Count);
        foreach (string name in names.Sorted)
        {
            string phrase = Vocabulary.Arg(name);
            if (!said.Contains(phrase))
            {
                said.Add(phrase);
            }
        }

        return string.Join(", ", said);
    }
}
