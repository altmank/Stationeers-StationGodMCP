#nullable enable

using System.Text.RegularExpressions;

namespace StationGodMCP.Pure;

/// <summary>
/// The name a message shows for a thing. The game's DisplayName is its label, else the prefab's localised name; a
/// prefab with no entry in the language file comes back as the game's placeholder "&lt;N:EN:PrefabName&gt;"
/// (Localization.ErrorName "&lt;N:{0}:{1}&gt;"), which reads as noise, so the prefab name stands in for it.
/// </summary>
internal static class ThingName
{
    /// <summary>
    /// The game's placeholder: "&lt;N:{language}:{key}&gt;", the key being what the language file lacks (a thing's
    /// prefab name, a slot's key, a reagent's type).
    /// </summary>
    private static readonly Regex Placeholder = new Regex("<N:[^:<>]+:([^<>]+)>", RegexOptions.Compiled);

    private static readonly Regex WholePlaceholder = new Regex("^<N:[^:<>]+:[^<>]+>$", RegexOptions.Compiled);

    internal static string Shown(string? displayName, string prefabName) =>
        string.IsNullOrEmpty(displayName) || IsPlaceholder(displayName!) ? prefabName : Resolved(displayName!);

    /// <summary>
    /// A display_name field: the game's DisplayName, the prefab name where the whole name is the placeholder, and
    /// every placeholder inside a longer name (a plant's "&lt;N:EN:...&gt; 3") replaced by its key; null stays null.
    /// </summary>
    internal static string? Displayed(string? displayName, string? prefabName) =>
        displayName == null ? null
        : IsPlaceholder(displayName) && !string.IsNullOrEmpty(prefabName) ? prefabName
        : Resolved(displayName);

    /// <summary>
    /// A game text with every placeholder replaced by its key, so "Placement is blocked by &lt;N:EN:StructureX&gt;"
    /// names StructureX. Runs before rich-text tags are stripped, which would take the placeholder for a tag.
    /// </summary>
    internal static string Resolved(string text) => Placeholder.Replace(text, "$1");

    /// <summary>The whole name is the game's placeholder for a name the language file does not have.</summary>
    internal static bool IsPlaceholder(string name) => WholePlaceholder.IsMatch(name);
}
