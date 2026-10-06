#nullable enable

using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>A list's prefab and prefab_contains arguments (Pure/PrefabMatch).</summary>
internal static class PrefabMatches
{
    internal static PrefabMatch Parse(Args args) =>
        new PrefabMatch(args.OptionalString("prefab")?.Trim(), args.OptionalString("prefab_contains"));
}
