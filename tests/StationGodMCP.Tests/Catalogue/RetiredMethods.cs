#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The method catalogue's entries (catalogue/methods) whose game handler is gone because the game answers that work as
/// a verb. The MCP server and the client still read the method catalogue, so it keeps them; the checks that hold the
/// method catalogue to the game's handlers and reply views pass over them.
/// </summary>
internal static class RetiredMethods
{
    internal static IReadOnlyCollection<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "find_things"
    };

    internal static bool Has(string? name) => name != null && ((HashSet<string>)Names).Contains(name);
}
