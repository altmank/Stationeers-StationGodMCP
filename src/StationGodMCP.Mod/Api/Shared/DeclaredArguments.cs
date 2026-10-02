#nullable enable

using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The top-level argument names each method takes (the catalogue's params), so a pipe client is held to them as the
/// sidecar holds an MCP client: an argument the method does not take is invalid_argument, naming the nearest one it
/// does take and every one it takes, before the method runs. Without the check a misspelt filter (prefab for
/// prefab_contains) was dropped and the call answered the whole world. Null is an omitted argument. Nested objects
/// are left to each method. A method the catalogue does not list is not checked.
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
            string? nearest = NearestName.Of(property.Name, names.Sorted);
            problems.Append(nearest != null
                ? $"Unknown argument '{property.Name}'; did you mean '{nearest}'? "
                : $"Unknown argument '{property.Name}'. ");
        }

        if (problems != null)
        {
            string list = names.Sorted.Count == 0 ? "none" : string.Join(", ", names.Sorted);
            throw ApiErrors.InvalidArgument($"{problems}{method} takes: {list}. Nothing was run.");
        }
    }
}
