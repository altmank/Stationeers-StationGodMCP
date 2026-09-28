#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Why the label tool (and place_structure's label) refuses a thing whose class the hand Labeller cannot rename. The
/// pipe-size in-line tanks (class InLineTank: StructureInLineTankGas1x1 and the rest, insulated too) get their own
/// reason, since the big in-line tanks (StructureInLineTank) do take a label and the two look alike in game.
/// StationGod keeps no names of its own: a name the game would not show, save or sync is not a label.
/// </summary>
internal static class LabelRule
{
    internal const string InLineTankClass = "InLineTank";

    /// <summary>The refusal for subject (a thing with its id, or a prefab name) of the given class.</summary>
    internal static string NotLabelable(string subject, string className) =>
        className == InLineTankClass
            ? $"{subject} is a pipe-size in-line tank (InLineTank): the game's Labeller has no rename for that " +
              "class, so it takes no label, and StationGod keeps no names of its own. The big in-line tanks " +
              "(StructureInLineTank) take one; to name this tank, label a sign or a device beside it."
            : $"{subject} is a {className}; the Labeller cannot rename that class (pipes, cables, frames and " +
              "ordinary items take no label).";
}
