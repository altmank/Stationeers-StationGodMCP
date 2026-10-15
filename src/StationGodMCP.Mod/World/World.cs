#nullable enable

namespace StationGodMCP.World;

/// <summary>
/// The world model (cost design A1): the one module that reads the game's global collections (the list is
/// tests/StationGodMCP.Tests/CostRules/GameCollections.txt). It will keep indexes by class and by prefab, updated by
/// hooks on the game's add and remove paths, and calls will ask it instead of copying the game's lists. Empty until
/// the cost build's step 2; the source rules (CostSourceRulesTests) already count every read outside this folder.
/// </summary>
internal static class World
{
}
