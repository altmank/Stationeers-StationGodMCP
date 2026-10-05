#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// How one held item pays toward a build state's entry, by the game's rule (Structure.HandleToolUse): a stack pays up
/// to its quantity and keeps the rest (Stackable.OnUseItem); any other item (a printer mod, a circuit board) pays as
/// one whole unit and is destroyed when used (OnServer.Destroy of the entry item).
/// </summary>
internal abstract class HeldMaterial
{
    /// <summary>How many units of the entry the item can pay.</summary>
    internal abstract int Units { get; }

    /// <summary>What the item gives while this many are still owed.</summary>
    internal int PartOf(int owed) => StockTake.PartOf(owed, Units);

    internal static HeldMaterial Stack(int quantity) => new StackOf(quantity);

    internal static HeldMaterial Whole { get; } = new WholeItem();

    private sealed class StackOf(int quantity) : HeldMaterial
    {
        internal override int Units => quantity;
    }

    private sealed class WholeItem : HeldMaterial
    {
        internal override int Units => 1;
    }
}
