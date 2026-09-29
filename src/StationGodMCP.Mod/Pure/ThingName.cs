#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// The name a message shows for a thing. The game's DisplayName is its label, else the prefab's localised name; a
/// prefab with no entry in the language file comes back as the game's placeholder "&lt;N:EN:PrefabName&gt;"
/// (Localization.ErrorName "&lt;N:{0}:{1}&gt;"), which reads as noise, so the prefab name stands in for it.
/// </summary>
internal static class ThingName
{
    internal static string Shown(string? displayName, string prefabName) =>
        string.IsNullOrEmpty(displayName) || IsPlaceholder(displayName!) ? prefabName : displayName!;

    /// <summary>The game's placeholder for a name the language file does not have: "&lt;N:{language}:{key}&gt;".</summary>
    internal static bool IsPlaceholder(string name) =>
        name.StartsWith("<N:", StringComparison.Ordinal) && name.EndsWith(">", StringComparison.Ordinal);
}
