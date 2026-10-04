#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// How a build takes its materials from the stacks a source holds: exactly the quantity asked, stack by stack in the
/// order given, each stack giving what is still owed or all it holds, whichever is less, so only the last stack used
/// is split and the rest of it stays where it is (Stackable.OnUseItem takes the part off and removes a stack only when
/// it is used up).
/// </summary>
internal static class StockTake
{
    /// <summary>What one stack holding this many gives while this many are still owed.</summary>
    internal static int PartOf(int owed, int held) => Math.Max(Math.Min(owed, held), 0);

    /// <summary>Whether a stack lost more than the part taken from it: a build never uses up more than it asked.</summary>
    internal static bool TookTooMuch(int part, int before, int after) => before - Math.Max(after, 0) > part;
}
